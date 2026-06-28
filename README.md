# LTFI

A local-first personal **operations system** — a dense, dark, command-center–style
dashboard for taking control of projects, tasks, focus sessions, and concrete
evidence of progress. LTFI is not just a todo list: its goals are situational
awareness, operational control, evidence-based progress tracking, anti-sprawl on
projects, and regular review loops.

> **Status:** Phases 1–2 complete. Dark command-center shell, local SQLite persistence,
> project/task/subtask CRUD, focus sessions with a live timer and end-of-session review,
> evidence-on-completion, and a Today cockpit with points and a focus streak. Project
> control, reviews, and integrations follow in later phases.

**Stack:** C# / .NET 9 · Avalonia UI · MVVM (CommunityToolkit.Mvvm). Local-first by
default; SQLite persistence and all integrations (Git/GitHub, Logseq, LLM, focus
enforcement) arrive in later phases and are optional.

## Repository layout

```
LTFI.sln
src/
  LTFI.App/            Avalonia UI: shell, Views, ViewModels, composition root
  LTFI.Core/           Pure domain: entities, enums, service interfaces (no UI/DB/OS deps)
  LTFI.Infrastructure/ EF Core SQLite: DbContext, migration, services, DI wiring
docs/
  LTFI_7_Phase_Development_Plan.md   Authoritative development plan
  agent-notes/                       Decisions & assumptions log
  archive/                           Superseded plans
tests/
  LTFI.Infrastructure.Tests/         Service integration tests over real SQLite
```

Further layered projects (`LTFI.Application`, `LTFI.Platform.Windows`) and the
`integrations/` (browser extension, Logseq plugin) and `experiments/` trees from the plan
are introduced when there is real code to put in them — see
[docs/agent-notes](docs/agent-notes/).

Data lives at `%AppData%/LTFI/ltfi.db` (with rolling logs under `%AppData%/LTFI/logs`).
Run the tests with `dotnet test`.

## Build & run

Requires the .NET 9 SDK.

```bash
dotnet build LTFI.sln          # build everything
dotnet run --project src/LTFI.App   # launch the desktop app
```

## Development plan

All work follows the 7-phase plan in
[docs/LTFI_7_Phase_Development_Plan.md](docs/LTFI_7_Phase_Development_Plan.md).
Key principles: local-first, evidence over vibes, small vertical slices, and
human-confirmed automation. **Current focus: Phase 1** — foundation, core domain
model, local SQLite persistence, and the dark app shell.
