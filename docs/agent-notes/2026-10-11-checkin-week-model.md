# Weekly check-in week model: review the ending week, Sat/Sun window (2026-10-11)

Replaces the check-in timing from `2026-10-03-weekly-checkin.md` and the commitment weeks from
`2026-10-04-commitments-graph-click.md`.

**Why:** the old rule was "due when nothing has been saved since the most recent Sunday 00:00,
and it stays due all week". The owner submitted on Saturday 2026-10-10 and was prompted again on
Sunday 2026-10-11, because Sunday started a new "week".

## The model

- A week runs **Monday 00:00 → Sunday 23:59:59**, local wall clock. A check-in **reviews the
  week that is ending**.
- Each week W has a window:

  | Moment (default) | What happens |
  |---|---|
  | `opens` Sat 00:00 | The CHK form is available. Header chip `CHECK-IN DUE SUN 23:59` (amber). No gate. |
  | `gateFrom` Sun 18:00 | The app is gated until you submit. You can snooze. Chip stays amber. |
  | `due` Sun 23:59 | The last minute that counts as on time. |
  | Mon 00:00 of W+1 | **Overdue.** The gate comes up at once (snoozable), and the chip turns red: `CHECK-IN OVERDUE`. |
  | Sat 00:00 of W+1 | The next window opens. W is abandoned (it stays unreviewed). |

- **Which week a check-in reviews** (`WeeklyCheckIn.ReviewedWeekOf`) is derived from the save time:
  - saved on or after this week's `opens` → it reviews this week;
  - saved before it (Mon–Fri) → it reviews last week, and counts as **late** (`IsLate`).

  The function is monotonic in time, so the latest check-in alone tells whether the current review
  week is done.
- **Mon–Fri with last week reviewed:** nothing is due. There is no chip and no gate. The CHK page
  hides the form and shows "Next check-in opens SAT OCT 17", plus history.
- **After submitting, within the window (Sat/Sun):** the status is Done. The form stays available
  as **REVISE CHECK-IN** until the deadline. A revision is saved but earns no points, and it Drops
  the earlier submission's still-open commitments, as before.
- **Snoozes:** 2 × 3h **per reviewed week**. `CheckInSnoozeState.WeekStart` is now the reviewed
  week's Monday. A snooze is only possible while gated (Closing or Overdue). The cap spans that
  week's Sunday evening and its overdue days.
  - Old snooze files hold a Sunday date, so they never match a Monday and read as "0 used".
- **Points:** only the **first check-in per reviewed week** earns `ReflectionSubmitted`. Later
  check-ins for the same week (revisions, or a late one after an on-time one) are saved without
  points.
- **CHK header** states the convention in one line: `Reviews MON OCT 5 – SUN OCT 11 · due SUN 23:59`.
  When the form is closed, it shows the next review week. History rows read
  `SAT OCT 10 12:54 · FOR MON OCT 5 – SUN OCT 11`, with `(LATE)` when late.

### Pure core (`LTFI.Core/Domain`)

- `CheckInSchedule` holds offsets from Monday 00:00: `Opens`, `GateFrom` and `Due` (the
  inclusive minute), plus `Deadline = Due + 1 min`, `MaxSnoozes` and `SnoozeHours`.
  - `Default` is Sat 00:00 / Sun 18:00 / Sun 23:59 / 2 / 3.
  - The constructor validates that opens ≤ gate ≤ due ≤ Sun 23:59.
- `CheckInPhase`: `Done | Open | Closing | Overdue`.
- `WeeklyCheckIn`:
  - `MondayOf`, `ReviewedWeekOf`, `IsLate`, `CommitmentWeekFor` (+7 days);
  - `Evaluate(last, now, schedule) → CheckInState`, which carries Phase, ReviewWeek, OpensAt,
    GateAt, DueAt, NextOpensAt, CanSubmit and IsRevision;
  - `IsDue`, `MustShow`, `Snooze`, `SnoozesUsed`, `SnoozesRemaining` and `IsSnoozed`, all of which
    now take the schedule.
- `WeeklyCommitments.WeekFor(checkInAt, schedule)` and `IsLegacyWeekStart`.
  - `WeekOf` and `WeeklyCheckIn.WeekStart` are gone.
  - `ProjectPolicy.WeeklyCheckInDay` is gone. The two snooze constants remain as defaults.

All week math uses the wall clock of the timestamp it is given. The services convert stored
`CreatedAt` values into the clock's local zone first (`TimeZoneInfo.ConvertTime(…, clock.LocalTimeZone)`),
so a row stored in UTC still maps by local day.

## Settings (`settings.json` → `checkIn`, camelCase)

```json
"checkIn": {
  "opensDay": "Saturday",  "opensTime": "00:00",
  "gateFromDay": "Sunday", "gateFromTime": "18:00",
  "dueDay": "Sunday",      "dueTime": "23:59",
  "maxSnoozesPerWeek": 2,  "snoozeHours": 3
}
```

- Day names are English and case-insensitive. Times are 24h `HH:mm`.
- If anything fails to parse, is out of order, or has snoozeHours < 1, the **whole** section falls
  back to the defaults (`CheckInSettings.ToSchedule()`).
- A settings file without a `checkIn` key gets the section written in with the defaults on the
  next start, through the normal serializer. That is the owner's current file.
- DI registers `CheckInSchedule` from settings. `ReflectionService` and `CommitmentService` take
  it as an optional constructor parameter, defaulting to `CheckInSchedule.Default`.
- The spec named the gate key `gateFrom`. It is split into `gateFromDay` + `gateFromTime` to
  match the other two pairs.

## Data: what is stored, what is derived, migration

- **Reviewed week: derived, not stored.** It is a pure function of `ReflectionEntry.CreatedAt`.
  There is no schema change, no EF migration, and `ScopeId`/`Prompt` are untouched. The trade-off:
  editing `opensDay`/`opensTime` re-interprets old check-ins. That is acceptable for a personal app
  and keeps one source of truth.
- **`WeeklyCommitment.WeekStart` now means "the Monday of the week this commitment applies to"**,
  which is the reviewed week + 7. New rows are written that way.
- **Legacy rows: data migration on read.** Old rows hold the old Sunday-start check-in week.
  - `CommitmentService.MigrateLegacyWeeksAsync` runs first inside `ReconcileAsync`, so on every
    commitments read and every check-in save.
  - Every row whose `WeekStart` is not a Monday is re-derived from its check-in's `CreatedAt`
    (`WeekFor`). The row's own `CreatedAt` is the fallback.
  - It is **idempotent**: new values are always Mondays and old ones were always Sundays (the old
    day was a constant), so a second run finds nothing.
  - It is SQL-free, because the mapping depends on the configured schedule and the local zone,
    which a static SQL migration can't know.
  - The cost is one `(Id, WeekStart)` projection of a tiny table per read.
- **The owner's data** (read-only copy of the live DB, 2026-10-11):
  - Check-ins `2026-10-04 18:40:19 -04:00` (Sun) and `2026-10-10 12:54:00 -04:00` (Sat; the brief
    said "evening", but the row says 12:54).
  - The first reviews **Sep 28 – Oct 4** and the second reviews **Oct 5 – 11**. Both are on time,
    so **nothing is due on Sun 2026-10-11**, even after 18:00.
  - Commitment rows: the 10-04 row (`nsdr, wake up earlier, sleep earlier.`, Kept) moves from
    `2026-10-04` to **2026-10-05** (it applied to Oct 5–11). The three 10-10 rows (Open,
    reminder-linked) move from `2026-10-04` to **2026-10-12**.
  - The snooze file (`WeekStart 2026-10-11`, 1 used) reads as 0 used.
  - Not repaired: under the old rule the 10-10 check-in earned **no** `ReflectionSubmitted` points,
    because it counted as a second check-in in the same Sunday-week. Under the new rule it would
    have earned 10. Evidence was not back-filled; that is a deliberate scope choice.

## Commitments

- The check-in for week W writes commitments that **apply to W+1**.
- **Saving** settles Open commitments:
  - those with `WeekStart ≤ W` (they applied to the reviewed week or earlier) → Kept if chosen,
    otherwise Missed;
  - those with `WeekStart > W` (made by an earlier check-in for the same W) → Dropped.
- **`GetPendingReviewAsync`** (the check-in's review panel) lists the commitments that applied to
  the review week (`WeekStart == ReviewWeek`), plus older ones that are still Open.
  - It is empty once that week is done and nothing is Open.
  - The panel header is now `COMMITMENTS FOR MON OCT 12 – SUN OCT 18` rather than "LAST WEEK'S
    COMMITMENTS". On a Saturday the reviewed week is the current one, so "last week" would mislead.
- **`GetCurrentWeekAsync`** returns the commitments that apply to the current Mon–Sun week.
- **Command Center — the panel switches.** It is the new `GetPanelAsync` → `CommitmentPanel(WeekStart, IsNextWeek, Lines)`.
  - Normally it shows `THIS WEEK`.
  - Once the current week's own check-in is in (Sat/Sun), it switches to that check-in's
    commitments, tagged `NEXT WEEK · FROM MON OCT 12`.
  - Why switch rather than show both: submitting that check-in settled every one of this week's
    commitments (Kept/Missed), so nothing on the "this week" list is actionable any more. Next
    week's list is what you'll work on, and checking one off early (or completing its linked
    reminder) keeps it.
  - On Monday the same set shows as `THIS WEEK`.
- **Backfill (v1 free-text Q5)** keeps working. For the reviewed weeks "last week" and "this
  week" (whose commitments apply to this week and next), the latest check-in with no rows gets
  its Q5 split into up to 3 commitments with the right Monday. Older check-ins are never
  back-filled. It is idempotent.

## UI

- **Header chip** (`MainWindow`):
  - it shows whenever a check-in is wanted (Open, Closing or Overdue): amber
    `CHECK-IN DUE SUN 23:59`, or red `CHECK-IN OVERDUE`;
  - clicking it opens CHK;
  - it is updated by the existing gate check: at launch, every 15s, and after a submit or snooze.
- **CHK page:**
  - The header has the convention line and a colour-coded status line (amber due, red overdue,
    green done).
  - When the form is closed, a "Next check-in opens …" panel replaces the review and questions.
  - SNOOZE shows only while gated, with the label from the configured hours.
  - Submit reads REVISE CHECK-IN during an in-window revision.
- **Command Center:** the WEEKLY COMMITMENTS header has a THIS WEEK / NEXT WEEK tag, and the
  empty-state text says where commitments come from.

## Verification

- `dotnet build LTFI.sln --no-incremental` (with a scratch `OutDir`, because another agent was
  building in the same tree at the same time): **0 errors, 0 warnings**, LTFI.App included.
- `dotnet test LTFI.sln`: **194 pass.** That count includes a parallel agent's tests.
- `WeeklyCheckInTests` covers:
  - Mon–Sun weeks and the defaults;
  - review-week mapping and lateness: Fri 23:59:59 → last week (late), Sat 00:00 → this week,
    Sun 23:59:59 on time, Mon 00:00 late for the ended week, the next Sat flips;
  - window boundaries: Fri 23:59 closed, Sat 00:00 open with no gate, Sun 17:59:59 still open,
    Sun 18:00 gate, Sun 23:59:59 Closing (not overdue), Mon 00:00 overdue, overdue through
    Fri 23:59:59, Sat abandons it; a late check-in clears it;
  - a fresh install: overdue midweek, open on Saturday;
  - the snooze cap per reviewed week: not while Done or Open; it spans Sunday evening into
    overdue Monday; it resets for the next week; legacy state reads as 0 used;
  - the owner's exact history: every 30 minutes across Sun Oct 11 → Done, no gate, revision
    allowed, next opens SAT OCT 17; Monday closed;
  - settings: defaults equal `CheckInSchedule.Default`; a custom Fri 17:00 / Sun 12:00 / Sun 20:00
    schedule; invalid day, numeric day, bad time, out-of-order and zero hours all fall back; the
    missing `checkIn` section is written and edits are read back;
  - a service round trip in a fixed −04:00 zone: Open → gate → snooze persisted → submit →
    revision with no points → Monday closed → skipped week overdue → late check-in earns that
    week's points and is flagged late → next window;
  - the owner's rows plus the old snooze file in the service at 10:00, 18:00, 20:30 and 23:59:59
    → Done;
  - a UTC-stored Friday-evening row maps by the local day.
- `CommitmentTests` (updated to the new model):
  - creation lands on next week, and the panel shows NEXT WEEK;
  - mid-week the same set is THIS WEEK;
  - Saturday's review lists exactly the set that applied;
  - settle and carry over, then the new set is next week's;
  - a revision and a Monday-late re-submit Drop the superseded rows;
  - backfill for this week's and next week's sets, idempotent, skipping the old one;
  - **the owner's legacy rows are re-derived** (Oct 4 → Oct 5, Oct 10 → Oct 12), the result is
    idempotent, the panel shows NEXT WEEK, nothing is due at Sun 19:00, and the Oct 17 review
    lists the Oct 10 commitments.
- I did not launch the app, per the brief.

## Deviations / open points

- `gateFrom` is split into `gateFromDay`/`gateFromTime`. `maxSnoozesPerWeek` and `snoozeHours`
  are configurable too.
- In-window revisions after submitting are allowed (REVISE CHECK-IN, no points). The brief only
  said the form is unavailable Mon–Fri.
- A fresh install with no check-ins is **overdue** Mon–Fri, so the gate comes up at once. It is
  snoozable twice, after which you must submit. This matches the old "new install is due right
  away".
- Points for the owner's 10-10 check-in were not back-filled (see Data).
- The service does not refuse a save when the form is closed. Only the UI hides it. A Mon–Fri save
  after an on-time check-in maps to the already-reviewed week, so it earns no points and supersedes
  that week's open next-week commitments.
