# Phase 1 — Foundation, domain model, persistence, app shell (2026-06-28)

Implements Phase 1 of [`../LTFI_7_Phase_Development_Plan.md`](../LTFI_7_Phase_Development_Plan.md).

## Solution layout (evolved from the hybrid prep)

A third project was added now that there is real persistence code to hold:

```
src/LTFI.Core/           Pure domain: entities (§4/§5) + enums + service port interfaces
src/LTFI.Infrastructure/ EF Core SQLite: LtfiDbContext, migration, DbPaths, service impls, DI
src/LTFI.App/            Avalonia UI, view models, composition root
tests/LTFI.Infrastructure.Tests/  xUnit integration tests over real SQLite
```

`LTFI.Application` / `LTFI.Platform.Windows` from the plan diagram are still deferred — no
code needs them yet. **Documented deviation:** the plan puts services in an Application
layer; for a small local app the CRUD services live in `LTFI.Infrastructure` and implement
interfaces declared in `LTFI.Core` (ports-and-adapters). Extract an Application layer in a
later phase if orchestration grows.

## Key decisions

- **EF Core via `IDbContextFactory`**, not a scoped `DbContext` — correct for a desktop app
  with no request scope. Each service call uses a short-lived context.
- **Enums persisted as strings** (`HasConversion<string>`) for readable, stable migrations.
- **DateTimeOffset is unsupported in SQLite `ORDER BY` (and in range+enum WHERE combos).**
  `TaskService.GetAllAsync` / `GetTodayAsync` therefore order/filter on the client. Fine at
  personal scale. If later phases need heavy time-based SQL queries (evidence timeline,
  focus-session analytics), introduce a `DateTimeOffset` value converter to a sortable form.
- **DB location:** `%AppData%/LTFI/ltfi.db` (+ `logs/`), created on demand (`DbPaths`).
  Migrations applied on startup via `Database.Migrate()`. Dev DB is gitignored.
- **Logging:** Serilog → rolling file in `%AppData%/LTFI/logs`, bridged into MS.Extensions
  logging. `Microsoft.Extensions.Hosting` from the plan's package list was **not** added —
  the Generic Host doesn't fit Avalonia's lifetime cleanly and would be unused; a plain
  `ServiceCollection` is the composition root instead.
- **Task model:** the earlier draft's timer/points fields (`RequiredWorkSeconds`,
  `PointsValue`, …) were dropped in favour of the plan's lifecycle-based `TaskItem`.
  Active-time tracking returns in Phase 2 via `FocusSession` (entity already persisted).

## Navigation / scope

Sidebar: Today · Projects · Tasks · Focus · Review · Settings. Today/Projects/Tasks are
functional; Focus/Review/Settings are honest placeholders pointing at their phase.

Entities persisted now: Project, TaskItem, SubtaskItem, TaskLabel, FocusSession, Milestone,
EvidenceItem, ReflectionEntry. Only Project/Task/Subtask have CRUD UI in Phase 1 (the rest
are schema-ready for later phases, per plan §1.3).

## Post-review refinements

Based on user feedback after the initial Phase 1 build:

- **Task statuses trimmed** from the plan's 8 (§4.3) to five that earn their keep:
  `Ready, InProgress, Completed, Canceled, Deferred`. New tasks default to `Ready`.
  Enums persist as strings with no DB CHECK constraint, so this needed no migration.
- **Project progress is now derived, not entered.** `Project.ProgressPercent` is a computed
  property (`ProjectProgress.Calculate`): each non-canceled task contributes 1.0 if Completed,
  else its fraction of completed subtasks, else 0; the project value is the average. The read
  services eager-load tasks+subtasks so it computes. The manual input was removed.
- **`DesiredOutcome` dropped** (user chose to keep only `DoneCondition`).
- Migration `SimplifyProjectFields` drops the now-unused `DesiredOutcome` and `ProgressPercent`
  columns from `Projects`.

## Verification

- `dotnet build LTFI.sln` — clean.
- `dotnet test` — 5 integration tests over real SQLite cover the acceptance criteria:
  create project, create task under a project, add/toggle/remove subtasks, data survives a
  restart (new context on the same file), and the Today filter.
- App launches, applies the migration, and creates `%AppData%/LTFI/ltfi.db` on first run.
