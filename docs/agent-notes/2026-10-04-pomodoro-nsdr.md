# Pomodoro focus sessions + 10-minute NSDR (2026-10-04)

Builds on the Phase 2 focus sessions ([`2026-06-28-phase-2-focus-sessions.md`](2026-06-28-phase-2-focus-sessions.md)).

## What shipped

- **Timer mode on the Focus page**: `POMODORO · 25 / 5` (default) or `FREE · COUNT UP` (the
  original behaviour, unchanged).
- **Pomodoro**: 25:00 work counting down, 5:00 short break, 15:00 long break after every 4th work
  interval. Big countdown, phase label (WORK / BREAK / LONG BREAK / NSDR, plus WORK · PAUSED and
  BREAK OVER), dots for the current cycle of four (`●●○○`), "N POMODOROS COMPLETED", and the work
  time banked this session.
  - Work hits 0 → interval counted, session auto-paused, alert, break countdown starts.
  - Break hits 0 → alert, "BREAK OVER — START NEXT POMODORO"; work does **not** auto-start.
  - SKIP BREAK at any time during a break; Finish/Abandon and the end-of-session review work as before.
- **NSDR (non-sleep deep rest), 10:00**: `NSDR 10:00` button on the Focus setup screen (no session
  needed), and `TAKE NSDR INSTEAD` during a pomodoro long break. Calm panel: countdown, a thin
  progress meter, the current guide cue, "NEXT AT m:ss · …", and STOP. Six plain cues
  (0:00 settle/long exhale, 1:30 feet and legs, 3:30 torso/hands/arms, 5:30 face, 7:00 whole body,
  9:00 return) live as data in `Core/Domain/Nsdr.cs`.
  - Completing the full 10:00 writes one `EvidenceType.NsdrCompleted` evidence item (3 points;
    3 on the contribution graph via `ForContribution`). Stopping early writes nothing.
  - Optional `focus.nsdrAudioUrl` in `settings.json` (default `null`). When set, an OPEN AUDIO
    button opens it — an http(s) link in the browser, or an absolute path to a local audio file in
    the default player.
- **Command Center → Current Operation** shows the pomodoro countdown, phase and dots
  (`LTFI · WORK · ●●○○`), pomodoros + work time in the sub-line, and the run button becomes
  SKIP BREAK / END NSDR / START NEXT during breaks. A standalone NSDR shows its countdown there too.
- **Alert**: `System.Media.SystemSounds.Exclamation` (package `System.Windows.Extensions`, guarded
  by `OperatingSystem.IsWindows()`, `Console.Beep` otherwise) and the main window is restored if
  minimized, `Topmost` toggled and `Activate()`d. Every step is try/catch + logged.

## Key decisions

- **One pomodoro run = one `FocusSession`.** Breaks pause the session through the existing
  pause path, so `Duration` is work time only. Completed work intervals are counted in
  `FocusSession.PomodorosCompleted` (migration **`AddPomodoroAndNsdr`**, one int column, default 0),
  persisted on pause, pomodoro completion, finish and abandon — same points as `Duration`.
- **Work countdown is measured against the session's own elapsed** (`25:00 − (elapsed − elapsed at
  interval start)`), so a manual pause during work freezes the countdown for free. Breaks and NSDR
  run on the wall clock.
- **State lives in singletons** so it survives navigation: `FocusSessionService` (clock + pomodoro
  count), new `PomodoroService` (phase/break state) and `NsdrService` (NSDR run), all in
  Infrastructure so they're testable without the App project. The pure state machine is
  `PomodoroCycle` in `Core/Domain/Pomodoro.cs` (immutable; `CompleteWork`, `EndBreak`/`SkipBreak`,
  `TakeNsdrInstead`, `DotsFilled`).
- **One driver for transitions**: the singleton `FocusViewModel`'s 1-second tick calls
  `IPomodoroService.AdvanceAsync()` (or `INsdrService.CompleteIfDueAsync()` for a standalone NSDR)
  and raises the alert; the Command Center only reads snapshots, so an alert can't fire twice.
  `AdvanceAsync` is re-entrancy guarded; NSDR completion claims itself under a lock before writing
  so it is recorded exactly once. Ticks are skipped while the review form is open.
- **Services take an optional `TimeProvider`** (registered as `TimeProvider.System`); tests drive a
  fake clock through whole runs.
- **Review "Back" no longer always resumes**: it resumes only if the clock was running when Finish
  was clicked, so cancelling a review during a pomodoro break doesn't start counting the break.
- Pause/Resume is disabled during breaks (the session must stay paused); stopping an NSDR taken in
  place of a long break skips straight to the next pomodoro (nothing recorded).
- `EvidenceType.NsdrCompleted` is appended at the end of the enum; the evidence feed tags it `NSDR`.
  When taken during a pomodoro, the NSDR evidence links the session via `FocusSessionId`.

## Verification

- `dotnet build LTFI.sln --no-incremental` — 0 errors, 0 warnings.
- `dotnet test LTFI.sln` — 109 passed. New `PomodoroNsdrTests` (23): state machine (work → short
  break, 4th → long break, next cycle empties the dots, skip keeps the count, NSDR only replaces a
  long break, invalid transitions throw, phase durations), NSDR cue lookup by elapsed (boundaries
  incl. past 10:00), cue ordering, NSDR = 3 points / 3 contribution weight, NSDR evidence only on
  completion (and only once), `PomodorosCompleted` persisted on pause and finish, a full pomodoro
  run on a fake clock (session paused in breaks, break over waits, only work time recorded),
  long break → NSDR → evidence linked to the session, skipping an NSDR part-way records nothing.
- App not launched (per instructions); UI verified by compiled-binding build only.

## Deferred

- No pause for breaks/NSDR, and no custom interval lengths (constants in `Pomodoro`).
- Live pomodoro state does not survive an app restart (same as the session clock — Phase 7).
- Alert is a system sound + window raise; no toast notification, no custom chime.
- The Today page banner still shows plain elapsed, not the pomodoro countdown.
- Standalone NSDR is only offered from the Focus setup screen (not during a FREE session).
