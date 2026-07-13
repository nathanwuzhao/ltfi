# Command Center UI + visual-system lock (2026-07-13)

Imports the **"LTFI Command Center"** design (Claude Design doc, option `1a` — the reflow grid)
and turns it into the app's real visual identity, before the UI grows further on the generic
Avalonia template look. Nothing in the 7-phase plan changed; this is a presentation-layer pass
plus one small read-only service.

## What shipped

- **New visual system** (App.axaml + Styles/Controls.axaml): near-black command-center palette
  (`#090B10` grid / `#101319` panels / `#23272F` borders), **status-only colour** (green
  `#46D17F`, amber `#E6A13A`, red `#E5484D` — the old blue accent is gone), and **JetBrains Mono**
  bundled in `Assets/Fonts` (falls back to Cascadia Mono / Consolas). Reusable classes: `.panel`,
  `.panel-hd`, `.micro`/`.micro-dim` (tiny tracked labels), text ramp (`.bright/.muted/.dim/.faint`),
  `.chip`, `.rail`/`.rail-item`, `Button.primary/.ghost/.danger/.row/.hd`, `ProgressBar.meter`.
- **New shell** (MainWindow + MainWindowViewModel): a 36px top status bar (LTFI wordmark · live
  date/clock · PTS · STREAK · ACTIVE `▮▮▮▯` meter) over a **46px icon rail**
  (CMD · TDY · PRJ · TSK · FOC · REV, Settings pinned at the bottom). Header numbers refresh on
  navigation and every ~15s; the clock ticks each second.
- **Command Center page** (`CommandCenterViewModel` / `CommandCenterView`) — the new landing page,
  every panel wired to real local data:
  - **Current Operation** — live focus clock/task/intent from `IFocusSessionService`; RUN pauses/
    resumes inline, FINISH hands off to the Focus page (`OpenFocusRequested`).
  - **Focus Debt** — this-week focus hours vs `ProjectPolicy.WeeklyFocusTargetHours` (new constant,
    15h), focus streak, tasks/wk (from the weekly review + insights).
  - **Active Projects** — derived `ProgressPercent`, weekly focus, and an evidence-derived risk
    (on track / no evidence / stalled). Clicking a row drives the progress panel.
  - **Upcoming** — task due dates + project target dates, colour-coded by slack.
  - **Evidence Feed** — recent evidence with type tags (new read service, see below).
  - **Weekly Commitments** — clearly-marked **PREVIEW** stand-in over top open tasks; a real
    commitments loop is a later phase.
  - **Project Progress** — evidence-derived cumulative-progress trend (SVG-equivalent
    `Path`/`Geometry` in a `Viewbox`) + a 90-day daily-activity heatmap lane.
- **`IEvidenceService`** (Core) + `EvidenceService` (Infra): the only new backend — read-only
  `GetRecentAsync`, `GetDailyActivityAsync`, `GetProjectDailyActivityAsync`. Same in-memory
  time-filtering pattern as the other services (SQLite can't range/order `DateTimeOffset`).
- **Existing pages reskinned for free** — Today/Projects/Tasks/Focus/Review/Placeholder consume the
  shared style classes + `DynamicResource` brushes, so they picked up the new palette/type with no
  per-view rewrites.

## Key decisions / gotchas

- **Built entirely in Avalonia** (no MAUI). The design's SVG charts map to `Path` + `Geometry`,
  the heatmap/lanes to small coloured `Border`s in a `UniformGrid`. Computed visualisation colours
  live as frozen `IBrush`es in the App-layer VMs (`CcBrush`) — the VMs already reference Avalonia
  (e.g. `DispatcherTimer`), so this stays consistent and avoids a pile of value converters.
- **Layout**: the design's 3-column grid with several column-**spanning** panels caused Avalonia's
  star columns to inflate to each spanning child's desired width (~1500px) and overflow. Fixed by
  restructuring into a **2-column layout** (wide left stack / narrow right stack) with the
  full-width progress panel below — no spanning, columns resolve proportionally. A `SizeChanged`
  `MaxWidth` pin on the root stack is kept as belt-and-suspenders.
- **Progress trend is honest**: derived from cumulative evidence over 30 days, normalised to the
  current derived `ProgressPercent` (no stored history yet). The dashed "milestone target" is the
  next 25% band — a visual guide, not a fabricated milestone %.

## Verification

- `dotnet build LTFI.sln` clean; `dotnet test` — 27 tests pass (no backend behaviour changed).
- Launched the app and screenshotted (empty DB → correct empty states; seeded DB → populated panels,
  progress bars, evidence feed with typed tags, upcoming deadlines, and the rendered trend chart).
  Fits cleanly at the design's ~1360–1400 width.

## Deferred / follow-ups

- Panel expand/collapse exists for Current Operation / Focus Debt / Active Projects; the design's
  full inspector-overlay (`1b`) and the standalone activity views (`2a/2b/2c`) are not built.
- Weekly Commitments needs a real entity; Evidence timeline per-project (Phase 4) will supersede
  the feed's placeholder session log.
- Stalled risk currently keys off the weekly review's stalled set; revisit once `LastActiveAt`/
  evidence-based "last activity" is finalised.
