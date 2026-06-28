# Phase 3 — Project control & anti-sprawl (2026-06-28)

Implements Phase 3 of [`../LTFI_7_Phase_Development_Plan.md`](../LTFI_7_Phase_Development_Plan.md),
following its "Reconciliation with Phases 1–2" notes.

## What shipped

- **Active project limit** (`ProjectPolicy.MaxActiveProjects = 4`): enforced in `ProjectService`
  on the transition into `Active` (create or update), throwing `ActiveProjectLimitException`.
  The Projects page catches that and shows a **decision panel** listing active projects with
  Pause / Kill buttons (or Cancel); choosing one frees a slot and retries the blocked activation.
- **Milestones**: `MilestoneService` CRUD, surfaced as a sub-panel in the Projects editor
  (add / toggle done / remove), mirroring the subtask pattern.
- **Weekly review** (Review page replaces the placeholder): `ReviewService` computes, over the
  trailing 7 days, active count vs. limit, tasks completed, focus time, projects started/archived,
  per-active-project focus + completions, and **stalled** active projects.
- **Stalled detection**: an active project with no activity (evidence / focus end / task
  completion / fallback CreatedAt) for more than `ProjectPolicy.StaleAfterDays = 10` days.

## Key decisions

- **Limit is a constant**, not a stored setting (Settings UI is still a later-phase placeholder).
  Made user-configurable when Settings lands.
- **Single limit**, no major/minor split (deferred until needed).
- **"Archive another" dropped** from the decision screen — archiving isn't a separate action; the
  choices are Pause / Kill / Cancel.
- **No schema change this phase** — the `Milestone` entity already existed (Phase 1) and the limit
  is a constant. No new migration.
- **Review math runs in memory** after minimal projections (SQLite can't do DateTimeOffset
  range/ORDER BY), consistent with earlier phases.
- **`ActiveProjectLimitException`** (derives from `InvalidOperationException`) lets the UI react
  specifically (decision panel) while other validation errors still surface as plain messages.

## Deferred (not in Phase 3 acceptance)

- The **reflective kill ritual** (why ending / what learned / move tasks) — capture as a
  `ReflectionEntry`; still owned by Phase 3 but out of scope here.
- The **timed reactivation cooldown** for killed projects (currently a hard block) — wants
  calendar support (Phase 5).
- **Task ↔ milestone assignment UI** (`TaskItem.MilestoneId` exists) — small follow-up.
- A dedicated project **detail page** — folded the project-health view into the Review page.

## Verification

- `dotnet build LTFI.sln` clean; `dotnet test` — 27 tests pass, incl. active-limit (blocks the
  5th, frees on pause), milestones CRUD, weekly review summary, and stalled detection (seeded
  old project). Fresh-DB app run starts clean with the new DI graph.
