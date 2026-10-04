# Weekly check-in v0 — mandatory, deterministic (2026-10-03)

First slice of the owner's "more guidance and structure" priority: a mandatory weekly check-in
with a fixed question list. Deterministic, with no LLM. The LLM coach is a separate workstream and
can later read these entries; `ReflectionEntry.StructuredSummaryJson` is still unused and kept for it.

## What shipped

- **`WeeklyCheckIn` (Core/Domain)**: the versioned question list (`PromptVersion = "weekly-checkin-v1"`,
  6 questions), `WeekStart`, `IsDue`, the snooze rules (`Snooze`, `IsSnoozed`, `SnoozesRemaining`,
  `MustShow`), `BuildAnswers` validation, and JSON (de)serialization of `[{question, answer}]`.
- **Policy constants** in `ProjectPolicy`: `WeeklyCheckInDay = Sunday` (00:00 local),
  `MaxCheckInSnoozesPerWeek = 2`, `CheckInSnoozeHours = 3`.
- **`IReflectionService` / `ReflectionService`**: save a check-in (a `ReflectionEntry` with
  `ScopeType = Week`, `Prompt = weekly-checkin-v1`, Body = the answers as JSON), get the latest one,
  get history, get the gate status, and snooze. Saving also writes `ReflectionSubmitted` evidence
  (10 pts), mirroring how `TaskService` records evidence.
- **`ICheckInSnoozeStore` / `JsonCheckInSnoozeStore`**: snooze state in
  `%AppData%/LTFI/checkin-snooze.json`. A missing or corrupt file counts as "no snoozes taken".
- **Check-In page** (`CHK` rail item, `CheckInView`): a status line, this week's review numbers
  (focus, tasks done, stalled, active/limit from `IReviewService`), the 6 questions as multi-line
  boxes, Submit, `SNOOZE 3H · N LEFT`, and the last 5 check-ins (commitments first).
- **Gate** in `MainWindowViewModel.CheckGateAsync`: runs at launch, on every 15s header refresh, and
  after a submit or snooze. When a check-in is due and not snoozed, it switches to the Check-In page
  and disables the rail and Settings (`IsNavEnabled`).

## Key decisions

- **No migration.** The `Reflections` table already existed, and snooze state lives in a JSON file.
- **Friction, not punishment.** You can always snooze until the weekly cap. After that, only
  submitting clears the gate. Only the commitments answer (Q5) has to be non-empty; every other
  answer can be blank.
- **Evidence once per week.** Only the first check-in of a week earns `ReflectionSubmitted`, so
  re-submitting can't farm points. Re-submitting is still allowed and saved.
- **Answer drafts survive navigation and refresh.** `RefreshAsync` leaves them alone and only a
  successful submit clears them. Drafts are in memory only and are lost if the app closes.
- **Injectable clock.** `ReflectionService` takes an optional `TimeProvider`, defaulting to the
  system clock, so tests can pin "now". The DI container falls back to the default.
- The week boundary uses `now`'s UTC offset, so across a DST switch it can be an hour off. That
  doesn't matter for a weekly prompt.
- A brand-new install is due right away. It's mandatory, and the user can snooze.

## Verification

- `dotnet build LTFI.sln`: clean, 0 warnings. `dotnet test LTFI.sln`: **34 pass** (27 before + 7 new
  in `WeeklyCheckInTests`). The new tests cover the question list and version, the week boundary,
  due logic, the snooze cap and weekly reset, no snooze when nothing is due, answer validation and
  JSON round trip, and a service round trip on real SQLite (snooze persists across service
  instances, evidence is written once, history is ordered, and the check-in falls due again next week).
- Launched the built app against the real DB for about 9s: no startup crash, and no ERR/WRN lines in the log.

## Deferred

- LLM coach analysis of check-ins (separate agent). It can fill `StructuredSummaryJson`.
- Showing check-ins on the Review page. For now they appear only on the Check-In page.
- Feeding last week's commitments into Today/Command Center as reminders. They could map to iCloud
  Reminders once that sync exists.
- Making the check-in day, snooze count and snooze length configurable on the Settings page.
- Saving the draft to disk so it survives an app restart.
