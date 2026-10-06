# Due-date push to the iPhone + clearer header stats (2026-10-06)

Builds on `2026-10-04-areas-url-ids-outbox.md`. Two parts: LTFI can now set a reminder's due date
on the iPhone (outbox op `update`), and the top-bar STREAK is the activity streak the contribution
graph shows. No migration: `OutboxCommand` already had `Op` + `PayloadJson`.

## A. Push a due date (outbox `update`)

- **Contract** (`ltfi.outbox/v1`, still flat strings): `{ "op": "update", "url": "ltfi://r/…", "dueDate": "2026-10-11T09:30:00-04:00" }`.
  `dueDate` is always full ISO 8601 with offset (`ReminderOutbox.FormatDue`) and never empty, because
  only *setting* a date is supported. `OutboxCommand.UpdateOp`, `ReminderOutbox.EnqueueUpdate/UpdatePayload/ReadDue`.
- **TaskService.**
  - `SetDueDateAsync(id, due)` and `PushDueByDaysAsync(id, days)` (returns the new due) are on `ITaskService`.
  - `TaskService.ShiftDue(due, days, today)` is pure. It moves by local calendar days and keeps the
    local wall-clock time, so midnight stays midnight across DST. With no due date it counts from
    today's local midnight.
  - Reminder-backed tasks:
    - The task needs an `ltfi://` url. Otherwise it throws `NoLtfiIdDueMessage`, "…change it on your phone (no LTFI id yet …)".
    - If a `create` is still pending, the create's payload is rewritten.
    - Otherwise one unconfirmed `update` per url is coalesced: the newer date replaces the payload.
    - `ExternalPendingSince` is set, so the existing PENDING chip shows.
  - Legacy local tasks just change `DueAt`.
  - `UpdateAsync` (the editor) handles a changed `DueAt` on a reminder-backed task with no pending create the same way, validated before anything changes.
  - Clearing the date throws `CannotClearDueMessage`. Title, notes and priority stay phone-owned, as before.
- **Sync** (`ReminderSyncService.ApplyAsync`):
  - **Pending update for a url.** If the export's due date matches the pushed one
    (`ReminderRules.DueMatches`), or the reminder is completed, or the payload is unreadable, the update is confirmed.
    Otherwise `ApplyFields(..., keepDue: true)` leaves LTFI's `DueAt` alone. After confirmation the phone owns `DueAt` again.
  - **`DueMatches`** returns true when the two dates are the same instant within 1 minute, so dropped
    seconds and a different offset or UTC still match. When either side is local midnight
    (all-day / date-only), the same local calendar day is enough.
  - **Stale-outbox guard.** When a `create` is confirmed but the export's due date doesn't match the
    create payload's `dueDate`, the sync puts the payload date back on the task and queues an `update`.
    This covers the phone applying an older `outbox.json` from before a +1D (iCloud lag).
- **UI.**
  - Tasks list (open rows) and Today's due/overdue list both get a ghost **+1D** button, tooltip
    "Push due date 1 day — syncs to iPhone". Both call `PushDueByDaysAsync(…, 1)`, refresh the list
    and show a feedback line.
  - In the Tasks editor, DUE DATE is now honoured for iPhone reminders (the help text says so).
- **Docs.** `docs/reminders-sync-setup.md`:
  - LTFI Apply gets step 4.5, the `update` branch: Otherwise If `Op` is `update` → If `Match` has
    any value → Get Dictionary Value `dueDate` → Get Dates from Input → Edit Reminder `Match` Due Date.
  - Also updated: the outbox sample (full-ISO dates plus an update), and the flow/upsert/confirmation notes.

## B. Header stats

- **STREAK.** This is now `TodaySnapshot.ActivityStreakDays` (new, defaulted record param), which is
  the contribution graph's current streak.
  - Single rule: `ContributionGraph.ScoreDays` (moved from `EvidenceService.GetDailyScoresAsync`,
    which now calls it) plus `ContributionGraph.CurrentStreak`, which `ComputeStats` also uses.
  - `IInsightsService.GetActivityStreakAsync()` exposes it. It uses the same 365-day window as the graph.
  - The focus streak is unchanged and still feeds the Focus Debt panel, Today's "Focus streak" chip and Review.
- **Labels and tooltips.** PTS is now **PTS TODAY**. PTS TODAY, STREAK and ACTIVE each have a tooltip
  (point values; activity streak vs focus streak; active non-standing projects / limit).
- **Refresh.** A new `ShellSignals` singleton is injected into the Tasks, Today, Command Center and
  Focus VMs. Its `StatsChanged` event makes the shell call `RefreshHeaderAsync` right away. It fires after:
  - a task checkbox or Today "Complete";
  - an editor save;
  - a commitment kept;
  - a focus session finished;
  - a pomodoro work or break transition;
  - NSDR completion.

  The existing paths still apply: reminders `Synced` (only when the sync changed something), check-in
  `GateCleared`, navigation, and the 15 s tick.

## Decisions / deviations

- **+1D adds to the old due date, as specified.** An item 3 days overdue becomes 2 days overdue, not
  tomorrow. Easy to change in `ShiftDue` if the owner prefers "max(old, today) + 1".
- **A pending update is confirmed when the phone shows the reminder completed**, so it never sticks
  forever on a ticked-off reminder.
- **The stale-outbox guard on create confirmation is an addition.** Without it, a +1D made while the
  phone was applying an older outbox would be silently lost. There's a trade-off: a phone edit made
  between "LTFI Apply" and the export of a brand-new reminder loses to LTFI's date. That window is
  seconds wide.
- **Not signalled explicitly:** project pause/activate (ACTIVE) and subtask ticks (+2). Navigation and
  the 15 s tick cover them.
- **The +1D button doesn't refresh the header.** It changes no points or streak.

## Verification

- `dotnet build LTFI.sln --no-incremental`: the owner's running LTFI.App (PID 23772) locks
  `src/LTFI.App/bin`, so the normal build fails only on the DLL copy (MSB3027). The same build with
  `-p:OutDir=<scratchpad>` gives **0 Warning(s), 0 Error(s)**. That includes LTFI.App, with XAML
  compiled bindings checked. The app was not launched or killed.
- `dotnet test LTFI.sln`: **141 passed**, 0 failed. `DuePushAndStreakTests` is new and covers:
  - queue and coalesce, with the file contents (op/url/dueDate, flat strings, full ISO);
  - a change on a pending create edits the create (via push and via the editor);
  - an editor change goes out as an update, clearing is blocked, and an unchanged date queues nothing;
  - the sync keeps the pushed date, then confirms it from a UTC, seconds-less export, and the phone owns it again afterwards;
  - date-only / midnight confirmation;
  - `DueMatches` tolerance cases;
  - the stale-outbox guard;
  - `ShiftDue` keeps the time of day, including across DST, and handles no-due;
  - blocked without an ltfi url;
  - the activity streak equals the graph's `CurrentStreak`, not the focus streak, and distraction evidence doesn't bridge a gap.

## Owner to-do (iPhone)

In **LTFI Apply**, add the `update` branch after the `complete` branch (setup doc §3 step 4.5).
Make sure `Op` is typed **Rich Text** so *is* `update` is offered.
