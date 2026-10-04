# iCloud Reminders → LTFI setup

LTFI keeps a read-only copy of your iCloud Reminders. Apple has no Reminders API for Windows, so
the **iPhone does the exporting**:

```
iPhone Shortcut "LTFI Export"  →  iCloud Drive/LTFI/reminders.jsonl.json
      →  iCloud for Windows  →  %USERPROFILE%\iCloudDrive\LTFI\reminders.jsonl.json
      →  LTFI (checks the file every 15 s, and SYNC on the Today page)
```

Only Apple-supported parts are used. No Apple ID password or 2FA session is stored on the PC, and
it keeps working with Advanced Data Protection on. Data is as fresh as the last time the Shortcut
ran, which is usually minutes to a few hours. It is not real time.

Reminders stay the source of truth. LTFI never edits them. A reminder you complete on the phone
completes the matching LTFI task, and that completion counts as `TaskCompleted` evidence (points
and the activity graph). It is dated when you actually completed it, and the RequiredTime focus
gate does not apply. Ticking reminders off *from* LTFI ("write-back") is a later phase.

---

## 1. Windows: install iCloud for Windows (one-time, ~5 min)

1. Install **iCloud** from the Microsoft Store and sign in with your Apple ID.
2. Turn on **iCloud Drive**.
3. After the first export (step 3), open File Explorer → **iCloud Drive → Shortcuts → LTFI**, right-click the
   folder → **Always keep on this device**. Otherwise the file can stay a cloud-only placeholder.
4. LTFI looks in these places, in order:
   - `%USERPROFILE%\iCloudDrive\LTFI\reminders.jsonl.json` (or `.jsonl`)
   - `%USERPROFILE%\iCloud Drive\Shortcuts\LTFI\reminders.json` (or `.jsonl`)

   If yours is somewhere else, set it in `%AppData%\LTFI\settings.json`, which LTFI creates on first run:
   ```json
   { "reminders": { "snapshotPath": "D:\\Somewhere\\reminders.json" } }
   ```
   Restart LTFI after you edit the settings file. Until the file exists, the Today page's REMINDERS panel
   shows **Not connected yet** and the path it expects.

> No iCloud for Windows? You can still save or AirDrop/email the file from the iPhone and put it at
> the configured path by hand. LTFI picks it up the same way.

## 2. iPhone: build the "LTFI Export" Shortcut (one-time, ~30–45 min)

Shortcuts app → **+** → name it **LTFI Export**.

1. **Find Reminders** where *Is Completed* is **false**. Sort by Due Date, no limit. Then
   **Set Variable** `Open`.
2. **Find Reminders** where *Is Completed* is **true** **and** *Completion Date* is **in the last 30
   days**. Then **Set Variable** `Done`. (The 30-day cap keeps the loop fast. LTFI doesn't treat completed
   reminders that age out as deleted.)
3. **Add to Variable** `All`: first `Open`, then `Done`.
4. **Repeat with Each** item in `All`:
   1. Read the item's properties: Title, Notes, List, Due Date, Priority, Is Flagged, Is Completed,
      Completion Date, Creation Date, URL. Tap the *Repeat Item* magic variable to pick a property, or
      use **Get Details of Reminders**. **If your iOS shows an "Identifier" property, include it as
      `id`.** A real id makes renames safe.
   2. For **each date** (Due Date, Completion Date, Creation Date):
      **If** `<date>` *has any value* → **Format Date** (ISO 8601, *Include Time* ON) → Set Variable;
      **Otherwise** → **Text** (empty) → Set Variable.
      ⚠️ Never feed an empty date to Format Date. It quietly turns into *now*.
   3. **Dictionary** with these keys (Text/Boolean types as shown), each set to the variables above:
      | key | value |
      |---|---|
      | `title` | Title |
      | `notes` | Notes |
      | `list` | List (its name) |
      | `dueDate` | formatted Due Date or empty |
      | `priority` | Priority (None/Low/Medium/High) |
      | `isFlagged` | Is Flagged (Boolean) |
      | `isCompleted` | Is Completed (Boolean) |
      | `completionDate` | formatted Completion Date or empty |
      | `creationDate` | formatted Creation Date or empty (**needed for the key**) |
      | `url` | URL |
      | `id` | Identifier, *only if available* |

      Always use the Dictionary action. It escapes the JSON for you, so never build JSON by joining text.
5. After **End Repeat**: **Dictionary** `{ schema: "ltfi.reminders/v1", source: "shortcuts",
   exportedAt: <Current Date, Format Date ISO 8601> }` → **Set Dictionary Value** key `reminders` =
   *Repeat Results*.
   - If the result isn't a proper array on your iOS version, fall back to:
     *Repeat Results* → **Combine Text** with New Lines → save as **`reminders.jsonl`**.
     LTFI accepts that too.
6. **Save File** → Service: **iCloud Drive**, path **`/LTFI/reminders.json`** (inside the Shortcuts
   folder). Turn *Ask Where to Save* OFF and *Overwrite If File Exists* ON.

Run it once by hand, then check that `reminders.json` appears on the PC.

## 3. iPhone: make it automatic

Shortcuts → **Automation** → **+** → **Personal Automation**:

- **Time of Day**, e.g. 07:00, 12:00, 17:00 and 21:00, Daily → **Run Immediately** (Ask Before Running OFF,
  Notify When Run OFF) → *Run Shortcut* **LTFI Export**. Make one automation per time.
- **App** → **Reminders** → **Is Closed** → **Run Immediately** → *Run Shortcut* **LTFI Export**.
  After this, closing the Reminders app pushes your latest changes.
- Optional: add the Shortcut to the Home Screen or the Action Button as a manual "sync now".

iOS may delay Time of Day runs (Low Power Mode, phone off). If the newest export is more than 24 h old,
LTFI shows a stale warning in the REMINDERS panel.

---

## How LTFI treats the data

- **Key.** If the export has an `id`, LTFI uses it. Otherwise the key is
  `sc:<list>|<creationDate UTC to the second>|<title>`, lower-cased. Renaming a reminder or moving it to
  another list changes the key, so LTFI sees one reminder removed and one added. That is fine for a mirror.
- **Upsert.** Title, notes, due date, priority and list come from the phone on every sync. Your LTFI-side
  project link, In-progress/Deferred status and focus time are kept. If a list name matches an LTFI
  project title (case-insensitive), reminders in that list that don't have a project yet are filed under it.
- **Priority.** High → High (flagged + High → Urgent), Medium → Medium, Low → Low, None → Medium.
  pyicloud-style numbers 1/5/9/0 map the same way.
- **Completion.** The task is completed with the phone's completion date, and one `TaskCompleted`
  evidence item is recorded. Re-syncing never duplicates it. Reopening a reminder on the phone does
  not reopen the LTFI task.
- **Removed.** An open reminder that is missing from a non-empty export is marked removed (canceled,
  `ExternalRemovedAt` set) and is never deleted. It comes back if the reminder reappears. An empty export
  removes nothing, because it's treated as a glitch.
- **Broken file.** A half-written or malformed file is ignored. The last good mirror stays and the
  panel shows the error.

## Accepted file shapes (`ltfi.reminders/v1`)

1. `{"schema":"ltfi.reminders/v1","exportedAt":"…","source":"shortcuts","reminders":[{…},…]}`
2. A bare array `[{…},…]`
3. JSON Lines: one reminder object per line

Keys are case-insensitive and unknown keys are ignored. Only `title` is required. Dates are ISO 8601,
and a date-only value is all-day. Booleans may be `true/false`, `"Yes"/"No"` or `1/0`.

## Later / optional

- **Write-back:** LTFI would write `Shortcuts/LTFI/complete-requests.json`
  (`{"schema":"ltfi.complete/v1","requests":[{"key":…,"title":…,"list":…,"requestedAt":…}]}`), and the
  Shortcut would mark those reminders done with *Edit Reminder → Set Is Completed* before each export.
  This works because the composite key is a plain string the Shortcut can rebuild.
- **pyicloud sidecar** (`pip install pyicloud>=2.7`): a fallback producer that writes the same JSON
  without iCloud for Windows. It is unofficial, its 2FA needs re-auth about every 2 months, and it does not
  work with Advanced Data Protection. Build it only if the Shortcut route proves annoying.
- **CalDAV is not an option.** Upgraded iCloud Reminders no longer sync over CalDAV.
