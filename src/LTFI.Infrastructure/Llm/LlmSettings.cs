using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTFI.Infrastructure.Llm;

/// <summary>
/// LLM configuration, read from <c>%AppData%/LTFI/llm-settings.json</c> when present (no DB row,
/// no migration). Every field is optional in the file; missing ones fall back to the defaults below.
/// Model ids and prices change quickly, so nothing here is hard-wired beyond a sensible default.
/// </summary>
public sealed class LlmSettings
{
    public const string FileName = "llm-settings.json";

    /// <summary>Master switch. When false the coach reports "not configured" even with a key.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Any OpenAI-compatible base URL (the Responses API lives at <c>{BaseUrl}/responses</c>).</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Weekly-coach model. Near-flagship quality at ~$0.05 per check-in.</summary>
    public string Model { get; set; } = "gpt-6.1-sol";

    /// <summary>Reasoning effort ("none"/"low"/"medium"/"high"); null or empty omits the field.</summary>
    public string? ReasoningEffort { get; set; } = "low";

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxOutputTokens { get; set; } = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Loads settings from <paramref name="path"/>; a missing or unreadable file yields defaults.</summary>
    public static LlmSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LlmSettings>(File.ReadAllText(path), JsonOptions) ?? new LlmSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the app from starting; fall back to defaults.
        }
        return new LlmSettings();
    }
}
