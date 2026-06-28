# Repo structure prep — 2026-06-27

Cleanup pass to ready the repository for **Phase 1** of
[`docs/LTFI_7_Phase_Development_Plan.md`](../LTFI_7_Phase_Development_Plan.md).
The repo had accumulated slop (empty stub files, two competing plans, an empty
README). This note records what changed and why, per Coding Agent Instruction #12
("Document assumptions when repository structure differs from this plan").

## Project layout decision: Hybrid (src/ + Core)

Phase 1 task 1.1 requires deciding between a single project and layered projects.
We chose a **hybrid**: adopt the plan's `src/` layout now, but only create projects
that hold real code, to honor Principle 2.5 ("do not build a large number of empty
services or placeholder abstractions").

```
src/
  LTFI.App/      Avalonia UI + ViewModels + Services (moved from /LTFI)
  LTFI.Core/     Pure domain — no Avalonia/EF/OS dependencies
```

- `LTFI.Core` currently holds `Domain/TaskItem.cs` and `Domain/Enums.cs`,
  namespace `LTFI.Core.Domain`. It has **no** package references by design.
- `LTFI.Application`, `LTFI.Infrastructure`, and `LTFI.Platform.Windows` from the
  plan's target diagram are **deliberately not created yet**. They will be split out
  when Phase 1 produces real code to put in them (e.g. EF Core persistence →
  `LTFI.Infrastructure`). Creating them empty now would be the slop we are removing.

## What moved / changed

- App project moved `LTFI/` → `src/LTFI.App/`; `LTFI.csproj` → `LTFI.App.csproj`.
  `RootNamespace` pinned to `LTFI` so XAML `x:Class` (e.g. `LTFI.App`,
  `LTFI.Views.MainWindow`) resolves without touching every `.axaml`.
- Domain models moved to `src/LTFI.Core/Domain/`; namespace `LTFI.Models` →
  `LTFI.Core.Domain`. Consumers updated to `using LTFI.Core.Domain;`.
- `TaskDraft` stayed in the App project (`LTFI.Models`) — it is an edit/form model
  used by the planner VM and service, not a persisted domain entity.
- `TaskService` stayed in the App project for now. It is application/orchestration
  logic and will migrate to `LTFI.Application` when that project is created.
- Solution regenerated with both projects.

## Deleted slop (empty 0-byte tracked stubs)

`Models/SubtaskItem.cs`, `Models/Label.cs`, `Models/TimerSession.cs`,
`Models/PointEvent.cs`, `Data/AppDbContext.cs`, `Services/PointService.cs`,
`Services/RecurrenceService.cs`, `Services/TimerService.cs`.

Several (`Label`, `PointEvent`, `TimerSession`, `RecurrenceService`) reflected the
*superseded* Donetick-style plan rather than the mission-control domain vocabulary
(`Project`, `FocusSession`, `EvidenceItem`, `Milestone`, `ReflectionEntry`). Phase 1
re-introduces real versions of the entities it actually needs.

## Plans

- `LTFI_7_Phase_Development_Plan.md` is the **authority**; moved into `docs/`.
- The old `PLAN.md` (gitignored, Donetick-style) was archived to
  `docs/archive/donetick-plan-superseded.md` and its `.gitignore` entry removed.

## What is intentionally NOT done here

This pass is structural prep only — no Phase 1 features were implemented. Still open
for Phase 1: baseline packages (EF Core SQLite, DI, Hosting, Serilog), SQLite
persistence, the remaining core entities, the full sidebar shell
(Today/Projects/Tasks/Focus/Review/Settings), and the dark command-center styles.
The app currently runs on in-memory seed data in `TaskService`.
