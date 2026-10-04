# iCloud Reminders ↔ LTFI setup

iCloud Reminders is LTFI's **only** task source. Every LTFI task is a reminder. Apple has no
Reminders API for Windows, so the **iPhone does the work** in both directions:

```
phone → LTFI:  Shortcut "LTFI Export"  →  iCloud Drive/LTFI/reminders.jsonl.json
                 →  iCloud for Windows  →  %USERPROFILE%\iCloudDrive\LTFI\reminders.jsonl.json
                 →  LTFI (checks the file every 15 s, and SYNC on the Today page)

LTFI → phone:  LTFI writes %USERPROFILE%\iCloudDrive\LTFI\outbox.json
                 →  iCloud for Windows  →  iCloud Drive/LTFI/outbox.json
                 →  Shortcut "LTFI Apply" (runs at the start of every export) creates / completes reminders
```

Only Apple-supported parts are used. No Apple ID password or 2FA session is stored on the PC, and
it keeps working with Advanced Data Protection on. Data is as fresh as the last time the Shortcuts
ran, which is usually minutes to a few hours. It is not real time.

What flows where:

- **Phone → LTFI:** every reminder becomes an LTFI task. Title, notes, due date, priority, list and
  completion come from the phone. A reminder completed on the phone completes the LTFI task and
  counts as `TaskCompleted` evidence (points and the activity graph), dated when you completed it.
  The RequiredTime focus gate does not apply to phone completions.
- **LTFI → phone:** **New Task** in LTFI creates a reminder (it shows **PENDING ON iPHONE** until it
  comes back in an export), and **completing** a task in LTFI ticks the reminder off. That's all the
  write-back does. Edit titles, dates, lists etc. on the phone.

---

## 1. Windows: install iCloud for Windows (one-time, ~5 min)

1. Install **iCloud** from the Microsoft Store and sign in with your Apple ID.
2. Turn on **iCloud Drive**.
3. After the first export (step 3), open File Explorer → **iCloud Drive → LTFI**, right-click the
   folder → **Always keep on this device**. Otherwise the files can stay cloud-only placeholders.
4. LTFI looks in these places, in order:
   - `%USERPROFILE%\iCloudDrive\LTFI\reminders.jsonl.json` (the owner's current setup;
     `reminders.json` and `reminders.jsonl` also work)
   - the same names under `iCloud Drive\LTFI\` and the older `Shortcuts\LTFI\` folders

   LTFI writes `outbox.json` into **the same folder** as the export it found.

   If yours is somewhere else, set it in `%AppData%\LTFI\settings.json`, which LTFI creates on first run:
   ```json
   { "reminders": { "snapshotPath": "D:\\Somewhere\\reminders.json" } }
   ```
   Restart LTFI after you edit the settings file. Until the file exists, the Today page's REMINDERS panel
   shows **Not connected yet** and the path it expects.

> No iCloud for Windows? You can still save or AirDrop/email the export from the iPhone and put it at
> the configured path by hand. LTFI picks it up the same way (write-back then needs the reverse trip).

## 2. iPhone: build the "LTFI Export" Shortcut (one-time, ~30–45 min)

Shortcuts app → **+** → name it **LTFI Export**.

0. **First action: Run Shortcut → LTFI Apply** (built in step 3 below; add this line once it exists).
   That way LTFI's pending creates/completes are applied *before* the export, and the export
   confirms them in the same round trip.
1. **Find Reminders** where *Is Completed* is **false**. Sort by Due Date, no limit. Then
   **Set Variable** `Open`.
2. **Find Reminders** where *Is Completed* is **true** **and** *Completion Date* is **in the last 30
   days**. Then **Set Variable** `Done`. (The 30-day cap keeps the loop fast. LTFI doesn't treat completed
   reminders that age out as deleted.)
3. **Add to Variable** `All`: first `Open`, then `Done`.
4. **Repeat with Each** item in `All`:
   1. Read the item's properties: Title, Notes, List, Due Date, Priority, Is Completed,
      Completion Date, Creation Date, URL. Tap the *Repeat Item* magic variable to pick a property, or
      use **Get Details of Reminders**.
   2. For **each date** (Due Date, Completion Date, Creation Date):
      **If** `<date>` *has any value* → **Format Date** (ISO 8601, *Include Time* ON) → Set Variable;
      **Otherwise** → **Text** (empty) → Set Variable.
      ⚠️ Never feed an empty date to Format Date. It quietly turns into *now*.
   3. **Stamp an LTFI id into the URL** (this is what makes renames safe and lets LTFI tick a reminder off):
      1. **Get Details of Reminders** → *URL* of *Repeat Item* → **Set Variable** `ReminderURL`.
      2. **If** `ReminderURL` *does not have any value*:
         1. **Format Date** → *Creation Date* (the real date, not the formatted text), Date Format
            **Custom**, format string `yyyyMMddHHmmss`.
         2. **Random Number** between **10000** and **99999**.
         3. **Text**: `ltfi://r/` + *Formatted Date* + `-` + *Random Number*
            (e.g. `ltfi://r/20261004093000-48213`).
         4. **Edit Reminder** → *Repeat Item* → set **URL** to that Text.
            ⚠️ Check that *Edit Reminder* offers **URL** on your iOS version. If it doesn't, skip
            stamping: leave the URL empty and LTFI keys the reminder by its creation date instead
            (it still works, but LTFI can't complete that reminder for you).
         5. **Set Variable** `ReminderURL` to that Text.
      3. **Otherwise** (the URL already has a value) — leave it alone. If it's an `ltfi://` id it
         is used as the key. If it's a real link (a web page you attached), **don't overwrite it**:
         send it as-is and LTFI falls back to the creation-date key for that reminder.
      4. **End If**.
   4. **Dictionary** with these keys (Text type), each set to the variables above:
      | key | value |
      |---|---|
      | `title` | Title |
      | `notes` | Notes |
      | `list` | List (its name) |
      | `dueDate` | formatted Due Date or empty |
      | `priority` | Priority (None/Low/Medium/High) |
      | `isCompleted` | Is Completed |
      | `completionDate` | formatted Completion Date or empty |
      | `creationDate` | formatted Creation Date or empty (**needed for the fallback key**) |
      | `url` | `ReminderURL` |

      Always use the Dictionary action. It escapes the JSON for you, so never build JSON by joining text.
5. After **End Repeat**: **Dictionary** `{ schema: "ltfi.reminders/v1", source: "shortcuts",
   exportedAt: <Current Date, Format Date ISO 8601> }` → **Set Dictionary Value** key `reminders` =
   *Repeat Results*.
   - If the result isn't a proper array on your iOS version, fall back to:
     *Repeat Results* → **Combine Text** with New Lines → save as **`reminders.jsonl`**.
     LTFI accepts that too.
6. **Save File** → Service: **iCloud Drive**, path **`/LTFI/reminders.jsonl`** (at the iCloud Drive
   root; Save File appends `.json`, giving `reminders.jsonl.json`). Turn *Ask Where to Save* OFF and
   *Overwrite If File Exists* ON.

Run it once by hand, then check that `reminders.jsonl.json` appears on the PC.

> **As built by the owner (2026-10-04):** the dictionary keys are `title, notes, list, dueDate,
> priority, isCompleted, completionDate, creationDate`; `url` is being added with the stamping step.
> `isCompleted` comes through as `"Yes"`/`"No"` and empty dates as `""`. The parser handles
> all of these. The lists are **TODO GENERAL, GATECH, HOMEWORK, job** (plus **LTFI**, which LTFI
> creates reminders in — make that list once on the phone).

## 3. iPhone: build the "LTFI Apply" Shortcut (one-time, ~20–30 min)

This applies `outbox.json`. It only ever **adds** reminders and **ticks them off**; it never deletes
or edits anything else. Every step is safe to repeat: LTFI keeps listing a command until an export
shows it done, so the Shortcut may see the same command several times.

Shortcuts app → **+** → name it **LTFI Apply**.

1. **Get File** → Service **iCloud Drive**, path **`/LTFI/outbox.json`**, *Show Document Picker*
   OFF, *Error If Not Found* **OFF**.
2. **If** *File* **does not have any value** → **Stop This Shortcut**. **End If**.
   (No outbox yet — nothing to do.)
3. **Get Dictionary from Input** (the File) → **Get Dictionary Value** key **`commands`** →
   **Set Variable** `Commands`.
4. **Repeat with Each** item in `Commands`:
   1. **Get Dictionary Value** `op` from *Repeat Item* → Set Variable `Op`;
      **Get Dictionary Value** `url` → Set Variable `CmdURL`.
   2. **Find the reminder by URL:** **Find Reminders** where **URL** *is* `CmdURL` (limit 1) →
      Set Variable `Match`.
      - If *Find Reminders* can't filter by URL on your iOS: **Find Reminders** (all, including
        completed) → **Repeat with Each** → **If** *URL* (of that reminder) *is* `CmdURL` →
        **Add to Variable** `Match` → End If → End Repeat.
   3. **If** `Op` *is* `create`:
      1. **If** `Match` **does not have any value** (this existence check is what makes it idempotent):
         1. **Get Dictionary Value** `title`, `notes`, `list`, `dueDate`, `priority` from the
            *Repeat Item* (one action each, Set Variable each).
         2. **Add New Reminder**: Title = `title`, List = `list` (pick *Variable* for the list
            and pass the name), Notes = `notes`, URL = `CmdURL`, Priority = `priority`.
            - Due date: **If** `dueDate` *has any value* → **Get Dates from Input** (`dueDate`)
              → set it as the reminder's Due Date (use **Edit Reminder → Due Date** on the new
              reminder if *Add New Reminder* won't take a variable date). An all-day task comes
              as a date only (`2026-10-10`), a timed one as full ISO 8601.
         3. **End If** (already exists → nothing to do).
   4. **Otherwise, If** `Op` *is* `complete`:
      1. **If** `Match` **has any value** → **Edit Reminder** `Match` → set **Is Completed** to
         **on**. **End If**. (Not there yet? The next run will catch it once the create is applied.)
   5. **End If**.
5. **End Repeat**.

Then open **LTFI Export** and add **Run Shortcut → LTFI Apply** as its very first action (step 2.0).

Field notes:

- `outbox.json` looks like this; every value is a plain string (empty string = not set):
  ```json
  {
    "schema": "ltfi.outbox/v1",
    "writtenAt": "2026-10-04T18:20:00-04:00",
    "commands": [
      { "op": "create", "url": "ltfi://r/3f2c…e1", "title": "Order filament", "notes": "",
        "list": "LTFI", "dueDate": "2026-10-10", "priority": "None" },
      { "op": "complete", "url": "ltfi://r/20261001090000-54321" }
    ]
  }
  ```
- `priority` is `None`/`Low`/`Medium`/`High`. LTFI's default (Medium) goes out as `None`.
- Which list a new reminder goes into: tasks under the standing project **Life** go into the list
  named by their **area** (e.g. Life / GATECH → list GATECH). Every other task goes into the list
  **LTFI** (`reminders.ltfiList` in settings.json). Create that list on the phone once.

## 4. iPhone: make it automatic

Shortcuts → **Automation** → **+** → **Personal Automation**:

- **Time of Day**, e.g. 07:00, 12:00, 17:00 and 21:00, Daily → **Run Immediately** (Ask Before Running OFF,
  Notify When Run OFF) → *Run Shortcut* **LTFI Export** (which runs LTFI Apply first). Make one
  automation per time.
- **App** → **Reminders** → **Is Closed** → **Run Immediately** → *Run Shortcut* **LTFI Export**.
  After this, closing the Reminders app pushes your latest changes.
- Optional: add the Shortcut to the Home Screen or the Action Button as a manual "sync now".

iOS may delay Time of Day runs (Low Power Mode, phone off). If the newest export is more than 24 h old,
LTFI shows a stale warning in the REMINDERS panel. The panel also shows how many outbox commands are
still waiting for the iPhone.

---

## How LTFI treats the data

- **Key** (identity), in order:
  1. a `url` starting with `ltfi://r/` — used verbatim;
  2. a real `id` field, if your export has one;
  3. otherwise `cd:<creationDate in UTC to the second>`, e.g. `cd:2026-10-01T13:00:00Z`. Only when
     two reminders in the same export share that creation date is `|<lower-cased title>` added
     to tell them apart.

  The title and list are not part of the key, so renaming a reminder or moving it to another list
  keeps the same LTFI task.
- **Adoption.** When the export Shortcut stamps a URL on a reminder LTFI already knows by its
  creation-date key, LTFI re-keys that task to the URL. History (focus time, completion evidence) is
  kept and nothing is duplicated.
- **Projects and areas.** Each reminder is filed under a project and an *area* (a sub-division of a
  project) from its list:
  - `reminders.listMap` in settings.json, if the list is in it:
    ```json
    { "reminders": { "listMap": { "GATECH": { "project": "School", "area": "Classes" } } } }
    ```
    The project must already exist in LTFI (it is not auto-created, so the sync never hits the
    active-project limit). If it doesn't, the default rule applies.
  - otherwise the **standing project** `Life` (`reminders.standingProject`), area = the list name.
    So by default TODO GENERAL, GATECH, HOMEWORK and job become Life / TODO GENERAL, Life / GATECH, ….
    LTFI creates Life (marked *standing*: exempt from the active-project limit and from "stalled")
    and the areas on demand.

  This is re-applied on every sync. The exception is a reminder **LTFI created**: it keeps the
  project/area you gave it in LTFI, whatever list it lives in.
- **Upsert.** Title, notes, due date, priority, list and project/area come from the phone on every
  sync. LTFI's In-progress/Deferred status and focus time are kept.
- **Priority.** High → High (flagged + High → Urgent), Medium → Medium, Low → Low, None → Medium.
  pyicloud-style numbers 1/5/9/0 map the same way.
- **Completion.** Completed on the phone: the task is completed with the phone's completion date,
  and one `TaskCompleted` evidence item is recorded. Re-syncing never duplicates it. Completed in
  LTFI: evidence is recorded right away (the RequiredTime gate applies), a `complete` command goes
  into the outbox, and when the export later shows it completed, no second evidence is written.
  A reminder with no `ltfi://` URL yet can't be targeted from LTFI, so completing it in LTFI is
  blocked — complete it on the phone (or let the export Shortcut stamp a URL first). Reopening a
  reminder on the phone does not reopen the LTFI task.
- **Outbox confirmation.** A `create` is confirmed when an export contains a reminder with that URL;
  a `complete` when that URL is completed. Confirmed commands drop out of `outbox.json`; the file
  always lists every unconfirmed one and is replaced atomically (written to `outbox.json.tmp`, then
  swapped in).
- **Removed.** An open reminder that is missing from a non-empty export is marked removed (canceled,
  `ExternalRemovedAt` set) and is never deleted. It comes back if the reminder reappears. An empty export
  removes nothing, because it's treated as a glitch. A task LTFI created that the phone hasn't made
  yet is pending, not removed.
- **Broken file.** A half-written or malformed export is ignored. The last good mirror stays and the
  panel shows the error.

## Accepted file shapes (`ltfi.reminders/v1`)

1. `{"schema":"ltfi.reminders/v1","exportedAt":"…","source":"shortcuts","reminders":[{…},…]}`
2. A bare array `[{…},…]`
3. JSON Lines: one reminder object per line

Keys are case-insensitive and unknown keys are ignored. Only `title` is required. Dates are ISO 8601,
and a date-only value is all-day. Booleans may be `true/false`, `"Yes"/"No"` or `1/0`.

## Later / optional

- **pyicloud sidecar** (`pip install pyicloud>=2.7`): a fallback producer that writes the same JSON
  without iCloud for Windows. It is unofficial, its 2FA needs re-auth about every 2 months, and it does not
  work with Advanced Data Protection. Build it only if the Shortcut route proves annoying.
- **CalDAV is not an option.** Upgraded iCloud Reminders no longer sync over CalDAV.
