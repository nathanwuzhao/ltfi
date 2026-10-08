# Tasks agenda view + text boxes that don't widen panels (2026-10-07)

Builds on `2026-10-06-due-push-header.md`. UI-only plus one pure Core helper; no migration, no
service changes.

## A. Tasks tab: agenda grouping + sort

- **Core: `LTFI.Core/Domain/TaskAgenda.cs`** (pure, no clock):
  - `TaskGrouping { Due, Area, None }`, `TaskSort { Due, Priority, Title }`, `DueBucket`.
  - `BucketOf(due, today, zone?)` reads the due date's *local calendar day* (zone defaults to
    `TimeZoneInfo.Local`): `< today` Overdue, `today` Today, `+1` Tomorrow, `+2..+7` This week,
    `+8..` Later, `null` No due date.
  - `Build(tasks, today, grouping, sort, zone?)` returns ordered `TaskGroup(Key, Label, IsAlert, Tasks)`:
    - Due: OVERDUE (IsAlert → red), TODAY, TOMORROW, THIS WEEK, LATER, NO DUE DATE; empty ones omitted.
    - Area: one group per `AreaId`, labelled `#NAME` (upper-cased), A→Z, `NO AREA` last.
    - None: one `OPEN` group (the view shows it without a header).
    - Closed tasks (Completed/Canceled) present in the input always go in a final `COMPLETED`
      group, most recent first (`CompletedAt ?? UpdatedAt`). The caller decides whether to pass
      them (the "Show archived" checkbox).
  - `Sort`: Due = due (none last) → priority Urgent→Low → title; Priority = priority → due → title;
    Title = title (case-insensitive) → due.
- **VM (`TasksViewModel`)**:
  - New `Rows` (`ObservableCollection<object>`): `TaskGroupHeader(Label, Count, IsAlert, IsFirst)`
    rows followed by their `TaskItem`s. `Tasks` is kept as the flat visible list in display order.
  - `SelectedRow` is bound to the ListBox. A `TaskItem` row sets `SelectedTask` (editor unchanged).
    A header row is never a selection; if keyboard navigation lands on one, it's posted back to
    `SelectedTask`. `OnSelectedTaskChanged` mirrors into `SelectedRow`.
  - `Grouping` / `Sort` (+ `IsGroupDue`… bools for `.seg.selected`) and `SetGroupingCommand` /
    `SetSortCommand`. The VM is a singleton, so the choice lasts for the session (not persisted to
    settings). Default: GROUP DUE, SORT DUE.
  - `RebuildRows()` re-groups the same tasks **without reloading the editor**, so a GROUP/SORT click
    keeps unsaved edits and the selected task. `ApplyFilter` (refresh / Show archived) calls it,
    then re-selects by Id as before.
- **View (`TasksView.axaml`)**:
  - Under New Task / Show archived there's a wrap-able strip: `GROUP DUE|AREA|NONE  SORT DUE|PRIORITY|TITLE`
    (compact `Button.seg`, `CommandParameter` = `x:Static` enum).
  - The ListBox uses `ListBox.DataTemplates` (header template + the unchanged task row template:
    checkbox, +1D, PENDING/status chips, #area, due).
  - Headers are `.micro` label + `.micro-dim` count. OVERDUE uses a local `TextBlock.micro.alert`
    style (DangerBrush).
  - `TasksView.axaml.cs` makes header containers `IsHitTestVisible=false` / `Focusable=false` on
    `ContainerPrepared`. Both states are set every time because containers are recycled.

## B. Text boxes wrap instead of widening

- **Root cause on Focus:** the card was `HorizontalAlignment="Center"` with `MaxWidth=560`, i.e.
  auto-width. A TextBox's desired width grows with its text, so the card grew as you typed. It's
  now `HorizontalAlignment="Stretch"` + `MaxWidth=560`: a fixed column that layout still centres.
- **`Controls.axaml`:**
  - Every `TextBox` is `HorizontalAlignment=Stretch`. Single-line boxes keep NoWrap and scroll their
    text inside.
  - New **`TextBox.multi`**: AcceptsReturn, Wrap, MinHeight 60, MaxHeight 180, top-aligned, no
    horizontal scroll, vertical scroll Auto. Use a local `MinHeight` for 2-line fields (44).
- **Applied:**
  - Focus: intent (multi, 44), review "What changed?" (multi), blocker and next action (multi, 44).
  - Check-in: Q1–Q4 and Q6 answers (multi, 54). The commitment rows stay single-line in their star column.
  - Tasks editor: description (multi, 70).
  - Projects editor: description (multi, 80), done condition (multi, 44).
  - The Tasks/Projects editor ScrollViewers and the Review page ScrollViewer now say
    `HorizontalScrollBarVisibility="Disabled"` explicitly. That's already Avalonia's default; it's
    explicit so nobody "fixes" it into Auto.
  - Unchanged single-line fields: titles, due/target date, minutes, subtask/milestone/area names,
    the API key. All sit in star columns or stretched stacks.
  - TodayView and CommandCenterView have no TextBoxes.

## Decisions / deviations

- **NONE grouping shows no header** for the open tasks. The COMPLETED header still shows when archived tasks are visible.
- **Area groups are keyed by `AreaId`.** Two areas with the same name in different projects stay
  separate sections with the same label.
- **The Focus intent and review fields are now multi-line.** Enter inserts a newline (Focus has no
  default button, so nothing is lost). Newlines are stored as typed.

## Verification

- `dotnet build LTFI.sln --no-incremental`: 0 Warning(s), 0 Error(s). No LTFI.App was running, so
  this was a normal build.
- `dotnet test LTFI.sln`: **158 passed** (141 + 17 new in `TaskAgendaTests`):
  - bucket boundaries −30/−1/0/+1/+2/+7/+8/+60 at 00:00 and 23:00;
  - null → no date;
  - the zone is applied, not the stored offset;
  - section order and hidden empty sections;
  - due, priority and title orders;
  - area grouping with no-area last;
  - COMPLETED last and most recent first under every grouping;
  - empty input.
- **Launched the app against the real DB** (navigation and view toggles only; screenshots in the session scratchpad `ui4/`):
  - Tasks: default agenda (TOMORROW 3, THIS WEEK 6 …) and the GROUP/SORT strip.
  - Tasks: selected "odys", switched to AREA (#GATECH / #HOMEWORK / #JOB). The selection survived,
    and so did the editor.
  - Tasks: NONE + PRIORITY + Show archived, with the COMPLETED rows at the end.
  - Focus: typed a long intent. It wrapped and the box grew downward, while the card stayed 560 wide.
    The text was then cleared, and nothing was started.
  - Check-in and Review layouts look normal.
- **Seen, not changed:** on Focus the pomodoro hint ("…counts toward the session.") draws only its
  first line at 125% scaling. The 2-line space is reserved but line 2 isn't drawn. This looks like
  Avalonia dropping a last line at fractional DPI, not width-related.
