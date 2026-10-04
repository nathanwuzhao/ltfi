using System;
using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>
/// A JSON schema the provider should constrain its output to (OpenAI "structured outputs").
/// <see cref="SchemaJson"/> is the raw schema object; <see cref="Name"/> is the schema's identifier.
/// </summary>
public sealed record LlmJsonSchema(string Name, string SchemaJson);

/// <summary>One completion request: a system prompt, the user content, and an optional output schema.</summary>
public sealed record LlmRequest(
    string SystemPrompt,
    string UserContent,
    LlmJsonSchema? Schema = null,
    int MaxOutputTokens = 4000);

/// <summary>Token accounting reported by the provider (zeros when the provider doesn't report it).</summary>
public sealed record LlmUsage(int InputTokens, int OutputTokens);

/// <summary>The model's text output (JSON when a schema was requested) plus the model that produced it.</summary>
public sealed record LlmResponse(string Text, string Model, LlmUsage Usage);

/// <summary>Why an LLM call failed. Every kind is a soft failure: callers keep the user's input and offer a retry.</summary>
public enum LlmFailureKind
{
    /// <summary>No API key / provider disabled. Not an error so much as "the coach is off".</summary>
    NotConfigured,
    /// <summary>Network error, timeout, auth failure, rate limit, or a 5xx after retrying.</summary>
    Transport,
    /// <summary>The model declined to answer.</summary>
    Refused,
    /// <summary>The response was cut off (e.g. hit max_output_tokens).</summary>
    Incomplete,
    /// <summary>The output couldn't be parsed into the expected shape.</summary>
    MalformedOutput
}

/// <summary>A user-presentable LLM failure. Messages never contain the API key.</summary>
public sealed class LlmException(LlmFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LlmFailureKind Kind { get; } = kind;
}

/// <summary>
/// Provider-agnostic LLM completion (plan §5.3). OpenAI-compatible today; Ollama / LM Studio /
/// a mock for tests slot in behind the same interface. Core logic never names a provider.
/// </summary>
public interface ILlmProvider
{
    /// <summary>True when the provider has what it needs (e.g. an API key) to make calls.</summary>
    bool IsConfigured { get; }

    /// <summary>The model id requests will use, for display (e.g. "gpt-6.1-sol").</summary>
    string Model { get; }

    /// <summary>Runs one completion. Throws <see cref="LlmException"/> on any failure.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the LLM API key lives. Implementations must never log the key or store it in SQLite.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>The key, or null when none is configured.</summary>
    string? GetKey();

    /// <summary>Human-readable origin of the current key ("env:OPENAI_API_KEY", "file"), or null.</summary>
    string? KeySource { get; }

    void SetKey(string key);

    void ClearKey();
}
