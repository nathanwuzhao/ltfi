# New-task target list text + lost last line at 125% (2026-10-08)

Builds on `2026-10-07-tasks-agenda-textboxes.md`. No migration, no behaviour change in what gets
written. Only the editor text changed, plus one rendering fix.

## A. Tasks editor says which iPhone list a task goes to

- **One rule, shared:** `ReminderRules.TargetList(projectIsStanding, areaName, configuredLtfiList)`
  in `LTFI.Core/Domain/ExternalReminder.cs`, with `LtfiListOrDefault` and `DefaultLtfiList = "LTFI"`.
  - A standing project uses the area name.
  - A standing project with no area returns `null`: it can't be placed yet.
  - Anything else uses settings `reminders.ltfiList`, trimmed, or `LTFI` when that's blank.
- `TaskService` uses it for `CreateAsync` and for `RefreshPendingCreateAsync` through a private
  `TargetList(project, area)`. That throws the "Pick an area" message on `null`, which
  `ResolvePlacementAsync` already prevents. The old private `LtfiList` property is gone.
- **`TasksViewModel`:**
  - `CreateTargetText` uses the same rule and shows one of:
    - `→ iPhone list: job` for a standing project with an area.
    - `→ iPhone list: LTFI (non-Life project)`. The standing project is named when there's exactly
      one; otherwise it says `(not a standing project)`.
    - `→ iPhone list: LTFI (no project)`.
    - `Pick an area — it's the iPhone list this goes to.` for a standing project with no area.
  - The text is re-raised after `RefreshAsync` reloads the project options.
  - `AreaHeaderText`: `AREA = iPHONE LIST` for a standing project, otherwise `AREA (LTFI only)`.
  - `ExistingListText` / `HasExistingList`: `iPhone list: HOMEWORK` (from `TaskItem.ExternalList`)
    for an existing reminder-backed task. It's read-only and hidden when the list isn't known.
- **`TasksView.axaml`:** the target line is SemiBold 12, with the PENDING ON iPHONE explanation
  under it as muted 11. The existing-task list line sits above the iCloud help text. The AREA
  header is bound.
- Tests: `ReminderTargetListTests`, 8 cases.

## B. Wrapped text losing its last line at 125% scaling (Focus pomodoro hint)

- **Root cause (reproduced headless with Avalonia 12.0.4, `SetRenderScaling(1.25)`):**
  1. `TextBlock.ArrangeOverride` rebuilds its `TextLayout` with the *arranged* height as `MaxHeight`.
  2. `TextLayout` drops any line where `height + lineHeight > MaxHeight`.
  3. `LayoutHelper.RoundLayoutSizeUp` does `Math.Round(v, 8, ToZero)` before `Ceiling`. A height a
     few ULPs above a device-pixel boundary therefore comes back *below* the real height.
  4. JetBrains Mono at FontSize 10 has a line height of `13.200000000000001`. Two lines are
     `26.400000000000002`, and at 125% that rounds "up" to `26.4`. Line 2 is dropped while its
     space stays reserved.
  5. It's fine at 100% and 150%. Size 10 at 125% also fails at 4 or 8 lines; size 16 at 125% at 5
     or 10 lines. No width or style issue is involved (there's no MaxLines, Height or trimming).
- **Fix:** `LTFI.App/Controls/WrapTextBlock.cs`, a `TextBlock` subclass:
  - Its style key stays `TextBlock`, so `.faint` and the other classes still apply, and it wraps by
    default.
  - It arranges with +0.01 DIP of height. `ArrangeCore` clamps the returned size, so the bounds
    are unchanged.
- **Used for:**
  - The Focus pomodoro hint.
  - The Focus `CurrentObjective` (FontSize 16).
  - The Review page's two FontSize-10 faint notes: LLM settings and `CoachMeta`.
- **Rule:** use `c:WrapTextBlock` (`xmlns:c="using:LTFI.Controls"`) for any new wrapping
  FontSize-10 text, or anywhere a wrapped last line goes missing.
  - Other wrapping sizes are safe up to 12 lines at 100/125/150/175/200%: 9, 9.5, 10.5, 11, 11.5,
    12, 13, 14 and 15 (15 only fails at 12 lines).

## Verification

- `dotnet build LTFI.sln --no-incremental`: 0 Warning(s), 0 Error(s). The owner's LTFI.App was
  running, so this was built to the scratch `OutDir`.
- `dotnet test LTFI.sln`: **166 passed**.
- Headless check: a plain TextBlock against `WrapTextBlock` with the real hint at 500 wide.
  - At 125% the plain block drew 1 line and `WrapTextBlock` drew 2.
  - At 100% and 150% both drew 2.
  - The frames are in the session scratchpad `ui5/`.
- The real app wasn't screenshotted, because the owner's instance was running.
