using System;

namespace LTFI.Core.Domain;

/// <summary>
/// One pending LTFI → iPhone write-back command (contract <c>ltfi.outbox/v1</c>, see
/// docs/reminders-sync-setup.md). Every unconfirmed command is written to <c>outbox.json</c>; the
/// iPhone's "LTFI Apply" Shortcut performs it, and a later export confirms it (create → a reminder
/// with that url exists; complete → that url is completed). Confirmed rows are kept as history.
/// </summary>
public class OutboxCommand
{
    public const string CreateOp = "create";
    public const string CompleteOp = "complete";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><see cref="CreateOp"/> or <see cref="CompleteOp"/>.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>The <c>ltfi://r/…</c> url the command targets (also the task's ExternalId).</summary>
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>Flat JSON object of string fields merged into the command (title, notes, list, …).</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}
