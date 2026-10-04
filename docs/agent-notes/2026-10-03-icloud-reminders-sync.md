# iCloud Reminders mirror, LTFI side (2026-10-03)

The owner uses iCloud Reminders as their real task system and won't leave it, so LTFI now
**mirrors** it. An iPhone Shortcut exports the reminders as JSON into iCloud Drive, iCloud for
Windows syncs the file to the PC, and LTFI upserts it onto `TaskItem` rows. Completions on the
phone become `TaskCompleted` evidence, so they count toward points, streaks and the activity graph.
The iPhone-side setup is in `docs/reminders-sync-setup.md`.

## What shipped

- **Core.**
  - `ExternalReminder` DTO.
  - `ReminderRules`: `SourceKey = "icloud-reminders"`, `ComposeKey` and `MapPriority`. These are pure rules shared by every producer.
  - `IReminderSource`: `Probe()` returns availability and a change token; `ReadAsync()` returns a `ReminderSnapshot` and throws `ReminderSourceException`.
  - `IReminderSyncService`: `SyncAsync`, `SyncIfChangedAsync`, `GetOpenAsync` and `GetStatus`, plus the `ReminderSyncResult` / `ReminderSyncStatus` records.
- **`TaskItem`** gained `ExternalSource`, `ExternalId`, `ExternalList` and `ExternalRemovedAt`, plus a derived `IsExternal`. Migration **`AddReminders`** adds the four columns and a unique index on `(ExternalSource, ExternalId)`. Native tasks have NULLs there, which SQLite allows.
- **Infrastructure.**
  - `ReminderJsonParser` (tolerant v1 contract). It accepts:
    - a wrapper object, a bare array, or JSON Lines;
    - array elements that are JSON *strings*, and a `reminders` value that is a newline-joined string (both are Shortcuts quirks);
    - case-insensitive keys, a `list` given as text or as an object;
    - booleans as Yes/No, true/false or 1/0, and empty-string dates as null.

    Items with a blank title are skipped. Anything structurally broken throws.
  - `FileReminderSource` re-resolves its path on every probe. Reads use `FileShare.ReadWrite`, with 3 retries on IOException.
  - `SettingsStore` / `LtfiSettings` is a new `%AppData%/LTFI/settings.json`, written with defaults on first run. `reminders.snapshotPath` is auto-detected when null: it tries `iCloudDrive\Shortcuts\LTFI` and then `iCloud Drive\Shortcuts\LTFI`, each for `reminders.json` and `.jsonl`.
  - `ReminderSyncService` is a singleton guarded by a `SemaphoreSlim`. It does one `SaveChanges` per pass.
- **UI.**
  - **REMINDERS panel** on the Today page (`RemindersPanelViewModel`): open reminders grouped by list, with priority marks and due labels colour-coded by slack (late/today/tomorrow/weekday/date). It also has a SYNC button, a "SYNCED hh:mm · IPHONE EXPORT 3H AGO · n ITEMS" status line, a warning when the export is more than 24 h old, a sync error line, and a **Not connected yet** state that shows the expected file path and points to the setup doc.
  - The shell runs `SyncIfChangedAsync` on start-up and on its existing 15 s tick. That is only a file stat when nothing changed. A sync that changed data refreshes the header, plus Today or the Command Center if one of them is showing. Tasks is deliberately not refreshed, so an in-progress edit isn't clobbered.
  - Tasks page: mirrored tasks show `· iCLOUD <list>` in the list. The editor explains which fields the phone owns.

## Key decisions / gotchas

- **Mapped onto `TaskItem` rather than a separate `ExternalReminder` table.** The owner wants
  Reminders to be the *main task function*. As TaskItems, reminders automatically show on Today
  (due today), in Command Center Upcoming, and in the Tasks list. They can also be focus-session
  targets, so focus time attaches to them. Weekly review counts them too. All of that needs no new code paths. A
  separate table would have meant duplicating every one of those surfaces or joining across two task
  models. The research recommended the same thing. The cost is that the phone owns title, notes, due date and
  priority, and a sync overwrites them. LTFI-only state (project, InProgress/Deferred, focus time) is
  kept.
- **Evidence type.** It is the existing `TaskCompleted` with `Source = "icloud-reminders"`, not a
  new enum value. It earns the same 10 points as an LTFI completion, and it needs no Scoring change. It is
  dated at the phone's `completionDate`, falling back to `exportedAt` and then now. The first sync
  backfills up to 30 days of completions, which seeds the contribution graph. That is intended.
- **Dedupe/idempotency.** There is at most **one** reminder completion evidence per task, checked
  against existing `Source == icloud-reminders` rows. Evidence is only written on an
  open→completed transition. If the task was already completed locally, no second evidence is written.
- **External completion bypasses the RequiredTime gate**, because the phone is authoritative. A
  reminder **reopened** on the phone does **not** reopen a completed LTFI task. Without write-back
  that would make a task completed in LTFI flip back every sync. This is documented as accepted.
- **Removal.** An open mirrored task that is missing from a **non-empty** export gets
  `ExternalRemovedAt` and `Canceled`. The row and its focus history stay, and it is restored if the
  reminder reappears. Completed tasks that age out of the 30-day export window are left alone.
  An empty export removes nothing, because it is treated as a glitched Shortcut run.
- **Key.** The real `id` is used when present. Otherwise the key is
  `sc:<list>|<creationDate UTC to the second>|<title>`. It is deliberately **unhashed** so a future
  write-back Shortcut can rebuild it, since Shortcuts has no SHA1. Renaming a reminder counts as remove + add.
- **Malformed file.** It fails softly: the last good mirror stays and the panel shows the error. The
  change token is remembered so the 15 s poll doesn't re-read a broken file. The next export or a
  manual SYNC retries.
- **Polling, not FileSystemWatcher.** The watcher is unreliable on iCloud placeholder files.
- Time filtering and ordering run in memory, the same as the other services.

## Verification

- `dotnet build LTFI.sln`: clean, 0 warnings.
- `dotnet test LTFI.sln`: **39 passed** (27 existing + 12 new in `ReminderSyncTests`). The new tests
  cover the fixture (`tests/.../Fixtures/reminders.json`) parse, which includes mixed-case keys, a list
  given as an object, empty dates, priority mapping and a real id vs a composite key. They also cover:
  - array, JSONL and string-element shapes producing identical keys;
  - 5 malformed inputs;
  - upsert plus an idempotent re-sync, where the unchanged file isn't re-read;
  - phone completion: a single evidence item dated at the completion date, which survives re-exports and a fresh service, and bypasses RequiredTime;
  - removal marking, the empty-export guard and restore;
  - a missing file (not configured) and a malformed file that keeps the last mirror;
  - list → project mapping.
- I did not launch the app, so I wouldn't have to touch the owner's real `%AppData%/LTFI` DB from an
  unmerged branch. The XAML compiled-binding check passes at build time.

## Deferred / follow-ups

- **iPhone + Windows setup is the owner's job.** They need to install iCloud for Windows, build the Shortcut and add the
  automations (see the setup doc). They should also check whether their iOS exposes a reminder
  *Identifier* in Shortcuts, because real ids make renames safe.
- Write-back (`complete-requests.json` + an "LTFI Apply" Shortcut) and the optional pyicloud sidecar
  are both drop-in producers or consumers behind `IReminderSource`.
- A Settings UI for `snapshotPath`. Today it is a JSON file that needs a restart after editing.
- Completing a mirrored task in LTFI currently only completes it locally. Until write-back exists,
  the panel could show it as "pending on iPhone".
