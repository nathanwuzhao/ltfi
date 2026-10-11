# Due dates at 23:59, project codes + colours, NSDR inside a focus session (2026-10-11)

Builds on `2026-10-06-due-push-header.md`, `2026-10-04-pomodoro-nsdr.md` and `2026-10-04-audio.md`.
There is no migration.

## 1. Due dates sent to the iPhone are end-of-day

**The bug.**
1. LTFI sent `2026-10-11T00:00:00-04:00`.
2. The owner's Shortcut (Get Dates from Input → Edit Reminder Due Date) stored it as **12:00 PM**.
3. The export brought back 12:00.
4. `ShiftDue` kept the time of day, so +1D pushed 12:00 again.

**The rule.** It lives in one place, the new pure `Core/Domain/DueDates.cs`. LTFI treats due as a calendar
date. Every due date LTFI sets or sends is **that local date at 23:59:00, with its offset**.
- `DueDates.EndOfDay(DateTime | DateTimeOffset | DateTimeOffset?)` gives the local date at 23:59.
- `DueDates.Shift(due, days, today)` gives (local due date + N days) at 23:59. The old time is ignored, so a
  phone-stored 12:00 goes back out at 23:59. With no due date it counts from today.
- `DueDates.IsDateOnly(due)` is true when the local time is 00:00, 12:00 or 23:59. It is display-only
  and nothing uses it yet, because LTFI already shows only dates. Phone data is never rewritten on sync.

**Where the rule applies.**
- `TaskService.CreateAsync` stores `DueAt` as 23:59.
- `UpdateAsync` normalises the date only when it **changed**. An unchanged value keeps the stored
  (possibly phone-set) time, and no update is queued.
- `SetDueCoreAsync` normalises the date, which covers `SetDueDateAsync` and `PushDueByDaysAsync`.
- `TaskService.ShiftDue` now delegates to `DueDates.Shift`.
- `ReminderOutbox.CreatePayload` and `UpdatePayload` also apply `EndOfDay`. This is a second guard, so
  nothing can go out at another time.
- The editor (`TasksViewModel`) turns a newly chosen date into `DueDates.EndOfDay(date)`.
- The stored `DueAt` is the same instant as the one sent.

**Confirmation.** `ReminderRules.DueMatches` now matches on the **same local calendar day** (or the same
instant within a minute). A pushed 23:59 confirms against whatever the phone hands back on that day:
23:59, 12:00, midnight or date-only. It no longer distinguishes 09:30 from 10:30 on the same day. That
is intended, because due is a date.

**No migration.** The two open reminders LTFI pushed to 12:00 (nvidia ignite etc.) are left alone. Their
next +1D or editor change sends 23:59.

**Docs.** `docs/reminders-sync-setup.md` now covers:
- `dueDate` values arrive as 23:59 local (in the samples and the step notes);
- confirmation is by local day;
- the "every due LTFI sends is 23:59" rule.

## 2. Evidence-feed project codes and colours

- Pure `Core/Domain/ProjectCodes.cs` handles codes and colours.
  - **Code.** `Code(title)` takes the first 4 letters/digits, uppercased: "boids 01" → `BOID`, "mirolab
    research" → `MIRO`, "Life" → `LIFE`. Shorter titles stay as they are, and a title with no
    letters/digits gives `—`.
  - **Palette.** 8 muted colours: steel blue, lavender, teal, rose, sand, periwinkle, sky and mauve.
    None of them are the status green/amber/red, and all are light enough for the near-black background.
  - **Colour.** `ColorFor(id, isStanding)` picks a palette slot by **FNV-1a over `Guid.ToByteArray()`**,
    so the colour is stable across runs and machines. Standing projects get neutral `#7A828F`.
- `CommandCenterViewModel`:
  - `Code()` delegates to `ProjectCodes.Code`. That covers active projects, focus-debt lines,
    upcoming "XXXX target" entries and the current-op context.
  - `EvidenceRow` gains `ProjectBrush` and `HasProject`.
  - A `_standingProjects` set is filled in the existing project-title loop.
  - `ProjectBrushes` caches the frozen brushes.
- `CommandCenterView.axaml` evidence template: the code is drawn in the project colour, with a 2×10 colour
  bar to its left. Same column widths, so the feed stays compact. Evidence with no project shows a faint `—`.

## 3. NSDR inside a focus session

**Core** (`Pomodoro.cs`):
- `PomodoroCycle` gains `NsdrFromWork` (a defaulted record param) and `TakeNsdr()`:
  - from **Work** → Nsdr, with `NsdrFromWork = true`;
  - from a **short or long break** → Nsdr, replacing the break;
  - from inside an NSDR → throws.
- `TakeNsdrInstead()` (long break only) is now the same transition.
- `EndBreak()` clears the flag, so an NSDR taken from work goes back to the same interval with the same count.
- `IsRestAfterWork` keeps the dots honest: an NSDR in the 5th interval shows `○○○○`, not `●●●●`.
- New `NsdrReturn` enum: `None`, `ResumeSession` (FREE), `ResumeWork`, `StartNextPomodoro`.
  `cycle.AfterNsdr` and `PomodoroCycle.AfterSessionNsdr(inSession, cycle)` return it.

**Abstractions.**
- `PomodoroSnapshot` gains `IsNsdrFromWork` (defaulted) and `CanStartNsdr`.
- `PomodoroTransition` gains `NsdrEnded`.
- `NsdrSnapshot` gains `FocusSessionId` (defaulted).
- `IPomodoroService` gains `TakeNsdrAsync()`.

**`PomodoroService.TakeNsdrAsync`.** It runs under the existing gate and returns false when there is no
session or an NSDR is already running.
- **Pomodoro run:**
  - pauses the session if a work interval is running;
  - applies `cycle.TakeNsdr()` and clears break-over;
  - starts the NSDR linked to the session.
- **FREE session:** pauses the session and starts the NSDR linked to it.

**NSDR ending, by case.**
- **NSDR from a work interval:**
  - At 10:00, `AdvanceAsync` writes the evidence (via `NsdrService`, `FocusSessionId` set), returns to Work
    and returns `NsdrEnded`.
  - The session never ran during the NSDR, so the interval's remaining time is exactly what it was. It
    shows as WORK · PAUSED and the button reads **Resume work**.
  - `SkipBreakAsync` stops the NSDR early and goes back to paused work, recording nothing. That covers the
    Focus page STOP and the Command Center's END NSDR, which it already showed because `IsBreak` is
    true during any NSDR.
- **NSDR from a break:** completion → BREAK OVER → START NEXT POMODORO, the same as before.
- **FREE mode:** after completion or stop the session stays paused and the existing **Resume** button
  continues it.
- **Evidence:** a completed NSDR writes NsdrCompleted (3 pts) with `FocusSessionId`. Stopping early writes nothing.

**FocusViewModel and FocusView.**
- **NSDR 10:00 button.** A "NON-SLEEP DEEP REST · NSDR 10:00" row sits in the active-session panel above
  Pause/Finish/Abandon (`CanStartSessionNsdr`). It shows during work, any break, break-over and FREE mode.
  It is hidden during an NSDR and while the long break's TAKE NSDR INSTEAD is offered, to avoid two
  buttons for the same thing.
- **Panel.** The NSDR panel is unchanged: the in-app track, cues and chimes all work through `SyncNsdr`.
- **STOP label** depends on the case:
  - `STOP — BACK TO WORK (NOTHING RECORDED)`
  - `STOP — BACK TO SESSION (NOTHING RECORDED)`
  - `END NSDR — START NEXT POMODORO` (break)
  - `STOP (NOTHING RECORDED)` (standalone)
- **Tick.** `NsdrEnded` stops the track, plays the `nsdr-done` chime, signals stats and shows "Work is
  paused: RESUME WORK when ready". A FREE-session NSDR completes through the existing standalone path,
  with a session-aware message.
- **Resumed elsewhere.** If a FREE session is resumed from the Command Center during its NSDR, the next
  tick stops the NSDR, records nothing and says so. Work wins.

## Deviations / decisions

- **Stopping an NSDR that replaced a break still goes straight to the next pomodoro.** That is the
  existing "END NSDR — START NEXT POMODORO" behaviour, kept as asked ("keep TAKE NSDR INSTEAD
  working"). Completion still lands on BREAK OVER → START NEXT POMODORO.
- **Command Center current-operation panel: no NSDR-specific UI added** (the brief said optional, and
  that region belongs to the other agent). It already behaves correctly:
  - a pomodoro in-session NSDR shows `NSDR` with the countdown and an END NSDR button;
  - a FREE-session NSDR shows the session as PAUSED. Pressing RESUME there ends the NSDR on the next
    Focus tick, as described under NSDR ending above.
- **Same-day matching is looser.** `DueMatches` now matches any time on the same local day; before, a
  non-midnight time had to be within one minute.
- **`DueDates.IsDateOnly`** is provided and tested but not used anywhere yet.

## Verification

- **Build.** `dotnet build LTFI.sln --no-incremental` failed in the normal output only from races with
  a concurrent build: missing `obj/ref` dlls, a locked `LTFI.App.pdb`, and an `avalonia-logo.ico`
  being deleted by another change. With `-p:OutDir=<scratchpad>/buildcheck2/` the result was
  **0 Warning(s), 0 Error(s)**, with XAML compiled bindings included.
- **Tests.** `dotnet test LTFI.sln`: **194 passed**, 0 failed.
  - New `SessionNsdrAndCodesTests`:
    - state machine: work → NSDR → same interval, break → NSDR, invalid transitions, dots, `AfterSessionNsdr`;
    - an NSDR during work keeps exactly 15:00 left and links its evidence;
    - stopping early returns to paused work with nothing recorded;
    - an NSDR in a short break outlives the 5:00 break and then offers START NEXT;
    - a FREE session is paused, stays paused and resumes, with evidence linked;
    - no session means no NSDR;
    - code cases; palette size, distinctness, no status colours, lightness, spread; standing colour; FNV pinned.
  - `DuePushAndStreakTests` updated to 23:59, with new cases: the phone's noon + 1D gives 23:59 and confirms
    on that day; `ShiftDue` ignores the time, works by local date not UTC, and holds across DST; `EndOfDay`
    and `IsDateOnly`.
  - `WriteBackTests`: a create sent with a midnight date goes out at `T23:59:00`.
- The app was not launched.
