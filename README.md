# LTFI

A local-first personal **operations system** — a dense, dark, command-center–style
dashboard for taking control of projects, tasks, focus sessions, and concrete
evidence of progress. LTFI is not just a todo list: its goals are situational
awareness, operational control, evidence-based progress tracking, anti-sprawl on
projects, and regular review loops.

> **Status:** early development. The app builds and runs with an in-memory task
> planner (Today view + task planner). Persistence and the broader feature set are
> being built out per the phased plan below.

**Stack:** C# / .NET 9 · Avalonia UI · MVVM (CommunityToolkit.Mvvm). Local-first by
default; SQLite persistence and all integrations (Git/GitHub, Logseq, LLM, focus
enforcement) arrive in later phases and are optional.

## Repository layout

```
LTFI.sln
src/
  LTFI.App/    Avalonia UI: Views, ViewModels, Services, app shell
  LTFI.Core/   Pure domain models (no UI / DB / OS dependencies)
docs/
  LTFI_7_Phase_Development_Plan.md   Authoritative development plan
  agent-notes/                       Decisions & assumptions log
  archive/                           Superseded plans
tests/         Test projects (added in Phase 1)
```

Additional layered projects (`LTFI.Application`, `LTFI.Infrastructure`,
`LTFI.Platform.Windows`) and the `integrations/` (browser extension, Logseq plugin)
and `experiments/` trees from the plan are introduced when there is real code to put
in them — see [docs/agent-notes](docs/agent-notes/).

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
