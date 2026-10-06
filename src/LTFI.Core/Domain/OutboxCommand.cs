using System;

namespace LTFI.Core.Domain;

/// <summary>
/// One pending LTFI → iPhone write-back command (contract <c>ltfi.outbox/v1</c>, see
/// docs/reminders-sync-setup.md). Every unconfirmed command is written to <c>outbox.json</c>; the
/// iPhone's "LTFI Apply" Shortcut performs it, and a later export confirms it (create → a reminder
/// with that url exists; complete → that url is completed; update → that url's due date equals the
/// pushed <c>dueDate</c>, see <see cref="ReminderRules.DueMatches"/>). Confirmed rows are kept as history.
/// </summary>
public class OutboxCommand
{
    public const string CreateOp = "create";
    public const string CompleteOp = "complete";

    /// <summary>Set an existing reminder's due date (payload: <c>dueDate</c>, full ISO 8601 with offset).
    /// At most one unconfirmed update per url: a newer date replaces the queued one.</summary>
    public const string UpdateOp = "update";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><see cref="CreateOp"/>, <see cref="CompleteOp"/> or <see cref="UpdateOp"/>.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>The <c>ltfi://r/…</c> url the command targets (also the task's ExternalId).</summary>
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>Flat JSON object of string fields merged into the command (title, notes, list, …).</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}
