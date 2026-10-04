using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LTFI.Core.Abstractions;

namespace LTFI.Infrastructure.Llm;

/// <summary>
/// The weekly LLM coach. Serialises <see cref="CoachInput"/> (deterministic numbers + the user's
/// answers) into one prompt, asks the provider for strict-schema JSON, and parses it leniently into
/// a <see cref="CoachReport"/>. Read-only by design: it never touches the database (plan rule 8).
/// </summary>
public sealed class CoachService(ILlmProvider llm) : ICoachService
{
    /// <summary>Cap on evidence lines sent, to keep the prompt (and cost) small.</summary>
    public const int MaxEvidenceLines = 40;

    /// <summary>Per-string cap applied when parsing, mirroring the prompt's 280-char rule.</summary>
    private const int MaxStringLength = 400;

    public bool IsConfigured => llm.IsConfigured;

    public async Task<CoachReport> AnalyzeWeekAsync(CoachInput input, CancellationToken cancellationToken = default)
    {
        if (!llm.IsConfigured)
            throw new LlmException(LlmFailureKind.NotConfigured,
                $"OpenAI key not configured — set {DpapiApiKeyStore.DefaultEnvVar} or save a key in LTFI.");

        var request = new LlmRequest(
            CoachPrompts.WeeklySystemPrompt,
            BuildUserContent(input),
            new LlmJsonSchema(CoachPrompts.SchemaName, CoachPrompts.WeeklySchemaJson));

        var response = await llm.CompleteAsync(request, cancellationToken);
        return Parse(response);
    }

    /// <summary>
    /// The user message: a <c>&lt;checkin_data&gt;</c> JSON block of deterministic data, then an
    /// <c>&lt;answers&gt;</c> block of Q/A pairs. Public for tests.
    /// </summary>
    public static string BuildUserContent(CoachInput input)
    {
        var r = input.Review;
        var data = new JsonObject
        {
            // Keyed by the window's last day: a trailing 7-day review closes out the current ISO week.
            ["week"] = IsoWeekKey(input.WeekEnd),
            ["range"] = new JsonObject
            {
                ["start"] = input.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end"] = input.WeekEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            ["review"] = new JsonObject
            {
                ["active_project_count"] = r.ActiveProjectCount,
                ["max_active_projects"] = r.MaxActiveProjects,
                ["is_over_limit"] = r.IsOverLimit,
                ["tasks_completed"] = r.TasksCompletedThisWeek,
                ["focus_hours"] = Hours(r.FocusTimeThisWeek),
                ["new_projects"] = r.NewProjectsThisWeek,
                ["archived_projects"] = r.ArchivedProjectsThisWeek,
                ["project_activity"] = new JsonArray(r.ProjectActivity.Select(p => (JsonNode)new JsonObject
                {
                    ["title"] = p.Title,
                    ["focus_hours"] = Hours(p.FocusTime),
                    ["tasks_completed"] = p.TasksCompleted
                }).ToArray()),
                ["stalled_projects"] = new JsonArray(r.StalledProjects.Select(s => (JsonNode)new JsonObject
                {
                    ["title"] = s.Title,
                    ["days_since_activity"] = s.DaysSinceActivity
                }).ToArray())
            },
            ["daily"] = new JsonArray(input.Daily.Select(d => (JsonNode)new JsonObject
            {
                ["date"] = d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["weekday"] = d.Day.DayOfWeek.ToString()[..3],
                ["evidence_count"] = d.Count
            }).ToArray()),
            ["streak"] = new JsonObject { ["current_days"] = input.CurrentStreakDays },
            ["recent_evidence"] = new JsonArray(input.RecentEvidence.Take(MaxEvidenceLines).Select(e => (JsonNode)new JsonObject
            {
                ["when"] = e.OccurredAt.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["type"] = e.Type.ToString(),
                ["title"] = e.Title,
                ["project"] = e.ProjectTitle
            }).ToArray()),
            ["last_week_commitments"] = new JsonArray(input.LastWeekCommitments.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())
        };

        var sb = new StringBuilder();
        sb.AppendLine("<checkin_data>");
        sb.AppendLine(data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("</checkin_data>");
        sb.AppendLine("<answers>");

        var answered = input.Answers.Where(a => !string.IsNullOrWhiteSpace(a.Answer)).ToList();
        if (answered.Count == 0)
        {
            sb.AppendLine("(No check-in answers this time — coach from the data alone, keep the plan small, and say so in data_caveat.)");
        }
        foreach (var a in answered)
        {
            sb.Append("Q: ").AppendLine(a.Question.Trim());
            sb.Append("A: ").AppendLine(a.Answer.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("</answers>");
        return sb.ToString();
    }

    /// <summary>
    /// Parses the model's JSON into a <see cref="CoachReport"/>. Lenient about missing fields,
    /// tolerant of a markdown code fence, clamps list sizes and <c>first_step_minutes</c> (1-10).
    /// Invalid JSON or a non-object root throws <see cref="LlmException"/> (MalformedOutput).
    /// </summary>
    public static CoachReport Parse(LlmResponse response)
    {
        var text = StripCodeFence(response.Text);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmFailureKind.MalformedOutput,
                "The coach replied, but not in the expected format. Try again.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LlmException(LlmFailureKind.MalformedOutput,
                    "The coach replied, but not in the expected format. Try again.");

            var commitments = Items(root, "commitments", 3, e => new CoachCommitment(
                Str(e, "title"), Str(e, "why"), NullableStr(e, "project_title"), Str(e, "first_step"),
                Math.Clamp(Int(e, "first_step_minutes", 10), 1, 10), Str(e, "when_where"), Str(e, "if_then")))
                .Where(c => c.Title.Length > 0).ToList();

            return new CoachReport(
                Headline: Str(root, "headline"),
                Wins: Items(root, "wins", 5, e => new CoachWin(Str(e, "text"), Str(e, "evidence"))),
                Patterns: Items(root, "patterns", 4, e => new CoachPattern(
                    Str(e, "observation"), Str(e, "evidence"), OneOf(Str(e, "effect"), "neutral", "helped", "hurt", "neutral"))),
                Risks: Items(root, "risks", 3, e => new CoachRisk(Str(e, "risk"), Str(e, "if_then"))),
                LastWeekReview: Items(root, "last_week_review", 3, e => new CoachLastWeekItem(
                    Str(e, "commitment"), OneOf(Str(e, "status"), "unknown", "done", "partial", "missed", "unknown"), Str(e, "note"))),
                Commitments: commitments,
                DropOrPause: Items(root, "drop_or_pause", 3, e => new CoachDropOrPause(
                    Str(e, "item"), OneOf(Str(e, "action"), "pause", "pause", "archive", "drop", "shrink"), Str(e, "reason"))),
                QuestionToSitWith: Str(root, "question_to_sit_with"),
                DataCaveat: NullableStr(root, "data_caveat"),
                Model: response.Model,
                Usage: response.Usage,
                RawJson: text);
        }
    }

    /// <summary>ISO-8601 week key, e.g. "2026-W40".</summary>
    public static string IsoWeekKey(DateOnly day)
    {
        var dt = day.ToDateTime(TimeOnly.MinValue);
        return $"{ISOWeek.GetYear(dt)}-W{ISOWeek.GetWeekOfYear(dt):00}";
    }

    private static double Hours(TimeSpan t) => Math.Round(t.TotalHours, 1, MidpointRounding.AwayFromZero);

    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewline = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? t[(firstNewline + 1)..lastFence].Trim() : t;
    }

    private static List<T> Items<T>(JsonElement obj, string name, int max, Func<JsonElement, T> map) =>
        obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Take(max).Select(map).ToList()
            : [];

    private static string Str(JsonElement obj, string name) => NullableStr(obj, name) ?? string.Empty;

    private static string? NullableStr(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > MaxStringLength ? s[..MaxStringLength] + "…" : s;
    }

    private static int Int(JsonElement obj, string name, int fallback) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static string OneOf(string value, string fallback, params string[] allowed)
    {
        var v = value.ToLowerInvariant();
        return allowed.Contains(v) ? v : fallback;
    }
}
