# Contribution graph — the Command Center headline (2026-10-03)

The owner named a GitHub-style contribution graph "the star of the show". This ships it as a
full-width **CONTRIBUTIONS** panel at the top of the Command Center, driven by all evidence. No
schema change; one new read method.

## What shipped

- **Domain** (`LTFI.Core/Domain/ContributionGraph.cs`, pure): `ContributionGraph.Build(scores, today,
  days = 365)` lays the trailing window out as **Sunday-start week columns × 7 weekday rows**
  (53–54 columns). Cells are `Day`, `BeforeRange` (leading padding) or `Future` (after today, last
  column only). Also produces month labels and `ContributionStats` (total points/events, active
  days, current streak, longest streak, best day). Supporting records: `DayScore`,
  `ContributionCell`, `ContributionWeek`.
- **Weighting** (`EvidencePoints.ForContribution` in `Scoring.cs`): scored types use their points
  (task 10, reflection 10, focus 5, subtask 2); every other type counts **at least 1** so imported
  evidence (git, future iCloud Reminders) still lights a cell. Distraction signals weigh 0.
- **Infra**: `IEvidenceService.GetDailyScoresAsync(days)` → one `DayScore` per day oldest-first,
  zeros included. Same in-memory time filtering as the rest of the service (SQLite/`DateTimeOffset`).
- **UI** (`CommandCenterView.axaml` / `CommandCenterViewModel.cs`): month labels, Mon/Wed/Fri row
  labels, 11px cells / 3px gaps / 2px rounding in the existing `CcBrush.Heat0–4` ramp, Less→More
  legend, per-cell tooltip (`"22 pts · 4 events — Mon Jul 13, 2026"`), header summary + best day,
  and a 2×2 stat strip (YEAR TOTAL · CURRENT STREAK · LONGEST STREAK · ACTIVE DAYS). Reloaded in
  `RefreshAsync`, so it updates with every Command Center refresh.

## Key decisions / gotchas

- **Levels = GitHub-style quartiles** of the non-zero days (nearest-rank 25/50/75th percentile),
  with one tweak: the window's max is always level 4, so a single active day (or all-equal days)
  shows bright instead of the dimmest green.
- **Month labels** go on the first column whose first in-window day is in a new month (GitHub's
  rule — so on Sat Oct 3 there is no "Oct" yet; the column starts Sep 27). A label is dropped when
  the next one is < 3 columns away so a partial first column never collides.
- **Layout**: graph is a fixed-width `Auto` column (~770px), stats take the `*` column — no column
  spanning (see the 2026-07-13 gotcha). Month text lives in a non-clipping `Canvas` so "Sep" can
  overhang its 11px column. Fits at 1360 and at the 1120 min width.
- Streak semantics reuse `Streaks.ConsecutiveDays` (today not yet active doesn't break it).

## Verification

- `dotnet build LTFI.sln` clean (0 warnings); `dotnet test LTFI.sln` — **40 passed** (27 → 40: grid
  alignment ×3 todays, quartile levels, stats/streaks/best day, empty history, month labels,
  contribution weights ×5, and the `GetDailyScoresAsync` service test).
- Launched the app against the real DB and screenshotted: panel renders at the top with real data
  (108 pts / 41 events, 23 active days, longest streak 3d), no horizontal overflow; killed after.

## Deferred / follow-ups

- Click-a-day drill-down (filter the evidence feed to that day) and a per-project filter on the graph.
- Year picker / calendar-year view (currently trailing 365 days only).
- Once iCloud Reminders import lands, consider giving `ReminderCompleted`-style evidence a real
  point value instead of the floor of 1.
