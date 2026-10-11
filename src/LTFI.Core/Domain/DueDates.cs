using System;

namespace LTFI.Core.Domain;

/// <summary>
/// LTFI treats a task's due as a calendar DATE. The single rule for every due date LTFI itself sets
/// (editor, +1D, a reminder created from LTFI) and sends to the iPhone: that local date at
/// <b>23:59:00</b>, with its offset. The phone's Shortcut (Get Dates from Input → Edit Reminder) keeps
/// whatever time it is given, so midnight read as "12:00 PM" there and +N days then kept the noon;
/// 23:59 is the "no time, but end of the day" answer. Phone-made times are never rewritten on sync.
/// </summary>
public static class DueDates
{
    /// <summary>The time of day LTFI stores and sends for a due date.</summary>
    public static readonly TimeSpan EndOfDayTime = new(23, 59, 0);

    /// <summary><paramref name="localDate"/> (its date part) at 23:59 local, with that moment's offset.</summary>
    public static DateTimeOffset EndOfDay(DateTime localDate) =>
        AtLocal(localDate.Date + EndOfDayTime);

    /// <summary><paramref name="due"/>'s LOCAL calendar date at 23:59 (whatever time it had).</summary>
    public static DateTimeOffset EndOfDay(DateTimeOffset due) => EndOfDay(due.LocalDateTime);

    /// <summary>Null stays null; otherwise <see cref="EndOfDay(DateTimeOffset)"/>.</summary>
    public static DateTimeOffset? EndOfDay(DateTimeOffset? due) => due is { } d ? EndOfDay(d) : null;

    /// <summary>
    /// +N days: (the local due date + <paramref name="days"/>) at 23:59, regardless of the old time
    /// (so a reminder the phone stored at 12:00 goes back out at 23:59). With no due date, counts from
    /// <paramref name="today"/>.
    /// </summary>
    public static DateTimeOffset Shift(DateTimeOffset? due, int days, DateTime today) =>
        EndOfDay((due is { } d ? d.LocalDateTime.Date : today.Date).AddDays(days));

    /// <summary>
    /// True when a due's local time carries no real meaning: 00:00 (all-day), 12:00 (what the phone
    /// stores for a date given without a usable time) or 23:59 (LTFI's own). Display-only; LTFI shows
    /// dates anyway and never rewrites phone data because of it.
    /// </summary>
    public static bool IsDateOnly(DateTimeOffset due)
    {
        var t = due.LocalDateTime.TimeOfDay;
        return t == TimeSpan.Zero || t == TimeSpan.FromHours(12) || t == EndOfDayTime;
    }

    private static DateTimeOffset AtLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
