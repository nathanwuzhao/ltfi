# Weekly commitments, clickable graph days, standing projects without progress (2026-10-04)

Three slices from the plan's §0.1 "Next" list: the check-in's commitments become real records
(item 1), a contribution-graph day can be clicked to see its evidence (part of item 4), and
standing projects stop showing a meaningless progress %. Builds on
`2026-10-03-weekly-checkin.md`, `2026-10-03-contribution-graph.md` and
`2026-10-04-areas-url-ids-outbox.md`.

## What shipped

### 1. Weekly commitments

- **Domain** (`Core/Domain/WeeklyCommitment.cs`):
  - `WeeklyCommitment`: Id, CheckInId (the `ReflectionEntry`), WeekStart (`DateOnly`, the check-in week per `WeeklyCheckIn.WeekStart`), Text, LinkedTaskId?, Status, ResolvedAt?, SortOrder, CreatedAt.
  - `CommitmentStatus`: Open | Kept | Missed | Dropped.
  - `WeeklyCommitments` rules: `MaxPerCheckIn = 3`, `WeekOf`, `SplitLegacyAnswer` (v1 Q5 free text → lines, list markers `1.` `2)` `-` `•` `*` stripped repeatedly, inline "1. a 2. b" split, max 3), `JoinForAnswer` and `Normalize` (trim, drop blanks, 1..3 or throw).
  - `CommitmentDraft(Text, LinkedTaskId?)`.
- **Evidence**: a new `EvidenceType.CommitmentKept`, appended at the end of the enum. It is worth **5 pts** (`EvidencePoints.For`; the contribution weight follows automatically). The Command Center feed tags it `KEPT` in green.
- **Migration `AddWeeklyCommitments`**: a `WeeklyCommitments` table with Status stored as text.
  - FK CheckInId → Reflections (cascade). FK LinkedTaskId → Tasks (set null).
  - Indexes on CheckInId, WeekStart and LinkedTaskId.
- **`IReflectionService.SaveWeeklyCheckInAsync(answers, commitments, resolutions?)`** (new overload). It does all of this in one `SaveChanges`:
  - Q5 in the answers JSON becomes the joined `1. …\n2. …` text, for history and the coach.
  - It creates the rows, and drops links to tasks that no longer exist.
  - It settles **earlier weeks'** Open commitments: `Kept` if the review chose it, otherwise `Missed`.
  - Open commitments from an earlier check-in **in the same week** become `Dropped`, because a re-submit supersedes them.

  The old answers-only overload still works. It stores Q5 verbatim and splits it into commitments.
- **`ICommitmentService` / `CommitmentService`**:
  - `GetCurrentWeekAsync`: this week's commitments, excluding Dropped, enriched with the linked task's title, `#area`, due date and whether it is still open.
  - `GetPendingReviewAsync`: the most recent earlier week's commitments plus any still-Open older ones. It returns nothing once this week's check-in is in and nothing is Open.
  - `KeepAsync`: manual keep. It is idempotent and refuses Missed or Dropped commitments.
  - `GetLinkableTasksAsync`: the picker's list of open, reminder-backed, not-removed tasks, soonest due first.
  - `BackfillCurrentWeekAsync`.
- **Auto-keep = reconcile on read.** Every read (and every check-in save) first marks Open
  commitments whose linked task is `Completed` as Kept. They are resolved at the task's
  `CompletedAt` and get their evidence item at that time. This works the same whichever path
  completed the task (LTFI's `TaskService`, or the iPhone via `ReminderSyncService`), and neither
  service needed to change.
  - Evidence idempotency has two layers: the status transition happens only once, and an existing `CommitmentKept` row whose `MetadataJson` is `{"commitmentId":"…"}` blocks a second one.
  - The evidence carries the task's ProjectId and TaskId when linked.
- **Backfill**: `GetCurrentWeekAsync` first calls `BackfillCurrentWeekAsync`. When **this week's latest** check-in has no commitment rows (a v1 free-text check-in, like the owner's Sun Oct 4 ~18:40 one), it splits that check-in's Q5 into up to 3 unlinked commitments.
  - It is idempotent: it does nothing once that check-in has rows.
  - Older weeks are never back-filled.
- **Check-In page**:
  - **LAST WEEK'S COMMITMENTS** panel (only shown when there is something to review). Each row shows the text and `↪ task · #area · due`. Already-kept ones show `✓ KEPT · AUTO` for linked and `✓ KEPT` for checked. The others get **KEPT / MISSED / CARRY OVER** segmented buttons; clicking the chosen one again un-chooses it.
  - Carry over fills the first empty commitment row. It keeps the link only if the reminder is still open. Un-carrying clears the row unless you edited it.
  - Anything left unchosen becomes Missed on submit.
  - Q5 is now **3 rows**: a text box plus a "link to reminder" ComboBox (`title · #area · due OCT 08`, or "— no reminder link —"). It is marked "AT LEAST 1". Drafts, links and review choices survive the 15s refresh. The picker selection is restored by id when the option list is rebuilt.
- **Command Center WEEKLY COMMITMENTS**:
  - The PREVIEW chip and the stand-in text are gone.
  - It lists this week's commitments with a `k / n KEPT` counter.
  - Unlinked open rows have a **checkbox**, which keeps the commitment, writes +5 evidence and refreshes the graph and feed. Linked open rows show `↪` plus `#area · OCT 08`, with a tooltip naming the reminder. Kept rows show a green ✓ and dimmed, struck-through green text.
  - Empty state: "No commitments yet — do your weekly check-in" and an **OPEN CHECK-IN** button, which navigates to CHK through `OpenCheckInRequested`, wired in `MainWindowViewModel`.

### 2. Clickable contribution graph days

- `IEvidenceService.GetForDayAsync(DateOnly day)` returns all evidence on one **local** day, newest first, with project titles. It uses the same local-day bucketing as the graph, so a cell and its feed always agree.
- Clicking a real day cell (`Tapped`, handled in code-behind and passed to `CommandCenterViewModel.ToggleDayAsync`) selects it:
  - The cell gets a bright 1px outline, and real cells show a hand cursor and a hover outline.
  - EVIDENCE FEED filters to that day. Its header reads e.g. `SAT OCT 04 · 6 EVENTS · 22 PTS`, where pts uses the contribution weights, plus a **✕ ALL** button.
  - Times show as HH:mm. The empty text becomes "No evidence on this day."
- Clicking the same cell again, or ✕ ALL, clears the selection. Padding and future cells are transparent, not clickable and have no hand cursor.
- The selection survives Command Center refreshes. It is dropped if the day leaves the 365-day window.

### 3. Standing projects have no progress

- **Domain**:
  - `ProjectProgress.Tracks(project)` is `!IsStanding`.
  - `ProjectProgress.For(project)` returns null for standing projects; otherwise it calls `Calculate`.
  - `Project.ProgressPercent` now uses `For`.
  - The new derived `Project.HasProgress` is EF-ignored, like `ProgressPercent`.
- **Command Center**:
  - Active Projects rows for standing projects show a faint `STANDING` tag instead of the bar and %.
  - Standing projects are excluded from the PROJECT PROGRESS tab strip.
  - The default selection is the first non-standing active project.
  - The whole PROJECT PROGRESS panel is hidden when the selected project is standing (e.g. you clicked Life) or when no non-standing active project exists.
- **Projects page**: the list hides the bar for standing projects. The editor shows "Standing project — it never completes, so it has no progress." instead of the bar.

## Decisions / deviations

- **Reconcile on read** rather than a hook in TaskService and ReminderSyncService. It is one code path, works for both completion routes, and needs no cross-service coupling. The cost is that the auto-keep evidence is written on the next read, timestamped at the task's completion, so the graph still credits the right day. Every Command Center refresh, check-in load and check-in save reads.
- **Carry over marks the old row Missed.** Honest: it wasn't kept that week. The carried text becomes a new Open commitment for the new week.
- **Re-submitting a check-in in the same week Drops** that week's earlier still-Open commitments. Kept ones stay. This is the only use of `Dropped` so far.
- **Manual keep also works for linked commitments at the service level**, but the UI offers the checkbox only on unlinked ones, as specified. There is no un-keep.
- **Review choices from the check-in**: a "KEPT" choice on an unlinked commitment writes the same single `CommitmentKept` evidence item.
- **"Events" in the day header** counts every evidence row shown. The graph's tooltip counts only weight > 0 rows. They differ only for distraction rows, which nothing produces today.
- The link glyph is `↪`, because JetBrains Mono has it; 🔗 would rely on emoji fallback.

## Verification

- `dotnet build LTFI.sln --no-incremental`: 0 errors, 0 warnings, LTFI.App included, so the compiled XAML bindings were checked.
- `dotnet test LTFI.sln`: 131 pass. The 10 new `CommitmentTests` cover:
  - the legacy Q5 split and join, the 5-pt weights and the week date;
  - creation from a check-in (1..3, blanks dropped, Q5 joined, stale link dropped);
  - auto-keep after an LTFI completion (evidence once, carries project and task);
  - auto-keep after a reminders **sync** completion (evidence at the completion time, `#area` shown);
  - manual keep idempotency and 5 pts;
  - next-week review (Kept / Missed / carry-over with the link kept, unresolved → Missed, review empty afterwards);
  - a same-week re-submit dropping open commitments;
  - the Q5 backfill (split, max 3, idempotent, last week untouched);
  - single-local-day evidence (day boundaries, newest first);
  - standing progress null vs a normal project at 100%.
- I launched the app against the owner's real DB and only clicked graph cells, navigation, ✕ ALL and a picker I closed with Esc. What I saw:
  - The migration applied, and the log has no ERR or WRN lines from these runs.
  - The backfill created **1** commitment from the owner's v1 check-in. Its Q5 is one comma-separated line, which is not split on commas.
  - The Command Center showed `0 / 1 KEPT` with a checkbox, Life showed `STANDING` with no bar, and the PROJECT PROGRESS panel was hidden because only Life is active.
  - Clicking Sep 28 outlined the cell, and the feed showed `MON SEP 28 · 6 EVENTS · 60 PTS`, which matches the best day. ✕ ALL restored the full feed.
  - The CHK page showed the 3 commitment rows, and the picker listed e.g. `kiewit · #job · due OCT 17`.
  - The Projects editor for Life showed the "no progress" text.

## Deferred

- The coach's Accept / Edit / Drop on its suggested commitments (waits for the coach to be unpaused).
- Editing a commitment after the check-in, or re-linking it.
- A per-project filter on the graph (the other half of plan §0.1 item 4).
