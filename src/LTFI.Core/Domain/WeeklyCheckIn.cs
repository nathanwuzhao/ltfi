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
/// <em>reviewed</em> week (its Monday 00:00), so the count resets when the next review week begins.
/// (State written by the old Sunday-week model never matches a Monday and so reads as "none used".)
/// </summary>
public sealed record CheckInSnoozeState(DateTimeOffset WeekStart, int Count, DateTimeOffset? Until);

/// <summary>
/// The pure schedule answer for "now": which week a check-in would review, the phase, and the
/// moments the UI shows. All moments carry <c>now</c>'s UTC offset (display only).
/// </summary>
public sealed record CheckInState(
    CheckInPhase Phase,
    DateOnly ReviewWeek,
    DateTimeOffset OpensAt,
    DateTimeOffset GateAt,
    DateTimeOffset DueAt,
    DateTimeOffset NextOpensAt,
    bool CanSubmit)
{
    /// <summary>Done, but the review week's window is still open: a re-submit revises it (no extra points).</summary>
    public bool IsRevision => Phase == CheckInPhase.Done && CanSubmit;

    /// <summary>The app is gated in these phases (unless snoozed).</summary>
    public bool IsGatePhase => Phase is CheckInPhase.Closing or CheckInPhase.Overdue;
}

/// <summary>
/// The mandatory weekly check-in (plan §3.6 / §5 reflection): a fixed, versioned question list plus
/// the pure schedule/snooze rules. Deterministic — no LLM. Friction, not punishment: the user may
/// snooze up to <see cref="CheckInSchedule.MaxSnoozes"/> times per reviewed week, after which only
/// submitting clears the gate (short answers are fine).
/// <para>
/// The model (2026-10-11): a week is Monday 00:00 → Sunday 23:59:59 local. A check-in reviews the
/// week that is ending. Week W's window opens at <see cref="CheckInSchedule.Opens"/> (Sat 00:00),
/// gates from <see cref="CheckInSchedule.GateFrom"/> (Sun 18:00) and is due at
/// <see cref="CheckInSchedule.Due"/> (Sun 23:59). A check-in saved on/after W's opening reviews W;
/// one saved before it (Mon–Fri) reviews W-1, late. Commitments made in the check-in for W apply to
/// W+1. All week math uses the wall clock of the timestamp passed in, so callers convert stored
/// times to the local zone first.
/// </para>
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

    // ------------------------------------------------------------------ schedule

    /// <summary>Monday (00:00) of the Mon–Sun week containing <paramref name="at"/>'s wall-clock date.</summary>
    public static DateOnly MondayOf(DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(at.DateTime);
        return date.AddDays(-CheckInSchedule.DaysFromMonday(date.DayOfWeek));
    }

    /// <summary>
    /// The week (its Monday) a check-in saved at <paramref name="at"/> reviews: this week once its
    /// window has opened, otherwise last week (a late check-in). Monotonic in time.
    /// </summary>
    public static DateOnly ReviewedWeekOf(DateTimeOffset at, CheckInSchedule schedule)
    {
        var monday = MondayOf(at);
        return at.DateTime - monday.ToDateTime(TimeOnly.MinValue) >= schedule.Opens
            ? monday
            : monday.AddDays(-7);
    }

    /// <summary>True when a check-in saved at <paramref name="at"/> came after its reviewed week's deadline (a late one).</summary>
    public static bool IsLate(DateTimeOffset at, CheckInSchedule schedule) =>
        at.DateTime - ReviewedWeekOf(at, schedule).ToDateTime(TimeOnly.MinValue) >= schedule.Deadline;

    /// <summary>The week (its Monday) whose commitments a check-in for <paramref name="reviewedWeek"/> sets: the next one.</summary>
    public static DateOnly CommitmentWeekFor(DateOnly reviewedWeek) => reviewedWeek.AddDays(7);

    /// <summary>
    /// Where the check-in stands at <paramref name="now"/>. <paramref name="lastCheckInAt"/> is the
    /// most recent saved check-in (in local time); because <see cref="ReviewedWeekOf"/> is monotonic
    /// it alone tells whether the current review week is done.
    /// </summary>
    public static CheckInState Evaluate(DateTimeOffset? lastCheckInAt, DateTimeOffset now, CheckInSchedule schedule)
    {
        var week = ReviewedWeekOf(now, schedule);
        var done = lastCheckInAt is { } last && ReviewedWeekOf(last, schedule) >= week;
        var into = now.DateTime - week.ToDateTime(TimeOnly.MinValue);

        var phase = done ? CheckInPhase.Done
            : into < schedule.GateFrom ? CheckInPhase.Open
            : into < schedule.Deadline ? CheckInPhase.Closing
            : CheckInPhase.Overdue;

        DateTimeOffset Moment(DateOnly monday, TimeSpan offset) =>
            new(monday.ToDateTime(TimeOnly.MinValue) + offset, now.Offset);

        return new CheckInState(
            phase,
            week,
            Moment(week, schedule.Opens),
            Moment(week, schedule.GateFrom),
            Moment(week, schedule.Due),
            Moment(week.AddDays(7), schedule.Opens),
            // The form is there whenever something is due, and after submitting until the deadline
            // (to revise). Mon–Fri with last week reviewed it is not.
            CanSubmit: !done || (week == MondayOf(now) && into < schedule.Deadline));
    }

    /// <summary>A check-in is due (form + header chip) unless the current review week is done.</summary>
    public static bool IsDue(DateTimeOffset? lastCheckInAt, DateTimeOffset now, CheckInSchedule schedule) =>
        Evaluate(lastCheckInAt, now, schedule).Phase != CheckInPhase.Done;

    /// <summary>Snoozes already used for the current review week.</summary>
    public static int SnoozesUsed(CheckInSnoozeState? state, DateTimeOffset now, CheckInSchedule schedule) =>
        state is not null && DateOnly.FromDateTime(state.WeekStart.DateTime) == ReviewedWeekOf(now, schedule) ? state.Count : 0;

    public static int SnoozesRemaining(CheckInSnoozeState? state, DateTimeOffset now, CheckInSchedule schedule) =>
        Math.Max(0, schedule.MaxSnoozes - SnoozesUsed(state, now, schedule));

    /// <summary>True while a snooze taken for the current review week is still running.</summary>
    public static bool IsSnoozed(CheckInSnoozeState? state, DateTimeOffset now, CheckInSchedule schedule) =>
        state is not null
        && DateOnly.FromDateTime(state.WeekStart.DateTime) == ReviewedWeekOf(now, schedule)
        && state.Until is { } until
        && until > now;

    /// <summary>The gate is up from the gate time (or while overdue) until submitted, unless snoozed.</summary>
    public static bool MustShow(DateTimeOffset? lastCheckInAt, CheckInSnoozeState? state, DateTimeOffset now, CheckInSchedule schedule) =>
        Evaluate(lastCheckInAt, now, schedule).IsGatePhase && !IsSnoozed(state, now, schedule);

    /// <summary>
    /// Takes one snooze, returning the new state. Throws unless the gate is up (closing or overdue),
    /// or when this review week's snoozes are used up.
    /// </summary>
    public static CheckInSnoozeState Snooze(
        CheckInSnoozeState? state, DateTimeOffset? lastCheckInAt, DateTimeOffset now, CheckInSchedule schedule)
    {
        var current = Evaluate(lastCheckInAt, now, schedule);
        if (!current.IsGatePhase)
        {
            throw new InvalidOperationException(current.Phase == CheckInPhase.Done
                ? "No check-in is due, so there is nothing to snooze."
                : "The check-in isn't blocking anything yet, so there is nothing to snooze.");
        }

        if (SnoozesRemaining(state, now, schedule) == 0)
        {
            throw new InvalidOperationException("No snoozes left this week — submit the check-in (short answers are fine).");
        }

        return new CheckInSnoozeState(
            new DateTimeOffset(current.ReviewWeek.ToDateTime(TimeOnly.MinValue), now.Offset),
            SnoozesUsed(state, now, schedule) + 1,
            now.AddHours(schedule.SnoozeHours));
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
