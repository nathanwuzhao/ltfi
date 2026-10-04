using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LTFI.Core.Domain;

/// <summary>Where a weekly commitment stands.</summary>
public enum CommitmentStatus
{
    /// <summary>Set this week, not resolved yet.</summary>
    Open,

    /// <summary>Done: checked off by hand, or its linked reminder was completed.</summary>
    Kept,

    /// <summary>Not kept by the next check-in (also the fate of a carried-over commitment's old row).</summary>
    Missed,

    /// <summary>Superseded by a re-submitted check-in in the same week.</summary>
    Dropped
}

/// <summary>
/// One of the up-to-three commitments made in a weekly check-in (question 5). Optionally linked to
/// an open reminder-backed task: completing that task keeps the commitment automatically.
/// </summary>
public class WeeklyCommitment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The check-in (<see cref="ReflectionEntry"/>) that made this commitment.</summary>
    public Guid CheckInId { get; set; }

    /// <summary>The check-in week this commitment belongs to (<see cref="WeeklyCheckIn.WeekStart"/>'s date).</summary>
    public DateOnly WeekStart { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Optional reminder-backed task; its completion keeps the commitment.</summary>
    public Guid? LinkedTaskId { get; set; }

    public CommitmentStatus Status { get; set; } = CommitmentStatus.Open;

    public DateTimeOffset? ResolvedAt { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Pure rules for weekly commitments.</summary>
public static class WeeklyCommitments
{
    /// <summary>A check-in makes at most this many commitments.</summary>
    public const int MaxPerCheckIn = 3;

    /// <summary>Contribution/evidence source tag for <see cref="EvidenceType.CommitmentKept"/>.</summary>
    public const string EvidenceSource = "commitment";

    private static readonly Regex Bullet = new(@"^\s*(?:\d+[.)](?!\d)|[•·*]|[-–—](?=\s))\s*", RegexOptions.Compiled);

    /// <summary>The check-in week (as a date) that <paramref name="at"/> falls in.</summary>
    public static DateOnly WeekOf(DateTimeOffset at) =>
        DateOnly.FromDateTime(WeeklyCheckIn.WeekStart(at).DateTime);

    /// <summary>
    /// Splits a free-text Q5 answer (v1 check-ins) into commitments: one per line, with list markers
    /// ("1.", "2)", "-", "•", "*") stripped, blanks skipped, at most <see cref="MaxPerCheckIn"/>.
    /// A single line holding inline numbering ("1. a 2. b") is split on the numbers too.
    /// </summary>
    public static IReadOnlyList<string> SplitLegacyAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var lines = answer.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1)
        {
            // "1. foo 2. bar 3. baz" on one line.
            var inline = Regex.Split(lines[0], @"(?:^|\s)(?=\d+[.)]\s)")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            if (inline.Length > 1)
            {
                lines = inline;
            }
        }

        return lines
            .Select(StripMarkers)
            .Where(l => l.Length > 0)
            .Take(MaxPerCheckIn)
            .ToList();
    }

    /// <summary>Strips leading list markers, repeatedly ("2. • foo" → "foo").</summary>
    private static string StripMarkers(string line)
    {
        var current = line.Trim();
        while (Bullet.Match(current) is { Success: true, Length: > 0 } m)
        {
            current = current[m.Length..].Trim();
        }

        return current;
    }

    /// <summary>The Q5 answer stored for history/compat: "1. a\n2. b".</summary>
    public static string JoinForAnswer(IEnumerable<string> texts) =>
        string.Join("\n", texts.Select((t, i) => $"{i + 1}. {t}"));

    /// <summary>Trims, drops blanks and caps at <see cref="MaxPerCheckIn"/>; throws when nothing is left.</summary>
    public static IReadOnlyList<CommitmentDraft> Normalize(IEnumerable<CommitmentDraft> drafts)
    {
        var list = drafts
            .Select(d => d with { Text = d.Text?.Trim() ?? string.Empty })
            .Where(d => d.Text.Length > 0)
            .ToList();

        if (list.Count == 0)
        {
            throw new InvalidOperationException("Write at least one commitment for next week.");
        }

        if (list.Count > MaxPerCheckIn)
        {
            throw new InvalidOperationException($"At most {MaxPerCheckIn} commitments per week.");
        }

        return list;
    }
}

/// <summary>A commitment as typed on the check-in form, with an optional linked task.</summary>
public sealed record CommitmentDraft(string Text, Guid? LinkedTaskId = null);
