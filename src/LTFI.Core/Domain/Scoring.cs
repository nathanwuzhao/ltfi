using System;
using System.Collections.Generic;
using System.Linq;

namespace LTFI.Core.Domain;

/// <summary>
/// Maps evidence to points and sums them (plan §2.4). Points are derived from evidence rather
/// than a separate ledger, keeping "evidence over vibes" literal.
/// </summary>
public static class EvidencePoints
{
    public static int For(EvidenceType type) => type switch
    {
        EvidenceType.TaskCompleted => 10,
        EvidenceType.SubtaskCompleted => 2,
        EvidenceType.FocusSessionCompleted => 5,
        EvidenceType.ReflectionSubmitted => 10,
        _ => 0
    };

    public static int Sum(IEnumerable<EvidenceItem> evidence) =>
        evidence.Sum(e => For(e.Type));
}

/// <summary>
/// Factual streak counting (plan §2.4): the number of consecutive days, counting back from
/// today, on which an activity occurred. Today not yet having activity does not break a streak
/// that ran through yesterday.
/// </summary>
public static class Streaks
{
    public static int ConsecutiveDays(IEnumerable<DateOnly> activeDays, DateOnly today)
    {
        var days = activeDays.ToHashSet();
        if (days.Count == 0)
        {
            return 0;
        }

        // Anchor on today if active, otherwise yesterday; if neither, the streak is broken.
        var cursor = days.Contains(today)
            ? today
            : today.AddDays(-1);

        if (!days.Contains(cursor))
        {
            return 0;
        }

        var count = 0;
        while (days.Contains(cursor))
        {
            count++;
            cursor = cursor.AddDays(-1);
        }

        return count;
    }
}
