# Runbook: Alloggiati Web, honest status and manual submission

Task CO-11 (audit defects A5-01, A9-05, A5-03, A5-37, A5-35; decision D6). No external configuration is needed:
this page explains what the code does, what the migration changes and what hosts see. The web service client
(credentials per host, WSKEY, `Test`/`Send`/`Ricevuta`) is task CO-13; sources in
`.claude/context/regulations/alloggiati.md`.

## What changed

Until CO-11 every report was set to `Submitted` without transmitting anything (with `Alloggiati:Enabled` true or
false), the job ran as soon as the guest filled in the form (days before arrival) and ran again at the host check-in.
CasaZen still transmits nothing, so it now says so.

| State (`AlloggiatiWebStatus`, int) | Meaning | Set by |
|---|---|---|
| `DaInviare` (0) | Waiting for the arrival day: the portal accepts only today or yesterday as arrival date | Guest portal submit or host check-in |
| `DaInviareManualmente` (4) | Ready, CasaZen does not transmit: the host sends it on the Questura portal | Job at 00:00 Europe/Rome of the arrival day (also shown for any stay whose arrival day has come without a report) |
| `InviatoManualmente` (6) | The host declared the date he sent it on the portal. CasaZen holds no receipt | `POST /api/alloggiati/{id}/mark-sent-manually` |
| `Inviato` (2) | Transmitted, with the real receipt | CO-13 only. Database check `CK_AlloggiatiWebReports_SentRequiresReceipt` refuses it without `ConfirmationNumber` |
| `Rifiutato` (5), `Errore` (3) | Rejected by the portal, technical error | CO-13 |
| 1 (old `Submitted`) | Retired | Never |

- **Scheduling.** `AlloggiatiReportScheduler` schedules `AlloggiatiWebReportJob` with Hangfire `Schedule` at the start
  of the arrival day in Europe/Rome (22:00 or 23:00 UTC of the day before), or right away if it has already started.
- **Idempotency.** One report per booking and guest (unique index `IX_AlloggiatiWebReports_BookingId_GuestId`). The
  report stores `ScheduledJobId` and `ScheduledFor`; guest portal and host check-in call the same scheduler and the job
  is queued once. A job still pending more than 1 hour after `ScheduledFor` is treated as lost and scheduled again (the
  old one is deleted). If the check-in date moves later, the job reschedules itself for the new day.
- **Deadline.** `Bookings.ArrivedAt` is recorded at the host check-in. Deadline = arrival + 24 h, or + 6 h when the stay
  is at most one night (art. 109 TULPS: stays not longer than 24 hours; without check-in/out times the stricter term
  is applied). Without `ArrivedAt` the arrival is 00:00 Europe/Rome of the check-in date.
- **Guest session.** It stays `Completo` after the portal submit; `AlloggiatiInviato` is reserved for a real receipt.
- **Host flow.** Booking detail → tab Alloggiati: status, deadline, the per-guest summary in record order (copy
  buttons), then "Segna come inviato manualmente" with confirmation and date (check-in date … today in Europe/Rome).
- **Cockpit.** "Da inviare manualmente" (orange) counts every stay whose arrival day has come and that is neither sent
  nor declared sent; "Errori Alloggiati" counts `Errore`/`Rifiutato`.
- **Removed.** The `Alloggiati:Enabled` setting (it only changed the text of the simulation). `POST .../send` answers
  `422 alloggiati_transmission_unavailable`.

## Data migration `AlloggiatiHonestStatus`

EF migration `20260924012639_AlloggiatiHonestStatus`, applied with the others at startup. SQL in the migration class
(public constants, covered by `AlloggiatiHonestStatusMigrationPostgresTests`):

1. Duplicate reports of the same booking and guest: only the latest (`UpdatedAt`) is kept.
2. `Submitted` (1), `Confirmed` (2) and `Failed` (3) **without** a receipt reference → `DaInviareManualmente` (4), with
   `ErrorMessage` (e.g. `simulated`), `ReportedAt` and `ManuallyCompleted` cleared. A `Submitted` row **with** a receipt
   reference → `Inviato` (2). No row is expected to have one: nothing was ever transmitted.
3. Guest check-in sessions in `AlloggiatiInviato` (3) → `Completo` (2), unless their booking has a report with a receipt.
4. Then the unique index and the check constraint are created.

Down restores the schema; data best effort (4 → 0, 5 → 3, 6 → 1, empty `ReportedAt` ← `UpdatedAt`), duplicates are not
restored.

### Check after the deploy (Supabase SQL editor, schema of the environment)

```sql
-- No row may be "sent" without a receipt, none may keep the retired value 1.
SELECT "Status", count(*), count(*) FILTER (WHERE btrim(coalesce("ConfirmationNumber", '')) <> '') AS with_receipt
FROM "AlloggiatiWebReports" GROUP BY "Status" ORDER BY 1;

-- Sessions still marked "Alloggiati sent" (expected: 0 until CO-13).
SELECT count(*) FROM "GuestCheckInSessions" WHERE "Status" = 3;
```

## What to tell hosts

Every stay whose communication CasaZen marked "Inviato" before this release was **not** transmitted to the Questura:
it now shows "Da inviare manualmente". If the host sent it on the Alloggiati Web portal, he records it with "Segna come
inviato manualmente" and the date; otherwise the communication is still due (art. 109 TULPS). The portal accepts only
today or yesterday as arrival date, so late communications must be handled with the Questura.
