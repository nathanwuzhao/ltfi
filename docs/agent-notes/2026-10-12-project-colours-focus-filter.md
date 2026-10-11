# Project colours everywhere, Focus task picker filtered by project (2026-10-12)

Builds on `2026-10-11-due-2359-codes-nsdr-session.md` (ProjectCodes). No migration.

## 1. Focus: the task picker follows the project

- **Rule (pure, tested):** `Core/Domain/FocusTaskPicker.cs`.
  - `Options(tasks, projectId)`: open tasks only. No project → every open task, in service order.
    A project → only its tasks, grouped by area (area `SortOrder`, then name; no area last), service
    order kept within a group.
  - `Belongs(taskProjectId, selectedProjectId)`: the "can this task stay selected" rule.
- **`FocusViewModel`:**
  - keeps `_openTasks` (with `Area`/`Project` loaded) and rebuilds `TaskOptions` from it;
  - project changed → `RebuildTaskOptions` keeps the selected task if it is still offered, else "(No task)";
  - task picked with **no** project → its project is selected (posted to the dispatcher: rebuilding the
    task ComboBox's items inside its own selection change left it blank);
  - `_syncingPicker` ignores the nulls the ComboBoxes push back while their lists are cleared;
  - `RefreshAsync` restores the picked project/task by id; `PrepareFor` (Today quick-start) still wins,
    and a prefilled task with no project brings its project along.
- **`TaskOption`** gains `ProjectId`, `Area` (`AreaTag` "#job"), and `Code`/`CodeBrush`, set only while no
  project is selected (all tasks listed). Item template: `CODE #area title`.
- **Active session:** the project name (identity colour + dot) above the objective
  (`ActiveProjectName`/`ActiveProjectBrush`, looked up in `ProjectOptions`).

## 2. Project colour: one source of truth

- **Core:** `ProjectCodes.ColorOrNone(Guid?, isStanding)` + `NoneColor` (`#5B636F`, the faint text colour):
  the single colour rule (palette by id hash / standing grey / none).
- **App:** `ViewModels/ProjectBrushes.cs` (public, replaces Command Center's private cache):
  - `For(hex)` (frozen, cached), `For(Guid?, isStanding)`, `For(Project?)`, `ForAny(object?)`;
  - XAML converters `ProjectBrushes.Brush` (Project / ProjectOption / ProjectActivityLine /
    StalledProjectLine / Guid → brush) and `ProjectBrushes.Code` (Project / ProjectOption / title → code).
- **Data for it:**
  - `TaskService.GetAllAsync`/`GetTodayAsync` now `Include(t => t.Project)` (read-only, no-tracking);
  - `ProjectActivityLine` gains `ProjectId`, `IsStanding`; `StalledProjectLine` gains `ProjectId`
    (defaulted record params; `ReviewService` fills them). The coach JSON is built by hand, so unchanged.

| Screen | What is in project colour |
|---|---|
| Projects | 3px swatch bar + name per row (code in faint text beside it); editor header: swatch + name + code |
| Focus | project picker dot per item; task picker `CODE` tag (all-projects mode); active-session project line |
| Tasks | list row: project code tag (tooltip = name) before priority, `#area` stays; editor project picker dot |
| Today | due-today rows: project code tag before the title, `#area` after it |
| Command Center | active-projects code; progress tabs code + progress project name; focus-debt code; UPCOMING "XXXX target" items; current-operation code (own text run before `· RUNNING`); evidence feed (switched to the shared helper) |
| Review | focus-by-project and stalled project names |
| Check-In | unchanged: its link picker doesn't show the project |

Status colours (dots, risk text, bars, `%`) are unchanged. The progress tabs' code was the risk colour
before; it is now identity colour, and risk is still on the row dot/text and the `%`.

## Verification

- `dotnet build LTFI.sln --no-incremental`: 0 Warning(s), 0 Error(s).
- `dotnet test LTFI.sln`: 201 passed. New `FocusPickerAndColourTests`: filter/grouping/order, `Belongs`,
  `ColorOrNone`, tasks carry `Project`, review lines carry the project id.
- Launched the built app (owner's data, navigation and picker clicks only, no session started):
  Command Center, Focus (both dropdowns open; Life → only Life tasks by #area; picking a BOID/Life task
  with no project auto-selects the project), Projects (list + editor header), Tasks, Today, Review.
  Not seen: the current-operation code (needs a running session) and a project target in UPCOMING.
