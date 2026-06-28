# LTFI — 7-Phase Development Plan

> **Document status:** Planning authority for future LTFI development.  
> **Audience:** Human developer + coding agents working on the LTFI repository.  
> **Primary stack:** C# / .NET / Avalonia / MVVM.  
> **Future stack:** JavaScript/TypeScript for browser extensions and Logseq plugin bridges; possible OCaml for rule/constraint modeling or experimental planning logic.  
> **Product concept:** A local-first personal operations system: a “Palantir-style” command center for personal projects, tasks, focus sessions, evidence of progress, reflection, and anti-procrastination controls.

---

## 0. Project North Star

LTFI is not just a todo list. It is intended to become a **personal mission-control system** for taking control of work, school, projects, habits, and focus.

The user struggles with procrastination, overcommitting to too many projects, context switching, and feeling behind compared to peers. LTFI should not simply shame, punish, or gamify productivity in a shallow way. The core purpose is to provide:

1. **Situational awareness** — What am I doing, what is active, what is stalled, what is actually moving?
2. **Operational control** — What should I work on now, what should be paused, what environment should be opened, what distractions should be blocked?
3. **Evidence-based progress tracking** — Did I actually move the project forward? What commits, notes, focus sessions, task completions, or reflections prove it?
4. **Project anti-sprawl** — Prevent endless enthusiasm-driven project creation without completion.
5. **Daily and weekly review loops** — Make progress visible over time and identify plateaus.

The long-term aesthetic goal is a dense, dark, operational-intelligence-style dashboard: structured, serious, compact, and data-rich, inspired by command-center / Palantir-like interfaces without copying any proprietary product.

---

## 1. Known Current Context

The repository is assumed to be a new .NET Avalonia MVVM application. Prior context indicates:

- The repository is named **LTFI**.
- The project was created using an **Avalonia MVVM template**.
- `CommunityToolkit.Mvvm` was installed via the .NET CLI.
- Template-generated files such as `ViewLocator.cs`, `app.manifest`, and `LTFI.csproj` may exist and should be preserved unless intentionally replaced.
- There may not currently be a `.sln` file; creating one is acceptable if useful.
- Earlier discussion considered starting with domain models such as `TaskItem.cs` rather than building the final UI immediately.

This document supersedes any earlier rough plan. Existing repository structure should be adapted to this plan, but the plan does **not** require deleting template files just because they were not named here.

---

## 2. Development Principles

### 2.1 Local-first by default

LTFI should work without cloud accounts. Store primary data locally first, likely using SQLite. External services such as GitHub, Logseq local files, LLM APIs, calendar APIs, or browser extensions should be optional integrations.

### 2.2 Evidence over vibes

The app should avoid vague motivational scoring. Prefer concrete signals:

- Focus session started/completed
- Task time started/completed
- Subtask completed
- Git commit made
- Pull request opened/merged
- Logseq journal entry created
- Reflection submitted
- Project milestone advanced
- Distracting site override requested

### 2.3 Friction, not self-hostile punishment

Blocking and penalties should be designed as friction mechanisms, not malware-like lockdown. The system should make distraction and project switching explicit, logged, and inconvenient, but not destructive.

### 2.4 Human-confirmed automation

LLM features should suggest changes, not silently mutate the user’s productivity state. For example, an LLM may suggest that a project moved from 18% to 22%, but the user should confirm or edit that update.

### 2.5 Small vertical slices

Coding agents should prefer complete, testable slices over broad skeletons. For example:

- Create project model → persist project → display project → edit project
- Create focus session → start timer → stop timer → save evidence record

Do not build a large number of empty services or placeholder abstractions without proving at least one end-to-end workflow.

---

## 3. Suggested Repository Structure

This structure is a recommendation. If the current Avalonia template differs, migrate carefully and preserve working files.

```text
LTFI/
  LTFI.sln
  README.md
  docs/
    LTFI_7_Phase_Development_Plan.md
    architecture/
    product/
    agent-notes/
  src/
    LTFI.App/
      LTFI.App.csproj
      App.axaml
      App.axaml.cs
      Program.cs
      ViewLocator.cs
      app.manifest
      Assets/
      Styles/
      Views/
      ViewModels/
      Controls/
      Converters/
    LTFI.Core/
      LTFI.Core.csproj
      Domain/
      ValueObjects/
      Enums/
      Rules/
      Events/
    LTFI.Application/
      LTFI.Application.csproj
      Services/
      UseCases/
      DTOs/
      Interfaces/
    LTFI.Infrastructure/
      LTFI.Infrastructure.csproj
      Persistence/
      Repositories/
      Integrations/
      FileSystem/
      Git/
      Logseq/
      GitHub/
      Llm/
    LTFI.Platform.Windows/
      LTFI.Platform.Windows.csproj
      Startup/
      ProcessControl/
      AppBlocking/
      NativeInterop/
  tests/
    LTFI.Core.Tests/
    LTFI.Application.Tests/
    LTFI.Infrastructure.Tests/
  integrations/
    browser-extension/
      package.json
      src/
    logseq-plugin/
      package.json
      src/
  experiments/
    ocaml-rules/
```

### Notes

- `LTFI.App` should contain Avalonia UI only.
- `LTFI.Core` should contain pure domain logic and should not depend on Avalonia, SQLite, GitHub, Logseq, or OS APIs.
- `LTFI.Application` should contain use cases/orchestration.
- `LTFI.Infrastructure` should contain concrete persistence and integration implementations.
- `LTFI.Platform.Windows` should isolate Windows-specific features such as startup registration, app blocking, process monitoring, and OS interop.
- Browser extension and Logseq plugin code will likely be JavaScript/TypeScript, not C#.
- OCaml is optional and should remain experimental until there is a clear reason to use it.

---

## 4. Core Domain Vocabulary

### 4.1 Entities

Initial domain entities should include:

- `Project`
- `TaskItem`
- `SubtaskItem`
- `TaskLabel`
- `FocusSession`
- `EvidenceItem`
- `ReflectionEntry`
- `Milestone`
- `ProgressUpdate`
- `WorkspaceProfile`
- `FocusMode`
- `DistractionEvent`
- `IntegrationAccount`
- `ExternalLink`

### 4.2 Project states

Projects use a small, explicit set of lifecycle states (trimmed from the original eight to the
five that carry their weight):

```text
Idea
Active
Paused
Completed
Killed
```

`Killed` is not a failure state — it means the user intentionally ended a project to preserve
focus. **Archived is not a status; it is derived:** a project is archived once it is `Completed`
or `Killed`, and archived projects drop out of the active lists. Completing or killing a project
archives it immediately. Killing additionally blocks reactivation (the timed/calendar-aware part
of that block is Phase 3 — see §3.5).

### 4.3 Task states

Trimmed to five (as built in Phase 1):

```text
Ready
InProgress
Completed
Canceled
Deferred
```

A task may carry a **required focus time** — a minimum amount of tracked focus-session time
that must accumulate before it can be marked `Completed`. Completing a task archives it (drops
it from the active task list).

### 4.4 Evidence item types

Suggested evidence types:

```text
ManualNote
FocusSessionCompleted
TaskCompleted
SubtaskCompleted
GitCommit
GitHubPullRequest
GitHubIssueClosed
LogseqJournalEntry
FileChanged
ReflectionSubmitted
DistractionBlocked
DistractionOverride
CalendarEventCompleted
```

---

# Phase 1 — Foundation, Domain Model, Persistence, and App Shell

## Goal

Create a stable technical foundation and local-first data model. This phase should produce a running Avalonia desktop app with a basic dark shell, local persistence, and core domain models.

## Major outcomes

- Working Avalonia app launches reliably.
- Basic MVVM architecture is in place.
- Core domain entities exist.
- Local SQLite persistence works.
- User can create, view, edit, and delete basic projects and tasks.
- App has an initial command-center visual identity, but not final polish.

## Tasks

### 1.1 Stabilize solution and project layout

- Create a `.sln` file if missing.
- Decide whether to keep a single project temporarily or split into layered projects.
- Prefer layered projects if the repository is still early and migration cost is low.
- Preserve Avalonia template files that are necessary for app startup.
- Ensure `dotnet build` works from repository root.
- Ensure `dotnet run --project src/LTFI.App` or equivalent works.

### 1.2 Add baseline packages

Likely packages:

```bash
dotnet add package CommunityToolkit.Mvvm
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Serilog
dotnet add package Serilog.Sinks.File
```

Avalonia-specific packages may already exist from the template.

### 1.3 Define core models

Create domain models first, before advanced UI:

- `Project`
- `TaskItem`
- `SubtaskItem`
- `TaskLabel`
- `FocusSession`
- `Milestone`
- `EvidenceItem`
- `ReflectionEntry`

Keep models simple at first. Avoid over-modeling recurrence, LLM, GitHub, and blocking too early.

### 1.4 Persistence

Implement SQLite using EF Core or a lightweight repository pattern.

Minimum persistence requirements:

- Projects persist across app restarts.
- Tasks persist across app restarts.
- Subtasks persist across app restarts.
- Focus sessions can be saved, even if not fully used until Phase 2.
- Database migrations are documented.
- Database path is deterministic and user-local.

Suggested app data location:

```text
%AppData%/LTFI/ltfi.db
```

or, for development:

```text
./data/dev-ltfi.db
```

### 1.5 Initial app shell

Create a basic shell with:

- Left sidebar
- Top command/search bar placeholder
- Main content region
- Dark theme resource dictionary
- Navigation to Projects and Tasks pages

Initial navigation items:

```text
Today
Projects
Tasks
Focus
Review
Settings
```

### 1.6 Initial style direction

Create reusable styles:

- `DashboardCard`
- `SectionHeader`
- `MutedText`
- `StatusChip`
- `SidebarButton`
- `PrimaryActionButton`

Use muted dark backgrounds, thin borders, compact spacing, and clear status colors.

## Acceptance criteria

- App builds and launches.
- User can create a project.
- User can create a task under a project.
- User can add subtasks to a task.
- Data survives app restart.
- Basic sidebar navigation works.
- No external integrations are required.

## Out of scope for Phase 1

- GitHub integration
- Logseq integration
- LLM integration
- Browser blocking
- App blocking
- Advanced analytics
- Calendar sync

---

# Phase 2 — Core Planner, Task Execution, and Focus Sessions

## Goal

Build the core productivity loop: define work, start work, complete work, and save evidence.

## Major outcomes

- User can plan tasks and subtasks.
- User can begin/pause/resume/stop focus sessions.
- Focus sessions attach to projects and tasks.
- Completion creates evidence records.
- Points/streaks can be introduced in a simple, non-punitive way.

## Reconciliation with Phase 1 (as built)

Phase 1 shipped more of this than originally assumed, and made a few simplifications that
change how Phase 2 should be built. Carry these forward:

- **Task statuses are trimmed to five:** `Ready, InProgress, Completed, Canceled, Deferred`
  (no Inbox/Planned/Blocked). "Start work" = move a task to `InProgress`; starting a focus
  session against a task should do this automatically.
- **The `FocusSession`, `EvidenceItem`, `TaskLabel`, and `Milestone` entities already exist**
  and are persisted (Phase 1, §1.3). Phase 2 builds their services/UI, not the schema.
- **Project progress is derived, not stored** (`ProjectProgress.Calculate` over task/subtask
  completion). So completing tasks/subtasks/sessions advances the project % automatically —
  no separate progress write is needed.
- **Points have no separate ledger.** Derive points from `EvidenceItem` (each evidence type
  maps to a point value). This keeps "evidence over vibes" literal and avoids a `PointEvent`
  table the data model never defined.
- **The earlier per-task required-work-time/“complete after N minutes” model was removed.**
  Active-time tracking lives entirely in `FocusSession` now.
- **CRUD for tasks/subtasks and a basic Today view already exist.** Phase 2 enriches them
  rather than starting from scratch.

## Tasks

### 2.1 Task planning workflows

Largely delivered in Phase 1 (task list, task detail/edit, subtask management, priority,
status, due date, project assignment). Remaining for Phase 2:

- Project detail page (tasks + derived progress + milestones placeholder)
- Label assignment UI (entity exists; wiring deferred until it earns its place)

Task fields as implemented:

```csharp
Guid Id
Guid? ProjectId
Guid? MilestoneId
string Title
string? Description
TaskStatus Status          // Ready | InProgress | Completed | Canceled | Deferred
TaskPriority Priority
DateTimeOffset? DueAt
DateTimeOffset? ScheduledStartAt
TimeSpan? EstimatedDuration
DateTimeOffset CreatedAt
DateTimeOffset UpdatedAt
DateTimeOffset? CompletedAt
```

### 2.2 Focus session model

The `FocusSession` entity already exists from Phase 1 with the fields below (plus `NextAction`).
Two clarifications for Phase 2:

- **`Status` is the lifecycle** (`Active | Paused | Completed | Abandoned`). The end-of-session
  review outcome (Completed / Partial / Blocked) is a *separate* concern — add a small
  `FocusSessionResult` enum + nullable `Result` field rather than overloading `Status`.
- **`Duration` is persisted on pause and finish** so the session record is durable, but the
  authoritative live timer is held in memory by a singleton focus service while the app runs
  (see §2.3). Cross-restart recovery of a running session is a Phase 7 concern; on startup,
  any session left `Active`/`Paused` from a previous run is marked `Abandoned`.

```csharp
Guid Id
Guid? ProjectId
Guid? TaskId
string? Intent
DateTimeOffset StartedAt
DateTimeOffset? EndedAt
TimeSpan? Duration
FocusSessionStatus Status        // lifecycle
FocusSessionResult? Result       // review outcome (Completed | Partial | Blocked)
string? ResultSummary
string? BlockerSummary
string? NextAction
```

### 2.3 Focus page

Create a dedicated Focus page with:

- Current project selector
- Current task selector
- Intent text box
- Start button
- Pause/resume button
- Finish session button
- Abandon session button
- Timer display
- Current objective display

At session end, require a small review:

```text
What happened?
- Completed
- Partially completed
- Blocked
- Abandoned

What changed?
What is the next concrete action?
```

### 2.4 Points and streaks v0

Add simple points only after focus sessions and task completion are stable.

Points are **derived from `EvidenceItem`** (no separate ledger): each completion writes an
evidence record, and a points calculator maps evidence type → value:

- Complete task (`TaskCompleted`): +10
- Complete subtask (`SubtaskCompleted`): +2
- Complete focus session (`FocusSessionCompleted`): +5
- Finish daily review (`ReflectionSubmitted`): +10
- No heavy penalties in Phase 2

Streaks should be factual, not moralizing — computed from evidence dates (e.g. a focus streak
= consecutive days with at least one completed focus session).

Examples:

```text
Daily review streak: 5 days
Workout task streak: 12 days
LTFI focus streak: 3 days
```

### 2.5 Today page

Enrich the basic Today view from Phase 1 into a useful daily cockpit:

- Current active focus session (intent, task, elapsed, running/paused)
- Tasks due today / overdue (already present)
- Active projects count (already present)
- Points earned today + current focus streak
- Quick-start focus button (jumps to the Focus page)
- Daily review prompt placeholder

## Acceptance criteria

- User can create a task, start a focus session for it, finish the session, and see the session persisted.
- Completing tasks/subtasks creates evidence items.
- Today page shows meaningful current work.
- Timer state is not lost unexpectedly during ordinary navigation.

## Out of scope for Phase 2

- Hard blocking of apps/sites
- LLM-generated tasks
- GitHub or Logseq evidence import
- Advanced recurrence rules

---

# Phase 3 — Project Control System and Anti-Sprawl Mechanics

## Goal

Make LTFI meaningfully different from a normal task app by adding project lifecycle control, active project limits, milestones, and decision friction around starting too many things.

## Major outcomes

- Projects have explicit states and milestones.
- User can define “done conditions.”
- App warns when too many projects are active.
- Starting a new active project requires pausing, killing, or archiving another if over limit.
- Weekly review starts to become useful.

## Reconciliation with Phases 1–2 (as built)

- **Project states/archiving already done.** The five-state lifecycle, derived archiving on
  Completed/Killed, and the hard reactivation block for killed projects shipped in Phase 2.
  This phase adds the limit, milestones, review, and stalled detection on top.
- **Project fields are settled.** `DesiredOutcome` was dropped; `ProgressPercent` is derived
  from task/subtask completion (not stored). `LastActiveAt` exists but is unused — this phase
  can start using it, or derive "last activity" from evidence instead (preferred — see §3.6).
- **Single active limit, not major/minor.** Implement one limit (default 4). The major/minor
  split is deferred until it proves necessary.
- **Limit lives as a constant for now.** No Settings UI yet (that nav item is still a later-phase
  placeholder); the limit is a `ProjectPolicy` constant, made user-configurable when Settings lands.
- **"Archive another" drops out of the decision screen** — archiving isn't a separate action
  anymore (it's implied by Complete/Kill). The choices become pause / kill / cancel.

## Tasks

### 3.1 Project detail expansion

Fields are as built in Phases 1–2 (note `DesiredOutcome` removed, `ProgressPercent` derived):

```csharp
string Title
string? Description
ProjectStatus Status          // Idea | Active | Paused | Completed | Killed
string? DoneCondition
DateTimeOffset? TargetDate
DateTimeOffset CreatedAt
DateTimeOffset UpdatedAt
DateTimeOffset? LastActiveAt
DateTimeOffset? ArchivedAt
// derived: ProgressPercent, IsArchived
```

### 3.2 Active project limit

A single limit (default 4), enforced in the service. When activating a project would exceed it,
show a decision screen:

```text
You already have 4 active projects.
To activate this project, choose one:
- Pause another active project
- Kill another active project
- Cancel activation
```

This is a central product feature.

### 3.3 Milestones

Implement project milestones:

```csharp
Guid Id
Guid ProjectId
string Title
string? Description
MilestoneStatus Status
DateTimeOffset? TargetDate
int SortOrder
```

Tasks can optionally belong to milestones.

### 3.4 Project dashboard

Project health overview (folded into the Review page for now — see §3.6 — rather than a separate
dashboard):

- Active projects (and count vs. the limit)
- Paused projects (note: `Blocked` is no longer a project status)
- Projects with no activity for N days (stalled)
- Projects with focus time but no completed evidence
- Projects near target date

### 3.5 Kill/archive project ritual

Killing or archiving a project should ask:

```text
Why are you ending this project?
What did you learn?
Should any tasks be moved elsewhere?
Should linked notes/repos remain attached?
```

This should not be heavy, but should make project closure explicit and positive.

> **Status:** completing/killing already archives, and killing already hard-blocks reactivation.
> The Phase 3 build focuses on the acceptance items (active limit, milestones, weekly review,
> stalled detection). The **reflective kill ritual** above and the **timed reactivation cooldown**
> are intentionally deferred — neither is in the Phase 3 acceptance criteria, and the timed
> cooldown wants calendar support (Phase 5).

### 3.6 Weekly review v0

Weekly review should summarize:

- Focus hours by project
- Tasks completed
- Projects advanced
- Projects stalled
- New projects started
- Projects killed/archived
- Recommended active project adjustments

In Phase 3, these can be deterministic summaries from local data only.

## Acceptance criteria

- Project state transitions are implemented.
- Active project limit works.
- User can create milestones.
- Weekly review shows basic project-level summaries.
- App can identify stalled projects based on no recent evidence or focus sessions.

## Out of scope for Phase 3

- LLM advice
- GitHub evidence
- Browser/app blocking
- Calendar integration

---

# Phase 4 — Local Knowledge Integration: Logseq, Filesystem, Workspaces, and Evidence Timeline

## Goal

Connect LTFI to local knowledge/work artifacts, especially Logseq, without requiring cloud services. Begin turning LTFI into an evidence-based operations system.

## Major outcomes

- User can link projects to local folders and Logseq pages.
- Focus sessions can append logs to Logseq journals.
- LTFI can scan Logseq TODO/NOW/LATER blocks.
- LTFI can open project workspaces.
- Evidence timeline combines manual task data, focus sessions, and local knowledge events.

## Tasks

### 4.1 Workspace profiles

A `WorkspaceProfile` defines what should open for a project or focus mode.

Suggested fields:

```csharp
Guid Id
Guid? ProjectId
string Name
List<WorkspaceLaunchItem> LaunchItems
List<string> AllowedApps
List<string> AllowedFolders
List<string> AllowedUrls
```

Launch item types:

```text
Folder
File
Executable
Url
Command
VsCodeWorkspace
CursorWorkspace
TerminalCommand
```

### 4.2 Workspace launcher

Implement Windows-first launching:

- Open project folder
- Open VS Code/Cursor workspace
- Open terminal in directory
- Open documentation URLs
- Open Logseq graph or page if possible

Keep this separate from app blocking. Launching is easier and should come first.

### 4.3 Logseq graph selection

Add settings:

```text
Logseq integration enabled: true/false
Logseq graph path: local folder path
Journal format: yyyy_MM_dd.md or detected
Preferred page format: Markdown
```

Logseq graphs are local files. Treat Logseq as the user’s knowledge base and LTFI as the execution/control layer.

### 4.4 Logseq file scanner

Implement a scanner for:

```text
journals/
pages/
assets/
```

Initial parser should support simple Markdown patterns:

```markdown
- TODO Add GitHub integration #LTFI
- NOW Implement FocusSession model #[[LTFI]]
- LATER Add browser blocker [[LTFI]]
```

Do not try to fully parse all Logseq syntax in the first version. Start with practical extraction.

### 4.5 Append focus logs to journal

At the end of a focus session, optionally append:

```markdown
- [[LTFI]] #[[focus-session]]
  - Project:: [[Project Name]]
  - Task:: Task title
  - Duration:: 45 min
  - Intent:: Implement TaskSession model
  - Result:: Partial
  - Blocker:: Need to clean up bindings
  - Next:: Add persistence test
```

This should be user-configurable.

### 4.6 FileSystemWatcher

Add local file watching for:

- Linked project folders
- Logseq graph folder
- Optional notes folder

Use file events carefully. File watching can be noisy. Convert raw events into coarse evidence like:

```text
Files changed in linked project folder during focus session
Logseq page updated during focus session
```

### 4.7 Evidence timeline

Create a timeline view per project:

```text
2026-06-26 09:10 — Focus session started
2026-06-26 09:55 — Focus session completed: 45 min
2026-06-26 09:56 — Logseq journal entry appended
2026-06-26 10:02 — Task completed: Add TaskItem model
```

This will later include GitHub and browser/blocking evidence.

## Acceptance criteria

- User can link a project to a local folder.
- User can create a workspace profile and launch it.
- User can select a Logseq graph folder.
- App can append a focus-session log to today’s Logseq journal.
- App can scan simple TODO/NOW/LATER blocks from Logseq pages/journals.
- Evidence timeline displays local evidence chronologically.

## Out of scope for Phase 4

- Writing a Logseq plugin
- Full Logseq AST parsing
- Browser extension
- LLM interpretation of notes
- Cloud sync

---

# Phase 5 — External Integrations: GitHub, LLM Reflection, Calendar Hooks, and Structured Review

## Goal

Add optional cloud/API integrations that enrich LTFI’s evidence model and support structured reflection. The app should become capable of saying not just “you worked,” but “this project advanced because these concrete things happened.”

## Major outcomes

- GitHub repo activity appears as project evidence.
- Local Git repo status can be scanned.
- Daily reflection can be summarized into structured updates.
- LLM-generated task decomposition is available but user-confirmed.
- Weekly review becomes more insightful.

## Tasks

### 5.1 GitHub integration

Add optional GitHub account/repo integration.

Minimum features:

- Store GitHub username/token securely.
- Link a project to one or more GitHub repositories.
- Fetch commits for linked repos.
- Fetch issues and PRs if desired.
- Convert relevant activity into `EvidenceItem`s.

Evidence examples:

```text
GitCommit
GitHubIssueClosed
GitHubPullRequestOpened
GitHubPullRequestMerged
```

Implementation notes:

- Use `HttpClient` initially.
- Consider Octokit later, but avoid dependency bloat early.
- Store tokens using OS credential storage if possible; do not store plaintext tokens in SQLite.

### 5.2 Local Git integration

For local repos, support:

- Current branch
- Last commit date
- Uncommitted changes exist
- Files changed count
- Commit history since date

Implementation options:

- Shell out to `git` CLI.
- Use LibGit2Sharp.

Start with CLI if simpler.

### 5.3 LLM provider abstraction

Create provider interface:

```csharp
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
```

Possible providers:

- OpenAI-compatible API
- Local Ollama
- LM Studio
- Qwen-compatible local/server endpoint
- Mock provider for tests

Do not hardcode one provider into core logic.

### 5.4 Structured daily reflection

Daily reflection prompt should produce structured output, but user must confirm changes.

Input sources:

- Today’s focus sessions
- Completed tasks
- GitHub commits
- Logseq entries
- Manual text reflection

Output proposal:

```json
{
  "projectUpdates": [
    {
      "projectId": "...",
      "summary": "Implemented core task model.",
      "suggestedProgressDelta": 3,
      "nextAction": "Add TaskSession persistence.",
      "blockers": []
    }
  ],
  "projectsToPause": [],
  "risks": [
    "Too many active projects with no recent evidence."
  ]
}
```

The UI should show this as a proposal with buttons:

```text
Accept
Edit
Reject
```

### 5.5 LLM task decomposition

For a project/milestone, allow:

```text
Generate next concrete subtasks
Generate smaller first step
Identify blockers
Rewrite vague goal into done condition
```

The user must approve generated tasks before insertion.

### 5.6 Calendar integration placeholder

Calendar integration can be designed in this phase but does not need full implementation unless core features are stable.

Possible future targets:

- Google Calendar
- Microsoft Outlook / Graph
- ICS import/export

Lowest-risk feature:

- Export scheduled tasks/focus blocks to `.ics`.

### 5.7 Weekly review v1

Improve weekly review with:

- Evidence by project
- Focus time vs completed output
- Active project overload warnings
- Projects with no commits/notes/tasks despite time logged
- Suggested next week top 3 commitments
- LLM-assisted summary if enabled

## Acceptance criteria

- User can link a GitHub repo to a project.
- Recent commits appear in project evidence timeline.
- Local Git status can be scanned for a linked folder.
- User can complete a daily reflection.
- LLM can propose structured project updates, and user can accept/edit/reject them.
- Weekly review uses task, focus, Logseq, and GitHub evidence.

## Out of scope for Phase 5

- Browser blocking
- App blocking
- Full calendar two-way sync
- Autonomous LLM control
- Mobile app

---

# Phase 6 — Focus Enforcement: Browser Extension, App Blocking, Startup Intent, and Lockdown Modes

## Goal

Add optional friction mechanisms that help the user stay inside an intended work context. This phase introduces JavaScript/TypeScript for browser extension work and Windows-specific C# for app/process controls.

## Major outcomes

- LTFI can launch on computer startup.
- User can be prompted to declare intent when starting their computer.
- Browser extension can block distracting domains during focus sessions.
- App can detect or close disallowed processes during strict focus modes.
- Overrides are logged rather than hidden.

## Tasks

### 6.1 Startup intent prompt

Add setting:

```text
Open LTFI on login: true/false
Require startup intent: true/false
```

Startup intent screen asks:

```text
What are you doing first?
- Deep Work
- Homework
- Project
- LeetCode
- Admin
- Leisure
- Skip
```

This should not require perfect planning. The point is to prevent unintentional drift immediately after opening the computer.

### 6.2 Focus modes

Define configurable focus modes:

```csharp
FocusMode
- Name
- Description
- DefaultDuration
- AllowedApps
- BlockedApps
- AllowedDomains
- BlockedDomains
- RequiresIntent
- AllowsOverride
- OverrideCooldown
```

Suggested modes:

```text
Deep Work
Homework
Coding
Research
LeetCode
Admin
Leisure
Shutdown Review
```

### 6.3 Browser extension architecture

Browser extension likely uses JavaScript/TypeScript.

High-level architecture:

```text
Avalonia app / local background service
    ↕ local HTTP/WebSocket/native messaging
Browser extension
    ↕
Browser tabs and navigation events
```

Initial browser features:

- Read current focus mode from local LTFI service.
- Block configured domains during focus.
- Show redirect/block page with current objective.
- Allow override if configured.
- Log attempted distraction and override reason.

Suggested extension folders:

```text
integrations/browser-extension/
  manifest.json
  package.json
  src/background.ts
  src/content.ts
  src/block-page.ts
  src/options.ts
```

### 6.4 Local communication bridge

Options:

1. Local HTTP server in C#.
2. Local WebSocket server in C#.
3. Browser native messaging host.

Start with local HTTP/WebSocket during development because it is easier to debug. Consider native messaging later for production polish.

### 6.5 App/process blocking

Windows-specific process control belongs in `LTFI.Platform.Windows`.

Features:

- Detect running processes.
- Compare against blocked list for active focus mode.
- Warn user first.
- Optionally close or kill process.
- Log event.

Do not build this as an always-hostile system. Make the behavior explicit in settings.

Blocking levels:

```text
Off
Warn only
Log only
Close after warning
Strict close immediately
```

### 6.6 Override system

Distraction blocking should support intentional override:

```text
Override requested
Reason required: true
Duration: 5 / 10 / 15 minutes
Cost: optional points penalty
Logged to evidence timeline: true
```

This converts distraction into an explicit decision.

### 6.7 Safety and recovery

Implement emergency recovery:

- Global disable shortcut or setting.
- Safe mode launch argument: `--safe-mode` disables blockers.
- Never block system-critical processes.
- Never make the app difficult to uninstall.
- Never corrupt browser or OS settings.

## Acceptance criteria

- LTFI can optionally start on login.
- Startup intent prompt works.
- Focus modes are configurable.
- Browser extension can block at least one configured domain while focus mode is active.
- Distraction attempts appear in evidence timeline.
- Windows process warning/blocking works in a controlled test mode.
- Safe-mode escape exists.

## Out of scope for Phase 6

- Mobile blocking
- Cross-platform app blocking
- Enterprise-level device management
- Unbypassable lockdown
- Kernel-level or invasive controls

---

# Phase 7 — Advanced Intelligence, Sync, Plugin Ecosystem, OCaml Experiments, and Product Polish

## Goal

Mature LTFI into a polished, extensible personal operations platform with stronger analytics, optional sync, deeper integrations, and experimental intelligent planning/rule systems.

## Major outcomes

- Dashboard becomes genuinely useful and polished.
- Analytics can detect plateaus and project overload.
- LTFI can generate review reports.
- Optional Logseq plugin can provide deeper interaction.
- Optional OCaml component can be explored for rules/planning if justified.
- The app feels cohesive rather than a pile of features.

## Tasks

### 7.1 Advanced dashboard polish

Refine the Palantir-like UI aesthetic:

- Dense project cards
- Status chips
- Evidence timeline
- Command palette
- Keyboard shortcuts
- Compact tables
- Graph-like relationship views
- Dark theme variants
- Smooth but restrained transitions

Potential dashboard panels:

```text
Current Operation
Active Projects
Evidence Feed
Focus Debt
Project Risk
Upcoming Deadlines
GitHub Activity
Logseq Notes Updated
Weekly Commitments
Distraction Attempts
```

### 7.2 Command palette

Add keyboard-first command palette:

```text
Ctrl+K
- Start focus session
- Create task
- Open project workspace
- Add reflection
- Pause project
- Kill project
- Open Logseq page
- Run weekly review
```

This reinforces the command-center feeling and improves speed.

### 7.3 Plateau and risk detection

Create deterministic analytics first:

Examples:

```text
Project has >4h focus time this week but zero completed tasks.
Project has no evidence for 10 days.
User started 3 new projects but completed none.
Task has been deferred 5 times.
Focus sessions repeatedly end as “blocked.”
GitHub repo has commits but no milestone progress update.
```

LLM can later explain these findings, but detection should not require LLM.

### 7.4 Personal reports

Generate reports in Markdown first:

- Daily summary
- Weekly review
- Monthly project review
- Active project audit
- Accountability report

Possible exports:

```text
Markdown
PDF later
Logseq journal append
Clipboard
```

### 7.5 Optional Logseq plugin

If file-based Logseq integration becomes insufficient, build a lightweight Logseq plugin in TypeScript.

Plugin features:

- Send current block/page to LTFI.
- Show LTFI project status inside Logseq.
- Insert focus session block from Logseq.
- Link selected Logseq block to task/project.

Architecture:

```text
Logseq TypeScript plugin
    ↕ local WebSocket/HTTP
LTFI C# desktop app
```

Do not build this before file-based integration proves valuable.

### 7.6 Optional sync

Local-first sync options:

- Manual backup/export
- Sync database via user-controlled cloud folder
- Git-backed data sync
- Custom sync server

Do not add sync before the local data model is stable. Sync multiplies complexity.

### 7.7 OCaml experiments

OCaml may be useful later if there is a clear reason, but it should not be used just because it is interesting.

Possible OCaml use cases:

1. Rule engine for productivity constraints.
2. Temporal logic / state-machine modeling.
3. Project dependency graph analysis.
4. Planning DSL for focus modes and project lifecycle rules.
5. Static analysis of workflow rules.

Example future rule idea:

```ocaml
rule active_project_limit =
  count(active_projects) <= user_settings.max_active_projects
```

or:

```ocaml
rule project_stalled =
  no_evidence_for_days(project, 10)
  && project.status = Active
```

However, C# is sufficient for all early and mid-stage rules. OCaml should remain in `experiments/ocaml-rules/` until it proves practical.

### 7.8 Plugin/integration API

If LTFI becomes extensible, define local API endpoints such as:

```text
GET /api/status
GET /api/focus/current
POST /api/focus/start
POST /api/focus/end
POST /api/evidence
GET /api/projects
POST /api/projects/{id}/evidence
```

This API can support:

- Browser extension
- Logseq plugin
- Scripts
- Future mobile companion
- CLI tools

### 7.9 Product hardening

Before considering LTFI “stable,” address:

- Error handling
- Logs
- Backup/restore
- Database migrations
- Settings import/export
- Safe mode
- Accessibility
- Keyboard navigation
- Installer/package
- Crash recovery for active focus sessions

## Acceptance criteria

- Dashboard is polished and useful.
- Command palette works.
- Risk/plateau detection produces useful warnings.
- Reports can be exported to Markdown.
- Optional plugin architecture is documented.
- Any OCaml work remains isolated and justified.
- App has backup/restore and safe-mode behavior.

---

## 5. Cross-Phase Data Model Sketch

This is not final schema code, but it should guide implementation.

```text
Project
- Id
- Title
- Description
- Status
- DesiredOutcome
- DoneCondition
- ProgressPercent
- TargetDate
- CreatedAt
- UpdatedAt
- LastActiveAt

TaskItem
- Id
- ProjectId
- MilestoneId
- ParentTaskId
- Title
- Description
- Status
- Priority
- DueAt
- ScheduledStartAt
- EstimatedDuration
- CreatedAt
- UpdatedAt

SubtaskItem
- Id
- TaskId
- Title
- Status
- SortOrder
- CreatedAt
- UpdatedAt

Milestone
- Id
- ProjectId
- Title
- Description
- Status
- TargetDate
- SortOrder

FocusSession
- Id
- ProjectId
- TaskId
- FocusModeId
- Intent
- StartedAt
- EndedAt
- Duration
- Status
- ResultSummary
- BlockerSummary
- NextAction

EvidenceItem
- Id
- ProjectId
- TaskId
- FocusSessionId
- Type
- Source
- Title
- Summary
- OccurredAt
- MetadataJson

ReflectionEntry
- Id
- ScopeType
- ScopeId
- Prompt
- Body
- StructuredSummaryJson
- CreatedAt

WorkspaceProfile
- Id
- ProjectId
- Name
- LaunchItemsJson
- AllowedAppsJson
- AllowedUrlsJson
- BlockedAppsJson
- BlockedUrlsJson

IntegrationAccount
- Id
- Provider
- DisplayName
- AuthStatus
- CreatedAt
- UpdatedAt

ExternalLink
- Id
- ProjectId
- Provider
- ExternalId
- Url
- MetadataJson
```

---

## 6. Suggested UI Navigation

Initial navigation:

```text
Today
Projects
Tasks
Focus
Evidence
Review
Settings
```

Later navigation:

```text
Today
Command Center
Projects
Focus
Calendar
Evidence
Analytics
Review
Integrations
Settings
```

Important views:

- `TodayView`
- `ProjectsView`
- `ProjectDetailView`
- `TasksView`
- `FocusView`
- `EvidenceTimelineView`
- `WeeklyReviewView`
- `SettingsView`
- `IntegrationsView`
- `FocusModeSettingsView`

---

## 7. Coding Agent Instructions

When a coding agent reads this file, it should follow these rules:

1. **Do not build all phases at once.** Work on one phase and one vertical slice at a time.
2. **Do not delete Avalonia template files unless replacing them with working equivalents.**
3. **Keep domain logic out of views.** Use MVVM.
4. **Prefer working persistence early.** Mock data is acceptable only temporarily.
5. **Do not add integrations before core workflows exist.**
6. **Do not silently add cloud dependencies.** All integrations should be optional.
7. **Do not implement harsh blocking without safe mode.**
8. **Do not let LLM features mutate user data without confirmation.**
9. **Prefer deterministic analytics before LLM interpretation.**
10. **Add tests for domain rules and application services.**
11. **Use clear commit messages and small changesets.**
12. **Document assumptions when repository structure differs from this plan.**

---

## 8. Early Implementation Priority

The first meaningful version should not try to be the entire vision. The recommended early sequence is:

```text
1. Project model + persistence
2. Task/subtask model + persistence
3. Basic Avalonia shell
4. Project/task CRUD views
5. Focus session timer
6. Evidence item creation
7. Active project limit
8. Weekly review v0
9. Logseq append-only journal integration
10. GitHub activity import
```

A working version with only these features would already be useful.

---

## 9. Long-Term Vision Summary

LTFI should eventually answer questions like:

```text
What am I supposed to be doing right now?
Which projects are actually moving?
Which projects are fake-active but stalled?
What did I accomplish this week?
What am I avoiding?
Did I spend time without producing evidence?
Am I starting too many things?
What should be paused or killed?
What should I open to start working immediately?
What distractions did I override, and why?
What is the next concrete action?
```

The point is not to become a perfect productivity app. The point is to create a system that turns vague anxiety and scattered ambition into visible operations, explicit commitments, and concrete evidence of progress.
