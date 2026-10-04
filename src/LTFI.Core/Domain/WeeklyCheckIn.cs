using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTFI.Core.Domain;

/// <summary>One answered question of a weekly check-in, as stored in <see cref="ReflectionEntry.Body"/>.</summary>
public sealed record WeeklyCheckInAnswer(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("answer")] string Answer);

/// <summary>
/// Snooze bookkeeping for the weekly check-in gate. <see cref="WeekStart"/> pins the state to one
/// check-in week, so the count resets automatically when the next week begins.
/// </summary>
public sealed record CheckInSnoozeState(DateTimeOffset WeekStart, int Count, DateTimeOffset? Until);

/// <summary>
/// The mandatory weekly check-in (plan §3.6 / §5 reflection): a fixed, versioned question list plus
/// the pure due/snooze rules. Deterministic — no LLM. Friction, not punishment: the user may snooze
/// up to <see cref="ProjectPolicy.MaxCheckInSnoozesPerWeek"/> times, after which only submitting
/// clears the gate (short answers are fine).
/// </summary>
public static class WeeklyCheckIn
{
    /// <summary>Stored in <see cref="ReflectionEntry.Prompt"/>; bump when the questions change.</summary>
    public const string PromptVersion = "weekly-checkin-v1";

    public static IReadOnlyList<string> Questions { get; } =
    [
        "What did you actually finish this week?",
        "What did you avoid, and what was the first moment you avoided it?",
        "Which active project moved least? Keep, pause, or kill it?",
        "What got in the way (energy, environment, unclear next step, overcommitment)?",
        "Top 3 commitments for next week — each with a first step under 10 minutes.",
        "One thing you'll do differently."
    ];

    /// <summary>Index of the commitments question — the one answer that must not be empty.</summary>
    public const int CommitmentsQuestionIndex = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// The most recent week boundary at or before <paramref name="now"/>:
    /// <see cref="ProjectPolicy.WeeklyCheckInDay"/> at 00:00 in <paramref name="now"/>'s offset.
    /// (Across a DST switch the boundary can be an hour off; harmless for a weekly prompt.)
    /// </summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var daysBack = ((int)now.DayOfWeek - (int)ProjectPolicy.WeeklyCheckInDay + 7) % 7;
        return new DateTimeOffset(now.Date.AddDays(-daysBack), now.Offset);
    }

    /// <summary>Due when no check-in has been saved since the most recent week boundary.</summary>
    public static bool IsDue(DateTimeOffset? lastCheckInAt, DateTimeOffset now) =>
        lastCheckInAt is not { } last || last < WeekStart(now);

    /// <summary>Snoozes already used in the current check-in week.</summary>
    public static int SnoozesUsed(CheckInSnoozeState? state, DateTimeOffset now) =>
        state is not null && state.WeekStart == WeekStart(now) ? state.Count : 0;

    public static int SnoozesRemaining(CheckInSnoozeState? state, DateTimeOffset now) =>
        Math.Max(0, ProjectPolicy.MaxCheckInSnoozesPerWeek - SnoozesUsed(state, now));

    /// <summary>True while a snooze taken this week is still running.</summary>
    public static bool IsSnoozed(CheckInSnoozeState? state, DateTimeOffset now) =>
        state is not null
        && state.WeekStart == WeekStart(now)
        && state.Until is { } until
        && until > now;

    /// <summary>The gate is up when a check-in is due and no snooze is running.</summary>
    public static bool MustShow(DateTimeOffset? lastCheckInAt, CheckInSnoozeState? state, DateTimeOffset now) =>
        IsDue(lastCheckInAt, now) && !IsSnoozed(state, now);

    /// <summary>
    /// Takes one snooze, returning the new state. Throws when nothing is due or this week's snoozes
    /// are used up.
    /// </summary>
    public static CheckInSnoozeState Snooze(CheckInSnoozeState? state, DateTimeOffset? lastCheckInAt, DateTimeOffset now)
    {
        if (!IsDue(lastCheckInAt, now))
        {
            throw new InvalidOperationException("No check-in is due, so there is nothing to snooze.");
        }

        if (SnoozesRemaining(state, now) == 0)
        {
            throw new InvalidOperationException("No snoozes left this week — submit the check-in (short answers are fine).");
        }

        return new CheckInSnoozeState(
            WeekStart(now),
            SnoozesUsed(state, now) + 1,
            now.AddHours(ProjectPolicy.CheckInSnoozeHours));
    }

    /// <summary>
    /// Pairs raw answers with the question list. Answers are trimmed; only the commitments answer is
    /// required to be non-empty.
    /// </summary>
    public static IReadOnlyList<WeeklyCheckInAnswer> BuildAnswers(IReadOnlyList<string?> answers)
    {
        if (answers.Count != Questions.Count)
        {
            throw new ArgumentException($"Expected {Questions.Count} answers, got {answers.Count}.", nameof(answers));
        }

        if (string.IsNullOrWhiteSpace(answers[CommitmentsQuestionIndex]))
        {
            throw new InvalidOperationException("Write at least one commitment for next week.");
        }

        return Questions
            .Select((q, i) => new WeeklyCheckInAnswer(q, answers[i]?.Trim() ?? string.Empty))
            .ToList();
    }

    public static string Serialize(IReadOnlyList<WeeklyCheckInAnswer> answers) =>
        JsonSerializer.Serialize(answers, JsonOptions);

    /// <summary>Parses a stored body; returns an empty list for malformed JSON rather than throwing.</summary>
    public static IReadOnlyList<WeeklyCheckInAnswer> Deserialize(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<List<WeeklyCheckInAnswer>>(body, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
