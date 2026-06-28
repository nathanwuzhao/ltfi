# Phase 2 — Core planner, focus sessions, evidence & points (2026-06-28)

Implements Phase 2 of [`../LTFI_7_Phase_Development_Plan.md`](../LTFI_7_Phase_Development_Plan.md),
following the "Reconciliation with Phase 1" notes added to that section.

## What shipped

- **Focus sessions**: a Focus page to start a session (project/task/intent), a live timer with
  pause/resume, and an end-of-session review (result + what changed + blocker + next action).
- **Evidence on completion**: completing a task → `TaskCompleted`, completing a subtask →
  `SubtaskCompleted`, finishing a focus session → `FocusSessionCompleted` (idempotent — only on
  the transition into the completed state).
- **Points & streak**: derived from evidence (no ledger). Today shows points earned today and a
  focus streak (consecutive days with a completed session).
- **Today enriched**: active-session banner, quick-start button (jumps to Focus), points, streak.

## Key implementation decisions

- **`FocusSessionService` is a singleton holding the live timer in memory** (accumulated time +
  a running-since marker). This is what makes the timer survive navigation. The session row is
  persisted on pause and finish; the in-memory clock is authoritative while the app runs.
- **`FocusSessionStatus` (lifecycle) vs `FocusSessionResult` (review outcome)** are separate.
  Status: Active/Paused/Completed/Abandoned. Result (nullable): Completed/Partial/Blocked.
  Migration `AddFocusSessionResult` adds the `Result` column.
- **Starting a session against a `Ready` task moves it to `InProgress`.** Finishing a session
  does *not* auto-complete the task — task completion stays an explicit, separate action.
- **Startup recovery**: any session left Active/Paused by a previous run is marked Abandoned on
  launch (`AbandonDanglingSessionsAsync`); trustworthy cross-restart resume is a Phase 7 concern.
- **Points/streak computed in `InsightsService` by pulling evidence and filtering in memory**
  (consistent with the Phase 1 note that SQLite can't do `DateTimeOffset` range/ORDER BY).
- Pure scoring lives in `LTFI.Core.Domain` (`EvidencePoints`, `Streaks`) and is unit-tested.

## Per-task time spent (added on request)

Each task shows the total focused time on it (e.g. two sessions of 30m + 15m → `45:00`).
Implemented as a **derived** value, consistent with derived project progress:
`TaskItem.TimeSpent` is a non-mapped property the read services populate by summing the
`Duration` of that task's **completed** focus sessions (abandoned and other-task sessions
excluded). It is durable because the source `FocusSession` rows are persisted, and it can't
drift from the actual sessions. Displayed in the Tasks list and the task editor via a
`DurationToClockConverter`. Updates on the next load/refresh of the Tasks page.

## Further refinements (on request)

- **Required focus time to complete**: a task can carry a `RequiredTime` (set as minutes in the
  editor). Completing it is blocked until its accumulated completed-session time meets the
  requirement — enforced in `TaskService` (both `SetStatusAsync` and `UpdateAsync`), surfaced as
  a feedback message on the Tasks and Today pages. Pure check lives in `TaskItem.MeetsRequiredTime`.
- **Project statuses trimmed to five**: `Idea, Active, Paused, Completed, Killed`.
- **Auto-archive**: "Archived" is derived (`Project.IsArchived` = Completed/Killed; a task is
  archived when Completed/Canceled). Archived items drop out of the active lists, with a
  "Show archived" toggle on both Projects and Tasks. `Project.ArchivedAt` is stamped on archive
  (anchors the future kill cooldown). Migration `AddArchiveAndRequiredTime`.
- **Kill blocks reactivation**: a `Killed` project can't be moved back to a non-killed status
  (hard block for now; the *timed* cooldown + calendar is left to Phase 3, see plan §3.5).
- **Today quick-start**: the "Start focus" button is disabled until a task is selected; clicking
  it opens the Focus page **prefilled** with that task/project (via `FocusViewModel.PrepareFor`,
  applied on the page's next refresh). Ignored if a session is already running.

## Verification

- `dotnet build LTFI.sln` — clean.
- `dotnet test` — 18 tests pass: focus lifecycle (start → InProgress task → pause/resume →
  finish → persisted + evidence), abandon (no evidence), idempotent task/subtask evidence,
  today snapshot (points + streak), plus pure points/streak unit tests.
- Fresh-DB app run applies all three migrations in order and starts clean.

## Out of scope (per plan) / deferred

- Hard app/site blocking, LLM task generation, GitHub/Logseq import (later phases).
- Project detail page and label-assignment UI (noted as remaining §2.1 items; deferred until
  they earn their place — the label entity exists).
- Live-ticking elapsed on the Today banner (Today shows elapsed at refresh; the Focus page ticks).
