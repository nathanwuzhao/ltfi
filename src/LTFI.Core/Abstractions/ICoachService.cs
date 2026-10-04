using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LTFI.Core.Abstractions;

/// <summary>One check-in question and the user's answer, in the user's own words.</summary>
public sealed record CheckInAnswer(string Question, string Answer);

/// <summary>
/// Everything the weekly coach sees. All of it is deterministic local data or the user's own
/// answers (plan rule 9: deterministic analytics before LLM interpretation). Lists may be empty —
/// the coach is told when data is sparse rather than guessing.
/// </summary>
public sealed record CoachInput(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    WeeklyReview Review,
    IReadOnlyList<DayActivity> Daily,
    IReadOnlyList<EvidenceLine> RecentEvidence,
    IReadOnlyList<CheckInAnswer> Answers,
    IReadOnlyList<string> LastWeekCommitments,
    int CurrentStreakDays = 0);

public sealed record CoachWin(string Text, string Evidence);

/// <summary><see cref="Effect"/> is "helped", "hurt", or "neutral".</summary>
public sealed record CoachPattern(string Observation, string Evidence, string Effect);

public sealed record CoachRisk(string Risk, string IfThen);

/// <summary><see cref="Status"/> is "done", "partial", "missed", or "unknown".</summary>
public sealed record CoachLastWeekItem(string Commitment, string Status, string Note);

/// <summary>A suggested commitment for the coming week. Suggestions only — the user accepts or edits.</summary>
public sealed record CoachCommitment(
    string Title,
    string Why,
    string? ProjectTitle,
    string FirstStep,
    int FirstStepMinutes,
    string WhenWhere,
    string IfThen);

/// <summary><see cref="Action"/> is "pause", "archive", "drop", or "shrink".</summary>
public sealed record CoachDropOrPause(string Item, string Action, string Reason);

/// <summary>
/// The typed weekly coaching card (mirrors the <c>ltfi_weekly_coach</c> JSON schema).
/// <see cref="RawJson"/> is the model's output verbatim, ready for
/// <c>ReflectionEntry.StructuredSummaryJson</c> once the user confirms it (plan §2.4).
/// </summary>
public sealed record CoachReport(
    string Headline,
    IReadOnlyList<CoachWin> Wins,
    IReadOnlyList<CoachPattern> Patterns,
    IReadOnlyList<CoachRisk> Risks,
    IReadOnlyList<CoachLastWeekItem> LastWeekReview,
    IReadOnlyList<CoachCommitment> Commitments,
    IReadOnlyList<CoachDropOrPause> DropOrPause,
    string QuestionToSitWith,
    string? DataCaveat,
    string Model,
    LlmUsage Usage,
    string RawJson);

/// <summary>
/// The optional LLM weekly coach. It only suggests: nothing here writes to the database
/// (plan rule 8 — LLM features never mutate user data without confirmation).
/// </summary>
public interface ICoachService
{
    /// <summary>False when no LLM provider/key is configured; callers show a setup hint instead.</summary>
    bool IsConfigured { get; }

    /// <summary>Asks the coach for a weekly card. Throws <see cref="LlmException"/> on any failure.</summary>
    Task<CoachReport> AnalyzeWeekAsync(CoachInput input, CancellationToken cancellationToken = default);
}
