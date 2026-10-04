# Areas, URL ids and the write-back outbox (2026-10-04)

The owner decided that iCloud Reminders is the **sole** source of truth for tasks. LTFI can no
longer create local-only tasks; it creates reminders on the iPhone through an outbox file and an
"LTFI Apply" Shortcut. Alongside that: areas (sub-divisions of a project), a standing "Life"
project that unmapped lists file into, and a URL-based reminder identity that survives renames.
iPhone-side setup: `docs/reminders-sync-setup.md`. Builds on `2026-10-03-icloud-reminders-sync.md`.

## What shipped

- **Migration `AddAreasAndOutbox`** (one migration for everything):
  - `ProjectAreas` table: `ProjectArea` = Id, ProjectId (cascade), Name, SortOrder, CreatedAt.
  - `Tasks.AreaId`: nullable FK, SetNull on area delete.
  - `Tasks.ExternalPendingSince`: set while LTFI has unconfirmed write-back commands for the task.
    It drives the derived `TaskItem.IsPendingOnPhone`.
  - `Projects.IsStanding` (bool).
  - `OutboxCommands` table: `OutboxCommand` = Id, Op, ExternalUrl (indexed), PayloadJson, CreatedAt, ConfirmedAt.
- **Areas.**
  - `IAreaService` / `AreaService`: list, create, rename, delete. Names are unique per project, case-insensitive. Deleting an area keeps its tasks with no area.
  - Projects page: an AREAS sub-panel (add, rename in place, remove) next to MILESTONES, plus a "Standing project" checkbox and a STANDING tag in the list.
  - Tasks page: an AREA picker filtered to the selected project, and a `· #area` tag in the list.
- **Standing projects.** These are exempt from the active-project limit in `ProjectService`. They are also excluded from `CountActiveAsync` (header meter), Today's count, the Command Center `n / 4 LIMIT`, and the review's `ActiveProjectCount`/`IsOverLimit` and stalled detection. They still show in the review's project-activity lines. A standing project becoming non-standing while Active is limit-checked.
- **List → project/area** (`ReminderSyncService.Placement`). Settings live in `settings.json` → `reminders`:
  - `standingProject` (default `"Life"`), `ltfiList` (default `"LTFI"`), and an optional `listMap` `{ "<list>": { "project", "area" } }`.
  - Unmapped list X → standing project, area X. The sync auto-creates the standing project (IsStanding, Active) and areas on demand, and sets an existing project with that title to standing.
  - This is applied to every reminder on every sync. The old "list name == project title" rule is gone.
  - Exception: an existing task whose ExternalId is an LTFI-minted url (`ltfi://r/<32 hex>`, see `ReminderRules.IsLtfiCreatedUrl`) keeps the project/area LTFI recorded.
- **Identity** (`ReminderJsonParser.AssignKeys`, `ReminderRules.ComposeKey`), in order:
  1. `url` starting with `ltfi://r/`, verbatim.
  2. A real `id`/`identifier`.
  3. `cd:<creationDate UTC to the second>`, with `|<lower trimmed title>` added only when 2+ reminders in the export share that creation date (counted across the whole export).

  Every item also carries its (3) key as `ExternalReminder.FallbackKey`, and the parsed `Url`. Title is no longer part of the normal key, so renames and list moves keep the task (tested). Old `sc:` keys are not migrated; the owner's DB will be wiped and re-synced.
- **Adoption.** A reminder arriving with an `ltfi://` url that no task has yet adopts the task keyed by its fallback key. It tries the export's FallbackKey, then the key with and without the title tiebreak. That task is re-keyed in place, so focus time and evidence are kept, with no duplicate row and no second evidence item.
- **Outbox** (`IReminderOutbox` / `ReminderOutbox`).
  - The file is `outbox.json`, in the same folder as the resolved export (`SettingsStore.ResolveOutboxPath`), schema `ltfi.outbox/v1`. Commands are flat string dictionaries.
  - It is rewritten from all unconfirmed rows on every change and after every successful sync pass. The write is atomic: `outbox.json.tmp` then `File.Move(overwrite)`.
  - If the folder doesn't exist, the write is skipped and `LastError` is set. Commands stay queued, and the next flush (sync pass or SYNC button) writes them.
  - The enqueue helpers run inside the caller's `SaveChanges`, so the task change and its command commit together.
- **TaskService.**
  - `CreateAsync` always makes a reminder-backed task: ExternalSource `icloud-reminders`, ExternalId `ltfi://r/<guid N>`, Ready, pending, plus a `create` command.
  - The list is the area name when the project is standing (an area is required, and it must belong to the project); otherwise it is `ltfiList`.
  - Completing a reminder-backed task (via `SetStatusAsync` or `UpdateAsync`) does three things: it records the usual evidence (`Source="task"`, RequiredTime enforced), sets status Completed, and queues a `complete` command.
  - A reminder without an ltfi url can't be completed: the call throws `TaskService.NoLtfiIdMessage` ("…complete it on your phone (no LTFI id yet — export with URL stamping first)").
  - Editing a task whose `create` is still unconfirmed rewrites that command's payload. Deleting it withdraws its unconfirmed commands.
- **Sync confirmation.**
  - A `create` is confirmed when the export has that url. A `complete` is confirmed when that url is completed. `ConfirmedAt` is set, and confirmed rows are kept as history.
  - `ExternalPendingSince` is cleared once a task has no unconfirmed commands.
  - A task whose create is unconfirmed is not marked removed when it is missing from the export.
  - `ReminderSyncResult.Confirmed` is new and counts toward `HasChanges`.
- **UI.**
  - "New Task" says where the reminder will land ("Creates a reminder in the iPhone list …").
  - Pending tasks show a **PENDING ON iPHONE** chip on Tasks, on Today, and in the Reminders panel rows.
  - The REMINDERS panel shows `OUTBOX n → iPHONE` plus any outbox write error, and SYNC also flushes the outbox.
  - Project/area pickers are disabled for phone-made reminders, because their placement follows the list.
  - Subtasks stay LTFI-local checklists.

## Decisions / deviations

- **Medium → `"None"`** in the outbox's `priority`. Medium is LTFI's default and None imports as Medium, so sending "Medium" would put `!!` on every LTFI-made reminder. Urgent → High.
- **Date-only `dueDate`** for local-midnight due dates (all-day reminders). Otherwise it is full ISO 8601 with offset.
- **No creation date → the title is always appended** (`cd:|title`). Otherwise every date-less reminder would collide on `cd:`.
- **A missing `listMap` project is not auto-created.** It falls back to the default rule (standing project, area = list name), so the sync can never exceed the active-project limit.
- **"LTFI-created" is recognised syntactically**: the `ltfi://r/` prefix plus exactly 32 hex digits. Shortcut-stamped ids are `yyyyMMddHHmmss-NNNNN`, so they never match. This needs no extra column, and it also works when the outbox row is gone.
- **Existing "Life"-titled project:** the sync marks it standing. A standing project that is *paused* stays paused; the sync only creates the project as Active when it doesn't exist.
- **"Remove local-only creation":** the "New Task" button stays, but it always routes through the outbox (there is no local-only path in `TaskService`). Today never had a quick-add.
- **Renaming a Life area** in LTFI doesn't rename the phone list, and the next sync re-creates an area with the list's name. The UI says so after a rename.
- **Moving an LTFI-made task to another project/area after the iPhone created it** changes LTFI's placement only. The reminder stays in its list, because there is no "move" command.

## Verification

- `dotnet build LTFI.sln`: 0 warnings, 0 errors. That includes LTFI.App, so the XAML compiled bindings were checked.
- `dotnet test LTFI.sln`: **87 passed**, 0 failed. 15 tests are new or replaced:
  - parser: key precedence (url > id > cd, a non-ltfi url is ignored) and the collision tiebreak;
  - sync: Life/area mapping (with the old title rule gone), the `listMap` override and missing-project fallback, rename/move keeps the task, adoption re-key without duplicate evidence;
  - `WriteBackTests`: area CRUD and uniqueness; standing exemption from the limit, the meter, the review count and stalled detection; no local-only creation; a standing task needs an area, which becomes the list; outbox file contents (flat strings, date-only due, None priority, no `.tmp` left behind); a pending edit rewrites the payload and a delete withdraws it; create and complete confirmed by later exports with one evidence item; LTFI-created placement survives a list move; completion blocked without an ltfi url; the focus gate still applies; a missing iCloud folder keeps commands queued.
- I did not launch the app, and nothing touched `%AppData%\LTFI`.

## Deferred

- Needs the owner, on the phone: add the "stamp URLs" step to LTFI Export, build **LTFI Apply** and run it first in the export, create the **LTFI** list, and check that *Edit Reminder* exposes URL and *Find Reminders* can filter by URL on their iOS (the setup doc has fallbacks for both).
- The owner's DB still holds old `sc:` keys and needs a wipe and re-sync. There is no migration for those keys, by decision.
- No write-back for edits (title/due/list/priority) or deletes. Uncompleting in LTFI isn't sent to the phone.
- Settings UI for `standingProject` / `ltfiList` / `listMap`. It is still settings.json plus a restart.
- Deleting a phone-made task in LTFI just comes back on the next sync. Consider hiding Delete for reminder-backed tasks.
