# Lightweight Local Task Planner — Avalonia/.NET Development Plan

## 1. Project Goal

Build a lightweight local desktop task-planning app inspired by Donetick, using **C#/.NET** and **Avalonia UI**.

The app should:

* Run locally without Docker or self-hosting.
* Start automatically when the computer starts.
* Sit on the desktop, in the tray, or as a small always-available planning window.
* Support tasks, subtasks, labels, priorities, scheduling, timers, recurring tasks, and points.
* Be simple enough for a beginner to build incrementally.
* Use this project as practice for Avalonia, MVVM, local persistence, and production-style app structure.

This should not start as a full productivity platform. The first target should be a reliable personal app with a small feature set.

---

## 2. Recommended Tech Stack

### Core

* **Language:** C#
* **Framework:** .NET 9, since your local SDK is already .NET 9
* **UI Framework:** Avalonia UI
* **Architecture:** MVVM
* **MVVM Library:** CommunityToolkit.Mvvm
* **Local Database:** SQLite
* **Data Access:** EF Core SQLite or lightweight raw SQLite

### Recommended beginner choice

Use:

* `CommunityToolkit.Mvvm`
* `Microsoft.EntityFrameworkCore.Sqlite`
* `Microsoft.EntityFrameworkCore.Design`

Avoid initially:

* Cloud sync
* User accounts
* Web APIs
* Mobile app support
* Complex plugin systems
* Native AOT
* Heavy background services
* Full calendar integration

---

## 3. High-Level App Concept

Working name: **TaskForge**, **LocalQuest**, **FocusLedger**, or similar.

The app has four main modes:

1. **Today View**

   * Shows tasks due today.
   * Shows scheduled blocks.
   * Shows active timer if one is running.
   * Shows points earned today.

2. **Task Detail View**

   * Task title, notes, priority, labels.
   * Subtasks.
   * Required work duration.
   * Start/pause/complete controls.
   * Completion rules.

3. **Planning View**

   * Create/edit tasks.
   * Assign due dates and scheduled times.
   * Set repeat rules.

4. **Tray/Desktop Reminder Mode**

   * App runs quietly in the tray.
   * Reminds you when scheduled tasks are due.
   * Allows quick open, start task, pause task, or dismiss reminder.

---

## 4. Core Feature Requirements

## 4.1 Tasks

Each task should support:

* Title
* Description/notes
* Priority
* Labels
* Due date
* Optional scheduled start time
* Optional estimated duration
* Required active work time before completion
* Completion status
* Points value
* Creation date
* Last modified date

Example tasks:

* “10 minute workout”
* “Study SystemVerilog”
* “Review calculus notes”
* “Work on Avalonia task app”

---

## 4.2 Subtasks

Each task can have subtasks.

Each subtask should support:

* Title
* Completed/not completed
* Sort order
* Parent task ID

For the MVP, subtasks do not need their own due dates, timers, labels, or recurrence.

---

## 4.3 Labels and Priority

Support both fixed priority and custom labels.

### Priority

Use a fixed enum:

```csharp
public enum TaskPriority
{
    Low,
    Medium,
    High,
    Urgent
}
```

### Labels

Labels should be user-defined.

Examples:

* `School`
* `Workout`
* `Programming`
* `Research`
* `SystemVerilog`
* `Georgia Tech`
* `Daily`
* `Weekly`

A task can have multiple labels.

---

## 4.4 Timer / Stopwatch System

Each task can be started with a **Begin Task** button.

Timer behavior:

* Pressing **Begin Task** starts active time tracking.
* Pressing **Pause** stops active tracking but keeps accumulated time.
* Pressing **Resume** continues tracking.
* Pressing **Complete** only works if the task has met its required active time.
* If no required active time is set, the task can be completed manually.

Example:

A task says:

> “Daily 10 minute workout”

Rules:

* Required active time: 10 minutes
* User presses Begin Task
* App tracks active time
* Completion button becomes available only after 10 minutes

Another task says:

> “Weekly SystemVerilog studying”

Rules:

* Required active time: 3 hours
* Recurs weekly
* Completion requires 3 hours of active tracked work

---

## 4.5 Points System

Each task can award points after completion.

Possible scoring rules:

* Small task: 5 points
* Medium task: 10 points
* Large task: 25 points
* Long focused work: 50 points
* Daily streak bonus: future feature

For the first version, keep this simple:

```text
Task completed -> add task.PointsValue to daily score
```

Later, add:

* Daily score
* Weekly score
* Streaks
* Level system
* Missed-task penalty, optional

---

## 4.6 Scheduling

Each task should support:

* Due date
* Optional scheduled start time
* Optional scheduled end time
* Optional reminder time before start

Example:

```text
Task: Study SystemVerilog
Due: Sunday
Scheduled: 2:00 PM - 5:00 PM
Required active work: 3 hours
```

For MVP, do not build a full drag-and-drop calendar. A simple list grouped by date is enough.

---

## 4.7 Recurring Tasks

Support repeating tasks.

Start with simple recurrence rules:

* Daily
* Weekly
* Monthly
* Every N days
* Every N weeks

Avoid complex recurrence at first:

* “Every weekday except holidays”
* “Second Tuesday of every month”
* “Every Monday, Wednesday, Friday”
* Timezone edge cases
* Syncing with external calendars

Example recurrence model:

```csharp
public enum RecurrenceType
{
    None,
    Daily,
    Weekly,
    Monthly,
    EveryNDays,
    EveryNWeeks
}
```

When a recurring task is completed, generate the next task instance.

Example:

```text
Complete “Daily 10 minute workout” on June 14
App creates next instance for June 15
```

For the MVP, create the next instance only after completion. Later, you can pre-generate upcoming tasks.

---

## 5. Suggested Database Model

Use SQLite.

Recommended entities:

```text
TaskItem
SubtaskItem
Label
TaskLabel
TimerSession
PointEvent
```

---

## 5.1 TaskItem

Fields:

```text
Id
Title
Description
Priority
DueDate
ScheduledStart
ScheduledEnd
RequiredWorkSeconds
AccumulatedWorkSeconds
Status
PointsValue
RecurrenceType
RecurrenceInterval
CreatedAt
UpdatedAt
CompletedAt
ParentRecurringTaskId
```

Possible status values:

```csharp
public enum TaskStatus
{
    NotStarted,
    InProgress,
    Paused,
    Completed,
    Skipped
}
```

---

## 5.2 SubtaskItem

Fields:

```text
Id
TaskItemId
Title
IsCompleted
SortOrder
CreatedAt
UpdatedAt
```

---

## 5.3 Label

Fields:

```text
Id
Name
ColorHex
CreatedAt
```

---

## 5.4 TaskLabel

Many-to-many join table:

```text
TaskItemId
LabelId
```

---

## 5.5 TimerSession

Store each work session separately.

Fields:

```text
Id
TaskItemId
StartedAt
EndedAt
DurationSeconds
WasCompletedNormally
```

This is better than storing only a single timer number because it lets you later build analytics.

Example:

```text
SystemVerilog task
Session 1: 45 minutes
Session 2: 30 minutes
Session 3: 1 hour 45 minutes
Total: 3 hours
```

---

## 5.6 PointEvent

Fields:

```text
Id
TaskItemId
Points
Reason
CreatedAt
```

Example reasons:

```text
Completed task
Daily streak bonus
Manual adjustment
```

---

## 6. Project Structure

```text
ltfi/
  LTFI.sln
  .gitignore
  README.md

  LTFI/
    LTFI.csproj
    Program.cs
    App.axaml
    App.axaml.cs
    ViewLocator.cs
    app.manifest

    Assets/

    Models/
      TaskItem.cs
      SubtaskItem.cs
      Label.cs
      TimerSession.cs
      PointEvent.cs
      Enums.cs

    ViewModels/
      ViewModelBase.cs
      MainWindowViewModel.cs
      TodayViewModel.cs
      TaskItemViewModel.cs

    Views/
      MainWindow.axaml
      MainWindow.axaml.cs
      TodayView.axaml
      TodayView.axaml.cs

    Services/
      TaskService.cs
      TimerService.cs
      PointService.cs
      RecurrenceService.cs

    Data/
      AppDbContext.cs
```

---

## 7. MVVM Design

### Model

Plain data objects:

```text
TaskItem
SubtaskItem
Label
TimerSession
```

Models should not know about buttons, windows, or UI.

---

### View

Avalonia XAML files:

```text
TodayView.axaml
TaskDetailView.axaml
TaskEditView.axaml
```

Views should only describe layout.

Avoid putting app logic in `.axaml.cs` files unless absolutely necessary.

---

### ViewModel

ViewModels connect the UI to app logic.

Example:

```text
TodayViewModel
- ObservableCollection<TaskItemViewModel> TodayTasks
- TaskItemViewModel? SelectedTask
- BeginTaskCommand
- PauseTaskCommand
- CompleteTaskCommand
```

---

### Service

Services hold business logic.

Example:

```text
TimerService
- StartTaskAsync(taskId)
- PauseTaskAsync(taskId)
- CompleteTaskAsync(taskId)
```

This keeps your ViewModels from becoming huge.

---

## 8. Development Phases

# Phase 0 — Repository Setup

Goal: make the project clean before adding features.

Tasks:

* Create GitHub repository.
* Add `.gitignore`.
* Add `README.md`.
* Confirm project builds.
* Confirm project runs.
* Add basic folder structure.
* Add first commit.

Suggested first commit:

```bash
git add .
git commit -m "Initial Avalonia MVVM project"
```

README should initially include:

```text
# LocalTaskPlanner

A lightweight local desktop task planner built with Avalonia UI and .NET.

## Goals

- Local-first task planning
- Timed tasks
- Recurring tasks
- Points/reward system
- Tray/startup support
```

---

# Phase 1 — Static UI Mockup

Goal: create the visual structure before database complexity.

Build:

* Main window
* Sidebar
* Today page
* Task list
* Task detail panel
* Fake sample tasks using hardcoded data

Do not use SQLite yet.

Example fake data:

```text
[High] Daily 10 minute workout — 0/10 min
[Medium] Study SystemVerilog — 35/180 min
[Low] Clean downloads folder
```

UI layout idea:

```text
-------------------------------------------------
| Sidebar       | Today                         |
|---------------|-------------------------------|
| Today         | 10 minute workout              |
| Upcoming      | [Begin Task] [Complete]         |
| Labels        |                               |
| Settings      | SystemVerilog Study             |
|               | [Resume] 35m / 3h               |
-------------------------------------------------
```

This phase teaches:

* Avalonia layout
* Data binding
* Observable collections
* Commands
* Basic MVVM

---

# Phase 2 — Task CRUD Without Database

Goal: make tasks interactive in memory.

Build:

* Add task
* Edit task
* Delete task
* Complete task
* Add subtasks
* Complete subtasks
* Assign priority
* Assign due date
* Assign points

Still do not use SQLite yet.

Reason: it is easier to debug MVVM and UI logic before database logic.

---

# Phase 3 — SQLite Persistence

Goal: tasks persist after closing the app.

Add:

* SQLite database
* EF Core DbContext
* Database creation on startup
* Save tasks
* Load tasks
* Update tasks
* Delete tasks

Database location:

```text
%AppData%/LocalTaskPlanner/tasks.db
```

On Windows, this would resolve to something like:

```text
C:\Users\<you>\AppData\Roaming\LocalTaskPlanner\tasks.db
```

At this phase, keep migrations simple. Since this is a personal beginner app, it is acceptable to delete and recreate the local database during early development.

---

# Phase 4 — Timer System

Goal: implement the Donetick-style “Begin Task” behavior.

Build:

* Begin task
* Pause task
* Resume task
* Accumulate active work time
* Prevent completion until required time is met
* Save timer sessions to database
* Restore task timer state after app restart

Important rule:

```text
A task with RequiredWorkSeconds > 0 cannot be completed until AccumulatedWorkSeconds >= RequiredWorkSeconds.
```

Recommended behavior after app closes:

* If task was running, mark it paused when app reopens.
* Do not count time while app was closed.
* Later, add a setting to optionally continue timers in background.

---

# Phase 5 — Recurring Tasks

Goal: support daily/weekly/monthly tasks.

Build:

* Recurrence type field
* Recurrence interval field
* Generate next task after completion
* Link generated tasks to original recurring template

Example:

```text
Daily workout completed on 2026-06-14
Next task generated for 2026-06-15
```

Begin with:

* Daily
* Weekly
* Monthly

Then add:

* Every N days
* Every N weeks

Avoid complicated recurrence until later.

---

# Phase 6 — Points System

Goal: make completion rewarding.

Build:

* Points per task
* Award points on completion
* Store point events
* Show today’s points
* Show weekly points
* Prevent duplicate points from repeated completion

Simple display:

```text
Today: 35 points
This week: 120 points
```

Optional later:

* Streaks
* Level system
* Badges
* Charts
* Penalties for skipped tasks

---

# Phase 7 — Tray and Startup Behavior

Goal: make the app feel like a real local productivity utility.

Build:

* System tray icon
* Tray menu:

  * Open planner
  * Start next task
  * Pause current task
  * Quit
* Minimize to tray
* Optional launch on startup setting

On Windows, startup can be handled by adding/removing a shortcut in the Startup folder.

Startup folder path:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

Do not implement startup behavior until the app is otherwise stable.

---

# Phase 8 — Reminders and Overlay

Goal: remind you when planned tasks should start.

Start simple:

* In-app reminder banner
* Notification panel inside the app
* Highlight overdue/scheduled tasks

Then add:

* Native desktop notification
* Small always-on-top reminder window
* Optional overlay mode

Suggested overlay behavior:

```text
A small borderless window appears:
"SystemVerilog Study is scheduled now"
[Begin] [Snooze 10 min] [Dismiss]
```

Avoid making the overlay aggressive at first. You want reminders, not malware-like behavior.

---

# Phase 9 — Polish and Packaging

Goal: turn the project into something you can use daily.

Build:

* App icon
* Settings page
* Theme support
* Export/import database
* Backup database
* Single-file Windows publish
* GitHub releases

Possible publish command later:

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

Only worry about packaging after the app is useful in development mode.

---

## 9. Beginner-Friendly Implementation Order

Recommended order:

```text
1. GitHub repo
2. README and .gitignore
3. MainWindow layout
4. Hardcoded task list
5. Add task form
6. Edit/delete/complete tasks in memory
7. Add subtasks in memory
8. Add timer in memory
9. Add SQLite persistence
10. Add recurrence
11. Add points
12. Add tray icon
13. Add startup behavior
14. Add reminders/overlay
15. Package app
```

The most important advice: do not start with the database, tray, startup, or notifications.

Start with a visible task list and buttons that work.

---

## 10. Suggested First Milestone

Milestone 1 should be:

> A local Avalonia window that shows today’s tasks and lets you start/pause/complete hardcoded tasks.

Acceptance criteria:

* App runs.
* Main window opens.
* There is a list of fake tasks.
* Selecting a task shows details.
* Begin Task starts a visible timer.
* Pause stops the timer.
* Complete works only when required time is met.
* No database yet.

This milestone is small enough to finish without getting lost.

---

## 11. Suggested First GitHub Issues

Create these issues:

```text
#1 Set up repository structure
#2 Create initial README
#3 Build MainWindow shell
#4 Add TodayView with hardcoded sample tasks
#5 Add TaskItem model
#6 Add TaskItemViewModel
#7 Implement Begin/Pause/Resume timer in memory
#8 Add task completion rule based on required work time
#9 Add SQLite database
#10 Persist tasks across app restart
```

---

## 12. Code Quality Rules

Use these rules from the beginning:

### Keep UI and logic separate

Bad:

```text
Button click directly modifies database inside MainWindow.axaml.cs
```

Better:

```text
Button binds to command in ViewModel
ViewModel calls TaskService
TaskService updates database
```

---

### Keep classes small

If a file gets longer than roughly 250–300 lines, consider splitting it.

---

### Use clear names

Prefer:

```text
TaskItem
TimerSession
RequiredWorkSeconds
AccumulatedWorkSeconds
CompleteTaskCommand
```

Avoid vague names:

```text
Thing
Data
Stuff
Handler
DoTask
```

---

### Commit often

Good commits:

```text
Add TaskItem model
Create TodayView layout
Implement in-memory task timer
Add SQLite task persistence
```

Bad commits:

```text
updates
stuff
fixed things
big changes
```

---

## 13. Testing Plan

You do not need heavy testing immediately, but test business logic once recurrence and timers exist.

Unit test these:

* Completing task before required time should fail
* Completing task after required time should pass
* Daily recurrence generates next day’s task
* Weekly recurrence generates next week’s task
* Points are awarded once
* Paused timer does not keep accumulating time

Example test names:

```text
CannotCompleteTimedTaskBeforeRequiredDuration()
CanCompleteTimedTaskAfterRequiredDuration()
CompletingDailyTaskCreatesTomorrowInstance()
CompletingTaskAwardsPointsOnce()
```

---

## 14. First Version Feature Cut

The first usable version should include only:

* Create/edit/delete tasks
* Today view
* Priority
* Labels
* Subtasks
* Timer
* Completion after required active time
* Points
* Daily/weekly/monthly recurrence
* SQLite persistence

Save these for later:

* Cloud sync
* Mobile app
* Calendar integration
* Complex analytics
* Full habit tracker
* Natural language task creation
* AI planning assistant
* Team/shared tasks
* Plugin architecture

---

## 15. Main Risks

### Risk 1: Too much scope

This project can easily become huge.

Solution:

Build the app in small, usable milestones.

---

### Risk 2: MVVM confusion

Avalonia MVVM can feel confusing at first.

Solution:

Start with one view, one viewmodel, and hardcoded data.

---

### Risk 3: Database complexity

EF Core migrations and object relationships can become annoying.

Solution:

Do not add SQLite until the UI and in-memory logic work.

---

### Risk 4: Timer bugs

Timers can be tricky if the app is closed, minimized, or suspended.

Solution:

For version 1, count only active time while the app is open.

---

### Risk 5: Notification/platform behavior

Startup apps, overlays, and notifications are platform-specific.

Solution:

Implement core planning features first. Add Windows-specific startup behavior later.