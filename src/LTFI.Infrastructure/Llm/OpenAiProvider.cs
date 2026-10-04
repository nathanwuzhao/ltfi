using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LTFI.Core.Abstractions;

namespace LTFI.Infrastructure.Llm;

/// <summary>
/// OpenAI Responses API (<c>POST {BaseUrl}/responses</c>) over plain <see cref="HttpClient"/> +
/// System.Text.Json — one POST, no SDK dependency, and any OpenAI-compatible endpoint works.
/// Requests use <c>store:false</c> and, when a schema is given, strict <c>json_schema</c> output.
/// Retries once on 429/5xx. The API key is read per call (so a newly saved key works without a
/// restart) and is never logged or included in error messages.
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly LlmSettings _settings;
    private readonly IApiKeyStore _keys;
    private readonly TimeSpan _retryDelay;

    public OpenAiProvider(HttpClient http, LlmSettings settings, IApiKeyStore keys, TimeSpan? retryDelay = null)
    {
        _http = http;
        _settings = settings;
        _keys = keys;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds));
    }

    public bool IsConfigured => _settings.Enabled && !string.IsNullOrWhiteSpace(_keys.GetKey());

    public string Model => _settings.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            throw new LlmException(LlmFailureKind.NotConfigured, "The LLM coach is disabled in llm-settings.json.");

        var key = _keys.GetKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmException(LlmFailureKind.NotConfigured,
                $"OpenAI key not configured — set {DpapiApiKeyStore.DefaultEnvVar} or save a key in LTFI.");

        var body = BuildRequestBody(request, _settings).ToJsonString();
        var url = _settings.BaseUrl.TrimEnd('/') + "/responses";

        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmException(LlmFailureKind.Transport, "The coach timed out. Your data is unchanged — try again.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException(LlmFailureKind.Transport, $"Couldn't reach the LLM endpoint ({ex.Message}).", ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return ParseResponse(text, _settings.Model);
                }

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (transient && attempt == 1)
                {
                    await Task.Delay(_retryDelay, cancellationToken);
                    continue;
                }

                throw new LlmException(LlmFailureKind.Transport,
                    $"LLM request failed: HTTP {(int)response.StatusCode}. {Scrub(ExtractErrorMessage(text), key)}".Trim());
            }
        }
    }

    /// <summary>Builds the Responses API request JSON. Public for tests (no network involved).</summary>
    public static JsonObject BuildRequestBody(LlmRequest request, LlmSettings settings)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["store"] = false,
            ["max_output_tokens"] = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : settings.MaxOutputTokens,
            ["input"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserContent }
            }
        };

        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = settings.ReasoningEffort };
        }

        if (request.Schema is { } schema)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = schema.Name,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(schema.SchemaJson)
                }
            };
        }

        return body;
    }

    /// <summary>
    /// Walks a raw Responses API payload: <c>output[]</c> → item of type <c>message</c> →
    /// <c>content[]</c> → <c>output_text</c>. Refusals and <c>status:"incomplete"</c> become
    /// <see cref="LlmException"/>s. Public for tests.
    /// </summary>
    public static LlmResponse ParseResponse(string json, string fallbackModel)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmFailureKind.MalformedOutput, "The LLM endpoint returned something that isn't JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : fallbackModel;

            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                && status.GetString() == "incomplete")
            {
                var reason = root.TryGetProperty("incomplete_details", out var d) && d.ValueKind == JsonValueKind.Object
                             && d.TryGetProperty("reason", out var r) ? r.GetString() : null;
                throw new LlmException(LlmFailureKind.Incomplete,
                    $"The coach's answer was cut off{(reason is null ? "" : $" ({reason})")}. Try again.");
            }

            var sb = new StringBuilder();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (!IsType(item, "message") || !item.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var part in content.EnumerateArray())
                    {
                        if (IsType(part, "refusal"))
                        {
                            var why = part.TryGetProperty("refusal", out var rf) ? rf.GetString() : null;
                            throw new LlmException(LlmFailureKind.Refused, $"The model declined to answer. {why}".Trim());
                        }
                        if (IsType(part, "output_text") && part.TryGetProperty("text", out var t))
                        {
                            sb.Append(t.GetString());
                        }
                    }
                }
            }

            if (sb.Length == 0)
                throw new LlmException(LlmFailureKind.MalformedOutput, "The LLM response had no text output.");

            var usage = new LlmUsage(0, 0);
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new LlmUsage(ReadInt(u, "input_tokens"), ReadInt(u, "output_tokens"));
            }

            return new LlmResponse(sb.ToString(), model, usage);
        }
    }

    private static bool IsType(JsonElement e, string type) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t)
        && t.ValueKind == JsonValueKind.String && t.GetString() == type;

    private static int ReadInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (proxy page etc.) — don't echo it.
        }
        return string.Empty;
    }

    private static string Scrub(string text, string key) =>
        string.IsNullOrEmpty(key) ? text : text.Replace(key, "***", StringComparison.Ordinal);
}
