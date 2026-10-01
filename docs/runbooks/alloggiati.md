# Runbook: Alloggiati Web, honest status and manual submission

Task CO-11 (audit defects A5-01, A9-05, A5-03, A5-37, A5-35; decision D6), CO-12 (A5-02: guests of the stay and
official code tables, sections at the end), CO-09 (A5-26, A5-27: check-in link and host fallback, see
"Guest check-in link and host fallback") and CO-21 (OTA stays from iCal blocks, see "Reservations of the OTAs linked by
iCal"). CO-11 needs no external configuration; CO-12 needs an admin to import the
official code tables (see "Tabelle codici Alloggiati"). This page explains what the code does, what the migrations
change and what hosts see. CO-13 added the **record file** the host uploads on the portal (see "Record file (CO-13)" at the
end); the web service client (credentials per host, WSKEY, `Test`/`Send`/`Ricevuta`) is **not implemented**, with the reasons
in the same section. Sources in `.claude/context/regulations/alloggiati.md`.

## What changed

Until CO-11 every report was set to `Submitted` without transmitting anything (with `Alloggiati:Enabled` true or
false), the job ran as soon as the guest filled in the form (days before arrival) and ran again at the host check-in.
CasaZen still transmits nothing, so it now says so.

| State (`AlloggiatiWebStatus`, int) | Meaning | Set by |
|---|---|---|
| `DaInviare` (0) | Waiting for the arrival day: the portal accepts only today or yesterday as arrival date | Guest portal submit or host check-in |
| `DaInviareManualmente` (4) | Ready, CasaZen does not transmit: the host sends it on the Questura portal | Job at 00:00 Europe/Rome of the arrival day (also shown for any stay whose arrival day has come without a report) |
| `InviatoManualmente` (6) | The host declared the date he sent it on the portal. CasaZen holds no receipt | `POST /api/alloggiati/{id}/mark-sent-manually` |
| `Inviato` (2) | Transmitted, with the real receipt | the future web service client only (not implemented). Database check `CK_AlloggiatiWebReports_SentRequiresReceipt` refuses it without `ConfirmationNumber` |
| `Rifiutato` (5), `Errore` (3) | Rejected by the portal, technical error | the future web service client only |
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
  buttons, one card per guest of the stay since CO-12), then "Segna come inviato manualmente" with confirmation and date
  (check-in date … today in Europe/Rome).
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

-- Sessions still marked "Alloggiati sent" (expected: 0 until the web service client exists).
SELECT count(*) FROM "GuestCheckInSessions" WHERE "Status" = 3;
```

## What to tell hosts

Every stay whose communication CasaZen marked "Inviato" before this release was **not** transmitted to the Questura:
it now shows "Da inviare manualmente". If the host sent it on the Alloggiati Web portal, he records it with "Segna come
inviato manualmente" and the date; otherwise the communication is still due (art. 109 TULPS). The portal accepts only
today or yesterday as arrival date, so late communications must be handled with the Questura.

## Guests of the stay (CO-12)

Task CO-12 (audit defect A5-02). Alloggiati Web needs one line (schedina) per person staying, minors included
(`.claude/context/regulations/alloggiati.md`, RS-1). Until CO-12 CasaZen held one guest per booking, with free-text
place of birth and citizenship and no place of issue of the document.

| Kind (`StayGuestType`, int) | Official category | Document (type, number, place of issue) |
|---|---|---|
| `SingleGuest` (1) | Ospite singolo | required |
| `HeadOfFamily` (2) | Capo famiglia | required, followed by its family members |
| `HeadOfGroup` (3) | Capo gruppo | required, followed by its group members |
| `FamilyMember` (4) | Familiare | not part of the line |
| `GroupMember` (5) | Membro gruppo | not part of the line |

The integers are CasaZen's own: the official numeric codes are **not** verified (RS-1) and come only from the imported
"Tipi alloggiato" table, matched by the official category name.

- **Table `StayGuests`** (tenant data, `OrgId` of the booking, TN-2 filter): one row per guest, ordered by `Position`
  (unique per booking). Row 0 stays linked to the booker (`GuestId`); its data is also copied on the booker's `Guests`
  row, as before, so the guest views keep working. Born in Italy: comune and province (two-letter car plate code);
  abroad: state. Citizenship, places and document type keep the entered name plus, when known, the official code.
- **Checks** (`AlloggiatiRecordRules`, `StayGuestService`): a family member follows its head of family, a group member
  its head of group, a head has at least one member; sex male/female only (`Gender.Other` is rejected, never mapped);
  document kind Passport/IdentityCard/DriversLicense or an official document code (`GuestDocumentType.Other` is
  rejected); surname ≤ 50, name ≤ 30, document number letters and digits ≤ 20 (lengths of the record).
- **Guest portal** (`/checkin/{token}`): the guest enters every person (the form starts with the guests declared on the
  booking), chooses single guests / family / group, and gives the document only for the first one (or for each single
  guest). Document numbers on file are still shown masked (`*****` + last 3, CO-02).
- **Host** (booking detail → tab Alloggiati): one card per guest in record order with its completeness ("Completo",
  "N dati mancanti", "Codici da completare"), minors flagged, the codes found and the button "Modifica ospiti"
  (`PUT /api/alloggiati/{bookingId}/stay-guests`, `booking.write`) for walk-in guests, corrections and codes. Since
  CO-09 the document number is masked here too, with "Mostra" (see "Guest check-in link and host fallback").
- **Record export**: the summary reports `dataComplete` and `exportReady`. A missing code blocks only `exportReady`
  (the record file, CO-13: `GET /api/alloggiati/{bookingId}/record-file`), never the data entry nor the manual submission on
  the portal. Positions and rules of the file are verified: see "Record file (CO-13)".

### Migration `AddStayGuestsAndAlloggiatiCodeTables`

Creates `StayGuests`, `AlloggiatiCodeEntries`, `AlloggiatiCodeTableImports` (empty) and gives every existing booking one
guest from its booker (SQL `BackfillStayGuestsSql`, idempotent, covered by `AddStayGuestsMigrationPostgresTests`):
`SingleGuest` when `NumberOfGuests` ≤ 1, otherwise `HeadOfFamily` (the host or the guest adds the members). Names, date
of birth, citizenship, document number and issuing country are copied as text; the old free-text place of birth goes
to `BirthComuneName` with "born in Italy" unknown, so it shows as a field to complete; sex "Other" and document kind
"Other" become empty (missing). Down drops the three tables.

```sql
-- Every booking has at least one guest of the stay (expected: 0).
SELECT count(*) FROM "Bookings" b WHERE NOT EXISTS (SELECT 1 FROM "StayGuests" s WHERE s."BookingId" = b."Id");

-- Guests of another org than their booking (expected: 0).
SELECT count(*) FROM "StayGuests" s JOIN "Bookings" b ON b."Id" = s."BookingId" WHERE s."OrgId" <> b."OrgId";
```

## Tabelle codici Alloggiati (CO-12)

Comuni, stati, tipi documento and tipi alloggiato are **official data of the Alloggiati portal**: CasaZen ships no
code (RS-1: never invented, never committed). Until an admin imports them the forms ask for names only and every code
shows "codice da completare". After an import:

- the forms suggest the official entries (guest portal `GET /api/public/checkin/{token}/codes`, host
  `GET /api/alloggiati/codes?list=comuni|stati|documenti|luoghi&q=`) and store the chosen code;
- a code already stored must exist in the table (otherwise 400 `CheckInCodeUnknown`);
- codes of guests entered by name are found at read time by a **unique** match of the name against the official
  description (accents, case and punctuation ignored; for comuni also the province). Ambiguous or unknown names stay
  "da completare": the host picks the entry with "Modifica ospiti";
- the kind of guest is found by the official category names (Ospite singolo, Capo famiglia, Capo gruppo, Familiare,
  Membro gruppo) and the state of birth of those born in Italy by the description "Italia" of the stati table. If the
  official descriptions differ, those codes stay to complete: report it (DUBBI of CO-12).

### Download (admin, from a network that reaches the portal)

Area Download Tabelle: https://alloggiatiweb.poliziadistato.it/portalealloggiati/tabelle.aspx (RS-1; the links were
blocked by the proxy of the development environment, so the file format is not verified):

| Table | Link | Import name |
|---|---|---|
| Comuni | `.../portalealloggiati/ashx/Download.ashx?ID=0&N=COMUNI` | `Comuni` |
| Stati | `.../portalealloggiati/ashx/Download.ashx?ID=1&N=STATI` | `Stati` |
| Tipi documento | `.../portalealloggiati/ashx/Download.ashx?ID=2&N=DOCUMENTI` | `Documenti` |
| Tipi alloggiato | `.../portalealloggiati/ashx/Download.ashx?ID=3&N=TIPO_ALLOGGIATO` | `TipiAlloggiato` |

The same tables come from the web service method `Tabella` (CSV with `;` separator, needs a host's credentials and a web
service client, which does not exist: see "Record file (CO-13)").

### File format accepted

- Text file (`.csv` or `.txt`), at most 10 MB, **UTF-8** (with or without BOM) or **Windows-1252**.
- First non-empty line: header. Separator detected from it: `;`, tab, `|` or `,`. Cells may be enclosed in `"`.
- Required columns (name compared ignoring case, accents, spaces and punctuation): **`Codice`** (or `Code`) and
  **`Descrizione`** (or `Description`). Comuni only, optional: **`Provincia`** (or `Province`, `SiglaProvincia`),
  two letters. Other columns are ignored.
- If the official file uses other column names, rename the header line (only the header) before the upload; do not
  change the rows.
- Checks per row: code present, uppercase letters and digits, at most the length of the record field (9 comuni and
  stati, 5 documenti, 2 tipi alloggiato); description present (≤ 200); province two letters; no duplicate code.
- **All or nothing**: one invalid row rejects the file (422 `alloggiati_code_import_invalid`, `lines: [{ line, error }]`,
  error codes `code_missing`, `code_invalid`, `description_missing`, `description_too_long`, `province_invalid`,
  `duplicate_code`, `code_column_missing`, `description_column_missing`, `no_rows`, `unreadable_encoding`,
  `file_too_large`, `empty_file`). A valid file **replaces** the whole table and is logged in
  `AlloggiatiCodeTableImports` (file name, version, SHA-256, rows, admin, date).

### Import (admin JWT, role `Admin`)

```bash
API=https://<railway-url>   # test first, then production
curl -sS -X POST "$API/api/admin/alloggiati/code-tables/Comuni" \
  -H "Authorization: Bearer $ADMIN_JWT" \
  -F "file=@COMUNI.csv" -F "sourceVersion=portale Alloggiati, scaricato il 2026-10-01"
# repeat for Stati, Documenti, TipiAlloggiato
curl -sS "$API/api/admin/alloggiati/code-tables" -H "Authorization: Bearer $ADMIN_JWT"   # rows and last import per table
```

Refresh the tables periodically (comuni change with mergers and suppressions) and whenever the portal publishes a new
version. Check after an import:

```sql
SELECT "Table", count(*) FROM "AlloggiatiCodeEntries" GROUP BY 1 ORDER BY 1;   -- 1 Comuni, 2 Stati, 3 Documenti, 4 TipiAlloggiato
SELECT "Table", "SourceVersion", "RowCount", "ImportedAt" FROM "AlloggiatiCodeTableImports" ORDER BY "ImportedAt" DESC;
-- The five kinds of guest and Italy must be found by name (expected: 5 and 1 rows).
SELECT "Code", "Description" FROM "AlloggiatiCodeEntries" WHERE "Table" = 4;
SELECT "Code", "Description" FROM "AlloggiatiCodeEntries" WHERE "Table" = 2 AND "NormalizedDescription" = 'ITALIA';
```

## Guest check-in link and host fallback (CO-09)

Task CO-09 (audit defects A5-26, A5-27). No external configuration beyond the optional `CheckIn` settings below.
Before CO-09 a check-in link whose email failed was expired at once and disappeared (no "send reminder" button), the
web app said "Link reinviato" even when nothing left, the host had no way to enter the guest data himself, and a link
expired by time was never marked expired, so the daily job never sent a new one.

### Settings (Railway, optional)

| Variable | Default | Meaning |
|---|---|---|
| `CheckIn__SessionLifetimeDays` | 7 | Validity of a check-in link, 1–60 days (values outside are brought within). The email says until when. |
| `CheckIn__SendWindowDays` | 3 | The daily job emails the link to confirmed stays starting within this many days (and to checked-in stays not yet checked out). |

### The link, host side (booking detail → tab Ospite, "Stato check-in ospite")

- `GET /api/bookings/{id}/checkin-session` (`booking.read`): the current link (the completed one if the guest has
  submitted, otherwise the latest), its status (an open link past its expiry reads `Scaduto` even before the job marks
  it), issue and expiry dates, the email state and `canIssueLink` (booking confirmed or checked in, check-in not
  completed).
- `POST /api/bookings/{id}/checkin/resend-link` (`booking.write`): new link emailed to the guest; it is also the
  reminder. `POST /api/bookings/{id}/checkin/link`: new link to copy, no email. Both answer `{ checkInLink, expiresAt,
  emailStatus, emailError }` **whatever happens to the email**: the host copies the link and sends it another way
  (WhatsApp, SMS) when the address is wrong. A new link expires the previous one (only the SHA-256 of a token is
  stored, so an old link cannot be shown again). 409 `checkin_link_booking_not_eligible` (booking not confirmed or
  checked in), 409 `checkin_already_completed` (the host corrects the data in the Alloggiati tab). Another org's
  booking: 404.
- **Email state** (`GuestCheckInSessions.LinkEmailStatus`, int): `NotRequested` (0, link to copy), `Queued` (1),
  `Sent` (2, handed to the provider, `SentAt`), `Failed` (3) with `LinkEmailError`: `no_recipient`,
  `provider_not_configured`, `queue_failed`, `rejected` (refused by the provider, e.g. invalid address, not retried),
  `not_delivered` (temporary errors on the 4 attempts: now, +1 min, +10 min, +1 h), `link_unavailable`
  (`App__PublicSiteBaseUrl` missing), `link_not_usable` (the link was replaced or expired before the email left: nothing
  sent). Null for links issued before CO-09. The web app polls while the email is queued and never says "sent" for
  anything but `Sent`.
- The email is sent by `GuestCheckInLinkEmailJob` (Hangfire, queued by the host endpoint or the daily job): it is built
  when it is sent, with the current guest address, and only while the link is still usable. The job carries the session
  id and the raw token; a job that fails for other reasons (database) is retried 3 times and then deleted.

### Daily job `guest-checkin-send` (08:00 UTC)

1. Marks `Scaduto` every open link (`Inviato`, `InCompilazione`) whose `ExpiresAt` has passed (A5-27).
2. Skips a stay with a usable link (open and not expired, **whatever happened to its email**: the host sees the
   failed email and resends it or copies the link; no daily resend to a wrong address), a completed check-in, a
   communication `Inviato`/`InviatoManualmente`, or **guest data already complete** (entered by the host).
3. Otherwise issues a new link and queues its email: a stay whose link expired gets a new one while it is still relevant
   (confirmed and starting within the window, or checked in and not yet checked out).

### Host entry of the guest data

- "Modifica ospiti" (`PUT /api/alloggiati/{bookingId}/stay-guests`, `booking.write`) is the fallback when the guest
  cannot use the link: every guest of the stay (identity, birth, citizenship, kind of guest; the document for a single
  guest or a head of family or group), with **the same validation as the guest portal** (`StayGuestService`: same field
  errors and messages, official codes when the tables are imported, otherwise names with "codice da completare").
- **Audit**: every row records who entered it last, `StayGuests.DataSource` (0 `NotRecorded`: copied from the booker or
  entered before CO-09, 1 `GuestPortal`, 2 `Host`) and, for the host, `EnteredByUserId` (Auth0 `sub`, never returned).
  The summary shows "Inseriti dall'ospite/dall'host il …". The API logs each host entry with user and booking id.
- Complete data schedules the communication for the arrival day, as the guest portal does (idempotent), and stops the
  daily link emails.
- **Document number**: the summary returns it masked (`documentNumberMasked`, `*****` + last 3, as on the guest portal).
  "Mostra" (and "Copia tutto", and opening "Modifica ospiti") call
  `GET /api/alloggiati/{bookingId}/stay-guests/document-numbers[?position=n]` (`booking.read` on the booking and
  `guest.read`), logged with user, booking and positions, never the numbers. A host without `guest.read` re-enters the
  number in the form, like the guest.

### Guest document scans (FD-07 open point)

`GET /api/guests/{id}/document-scan` (`guest.read` on the guest's org, TN-3): the scan uploaded by the guest (legacy
portal, `Guest.DocumentScanUrl`) from the **private** bucket, `Cache-Control: private, no-store`, download logged with
user and guest. Another org's guest: 404; no scan, a legacy `/uploads/...` path not migrated, or file missing from the
storage: 404 `guest_document_scan_missing`. Web app: guest detail → Documenti → "Scarica scansione documento". The scans
are not encrypted yet (CO-14).

### Migration `AddCheckInLinkEmailStatusAndStayGuestSource`

Adds `GuestCheckInSessions.LinkEmailStatus` (int, null = unknown for older links) and `LinkEmailError`, and
`StayGuests.DataSource` (int, 0 for every existing row: not recorded) and `EnteredByUserId`. No data is rewritten.

```sql
-- Links still open past their expiry (expected: 0 after the 08:00 UTC run)
SELECT count(*) FROM "GuestCheckInSessions" WHERE "Status" IN (0, 1) AND "ExpiresAt" < now();

-- Link emails by outcome in the last 7 days (3 = failed: see LinkEmailError)
SELECT "LinkEmailStatus", "LinkEmailError", count(*) FROM "GuestCheckInSessions"
WHERE "CreatedAt" > now() - interval '7 days' GROUP BY 1, 2 ORDER BY 1, 2;

-- Guests entered by hosts
SELECT count(*) FROM "StayGuests" WHERE "DataSource" = 2;
```

## Host alerts (CO-10)

Task CO-10 (audit defects A5-11, A6-07, A5-25). The host is alerted about a communication not sent **once per stage**,
by the hourly `stay-alerts` job (job, deduplication and first deploy: [hangfire.md §9](hangfire.md#9-stay-alerts-co-10)).
Before CO-10 two jobs sent the same "Check-in incompleto" email and push every hour for every such stay, a failed report
included, up to 8 days after arrival.

**Terms** (`.claude/context/regulations/alloggiati.md`, CO-11 `AlloggiatiTerms`): within 24 hours of arrival, 6 hours for
stays of at most one night; the portal accepts the communication only from the arrival day (Europe/Rome).

**Alert deadline.** With the arrival registered (`Bookings.ArrivedAt`, host check-in) it is the legal deadline
(arrival + 24 h / + 6 h). Without it, it is the **end of the arrival day** (00:00 Europe/Rome of the next day): for a stay
of more than one night that is the deadline CasaZen already shows; for a short stay the deadline shown (06:00 of the
arrival day, CO-11's conservative value) comes before any realistic arrival, and an "overdue" alert at that time would
almost always be false. The booking page keeps showing the conservative deadline.

**Stages** (stay confirmed, checked in or checked out; communication neither `Inviato` nor `InviatoManualmente`):

| # | Stage | When (first hourly run at or after) | Condition | Email template / push type |
|---|---|---|---|---|
| 1 | Guest data missing | Day before arrival, 10:00 Europe/Rome (`StayAlerts:GuestDataReminderHourLocal`) | Guest data of the stay incomplete (CO-12) and report not failed | `guest-checkin-incomplete` "Dati ospiti mancanti" / `guest-data-missing` |
| 2 | Deadline approaching | Alert deadline − 12 h (`StayAlerts:DeadlineWarningHours`), never before 00:00 of the arrival day | — | `alloggiati-deadline` "Alloggiati Web in scadenza": the term (24 h or 6 h), the exact deadline when the arrival is registered / `alloggiati-deadline` |
| 3 | Overdue | Alert deadline | — | `alloggiati-overdue` "Alloggiati Web scaduta" / `alloggiati-overdue` |
| 3+n | Daily reminder n (at most `StayAlerts:MaxOverdueReminders`, default 2) | 09:00 Europe/Rome (`StayAlerts:OverdueReminderHourLocal`) of the n-th day after the alert deadline | Dropped a day after the last one | same as 3, with "Promemoria n di N" |
| — | Failed communication | First run with the report `Errore` or `Rifiutato` (CO-13) | Once per check-in date; replaces stage 1 | `alloggiati-failed` "Invio Alloggiati Web non riuscito" / `alloggiati-failed` |

Only the most advanced stage due is sent (a stay created or first seen late gets that stage only); stages never go back.
Maximum per stay with the defaults: **5** messages (3 + `MaxOverdueReminders`), plus 1 "invio fallito" if the portal
rejects it, each one email to the org contact address plus one push to the property's hosts. Declaring the communication
sent ("Segna come inviato manualmente") or cancelling the stay stops the sequence at the next run. Moving the check-in
date starts it again for the new date.

Example, stay of 3 nights, check-in Monday 5 October, arrival not registered, guest data incomplete:
Sunday 4 at 10:00 "Dati ospiti mancanti"; Monday 5 at 12:00 "in scadenza" (24 hours from arrival); Tuesday 6 at 00:00
"scaduta"; Wednesday 7 and Thursday 8 at 09:00 reminders 1 and 2; nothing afterwards. With complete data the first
message is skipped; with the arrival registered on Monday at 15:00 the overdue alert comes on Tuesday at 15:00.

The texts say what to do: complete the guest data (link to resend to the guest), send the communication on the portal,
then record it with "Segna come inviato manualmente". Texts: `Casazen.Infrastructure/Email/Templates/EmailTexts.resx`
(IT) and `.en.resx`; the emails go out in Italian (no language preference yet).

**The check-out reminder** is part of the same job: every confirmed or checked-in stay, at 20:00 of its check-out day in
the property's time zone (Europe/Rome when none), email and push (A5-25; it was push only and only after a host check-in).

```sql
-- Stays alerted today and their stage (type 1 Alloggiati deadline, 2 failed communication, 3 check-out reminder)
SELECT s."BookingId", s."Type", s."Stage", s."AlertCount", s."LastAlertAt", b."CheckInDate", b."Status"
FROM casazen_prod."StayAlertStates" s JOIN casazen_prod."Bookings" b ON b."Id" = s."BookingId"
WHERE s."LastAlertAt" >= date_trunc('day', now())
ORDER BY s."LastAlertAt";

-- Never more than 3 + StayAlerts__MaxOverdueReminders Alloggiati messages for a check-in date (expected: 0 rows)
SELECT "BookingId", "AlertCount" FROM casazen_prod."StayAlertStates" WHERE "Type" = 1 AND "AlertCount" > 5;
```

The second query can list a stay whose check-in date was moved after some alerts (the sequence restarts and the count
keeps the earlier messages).

## Reservations of the OTAs linked by iCal (CO-21)

Task CO-21 (audit GC-AC9, decision D7). The communication to the Questura is due for every guest, whatever the channel of
the reservation (art. 109 TULPS; `.claude/context/regulations/alloggiati.md`). A reservation received on Airbnb or
Booking.com and imported by iCal is only a block of dates: until CO-21 it had no guest, so none of the steps above
started. Now the host turns the block into an **OTA stay** (calendar → block → "Crea soggiorno OTA", name and email of the
guest; API and rules in [ical.md](ical.md#ota-stays-from-ical-blocks-co-21)).

- The stay is a confirmed booking with the OTA source (`Airbnb`, `BookingCom`, ...): the check-in link (tab Ospite,
  "Copia link" / "Invia link", and the daily job within the send window), the guest portal, "Modifica ospiti", the
  scheduling on the arrival day, "Segna come inviato manualmente", the cockpit sections and the CO-10 alerts work exactly
  as for a direct or manual booking. No separate flow.
- Guests of the stay: at the start only the booker (name and email, as entered by the host), `SingleGuest` when the host
  gives 1 guest or none, `HeadOfFamily` otherwise (CO-12 rule). The guest completes identity and document through the
  link, or the host with "Modifica ospiti".
- A stay "da verificare" (reservation gone from the channel's calendar, or other dates) keeps its Alloggiati status: if the
  reservation was cancelled before the arrival the host cancels the stay (nothing is to send); if the dates changed the
  host applies the channel's dates ("Applica le date del canale"): status, deadline, cockpit and CO-10 alerts follow the
  new check-in date (a report job already scheduled for an earlier day reschedules itself, see "Scheduling" above).
- A stay already over cannot be created from a block (422 `ota_stay_block_ended`): a late communication is handled with
  the Questura as explained above.

## Record file (CO-13)

Task CO-13 (decision D6), second attempt on 2026-10-01. **Done: the record file** the host uploads on the portal.
**Not done: the web service client** (credentials per host, `Test`/`Send`/`Ricevuta`), see "Web service: not implemented"
below. CasaZen still transmits nothing: no status reads "Inviato" (database check
`CK_AlloggiatiWebReports_SentRequiresReceipt`), and downloading the file changes no status.

### What the host sees

Booking detail → tab Alloggiati → box "File per il portale Alloggiati Web" (users with `guest.read`):

1. The button "Scarica il file" is enabled when `exportReady` is true (data complete, every official code found, see
   "Guests of the stay") and the stay does not exceed 30 days. Otherwise it is disabled with the reason.
2. The host uploads the `.txt` on the portal: menu **File** → "Seleziona" → "Elabora" (the portal shows how many lines are
   correct) → "Prosegui" (`MANUALEALBERGHI.pdf` p. 16-18). A host with the profile "Gestione Appartamenti" picks the
   apartment on the portal first (p. 24).
3. Then "Segna come inviato manualmente" (CO-11): CasaZen records the declaration, it holds no receipt.

The portal accepts only schedine whose arrival date is **today or yesterday** (`MANUALEALBERGHI.pdf` p. 7, 31): a file
downloaded before the arrival day, or after the day following it, is rejected by the portal (the UI says so; the file is
still produced, it is not a CasaZen rule).

### The endpoint

`GET /api/alloggiati/{bookingId}/record-file` (`AlloggiatiController.DownloadRecordFile`, service
`IAlloggiatiWebService.BuildRecordFileAsync`, builder `Casazen.Core/Regulatory/AlloggiatiRecordFile.cs`):

- **Access**: `guest.read` (the file holds the identity documents, like `stay-guests/document-numbers`) **and** `booking.read`
  on the booking (TN-3). A booking of another org answers 404, a user without the permission 403, no token 401.
- **Private**: built on every request, **never stored** (no bucket, no table), `Cache-Control: private, no-store`,
  `Content-Disposition: attachment; filename=alloggiati-<arrival yyyy-MM-dd>-<first 8 hex of the booking id>.txt`
  (no personal data in the name), `text/plain; charset=utf-8`.
- **Logs**: user id, booking id and the number of lines on success; on refusal only the kinds and positions of the issues
  (`DataIncomplete@1:dateOfBirth`). Never names, document numbers or codes of guests.
- **Content**: one line of 168 characters per guest of the stay in record order (a head of family or group, then its members),
  CR+LF between lines and none after the last, UTF-8 **without** BOM. All output is ASCII (codes of the official tables, names
  as below), so characters and bytes agree.
- **Refusals (422, `DomainRuleException`, localized IT/EN)**: `alloggiati_file_not_ready` (data incomplete or an official code
  to complete or no code table imported), `alloggiati_file_stay_days_invalid` (stay of less than 1 or more than 30 days; for
  longer stays the portal wants a new schedina as a new arrival, which CasaZen does not build), `alloggiati_file_name_not_representable`
  (a name that cannot be written with A-Z, e.g. Cyrillic or Chinese: the host writes the Latin transcription of the document).
  Nothing is cut, guessed or left blank to make a line fit.

### How a line is built

| Pos. (0-based) | Field | Content in CasaZen |
|---|---|---|
| 0-1 | Tipo alloggiato | code of the kind of guest from the imported table `TipiAlloggiato` (matched by name, CO-12) |
| 2-11 | Data arrivo | check-in date, `dd/MM/yyyy` |
| 12-13 | Giorni di permanenza | nights, 2 digits with leading zero (`03`), 1 to 30 |
| 14-63 | Cognome | capitals A-Z, space, apostrophe; padded to 50 |
| 64-93 | Nome | same, padded to 30 |
| 94 | Sesso | `1` male, `2` female |
| 95-104 | Data di nascita | `dd/MM/yyyy` |
| 105-113 | Comune di nascita | code from `Comuni`; **9 spaces if born abroad** |
| 114-115 | Provincia di nascita | province entered by the guest (two letters, `RM` for Rome); **2 spaces if born abroad** |
| 116-124 | Stato di nascita | code from `Stati` (the code of Italy for those born in Italy) |
| 125-133 | Cittadinanza | code from `Stati` |
| 134-138 | Tipo documento | code from `Documenti` |
| 139-158 | Numero documento | normalized (no spaces, capitals), padded to 20 |
| 159-167 | Luogo di rilascio | comune code (issued in Italy) or state code (issued abroad) |

Single guest, head of family and head of group carry fields 134-167; **family and group members have 34 spaces** there.
Names: accents removed (`José` → `JOSE`), `ß` → `SS`, `Æ` → `AE` and similar, hyphens → space, `.` and `,` dropped,
apostrophes kept; any other character makes the file refuse (`alloggiati_file_name_not_representable`).

### Sources read on 2026-10-01 (all copies: the portal is blocked by the proxy)

`alloggiatiweb.poliziadistato.it`, `questure.poliziadistato.it`, `regione.piemonte.it`, `hoteldruid.com`, `wiisy.app`,
`vertoai.it`, `gestione-affitti-brevi-puglia.com`, `office-online.it`, `web.archive.org`, `community.withairbnb.com`,
`bedzzle.freshdesk.com`, `checkinfacile.com`, `gotocheck.pro`, `persefoneticket.zendesk.com` and `interno.gov.it` answered
"egress blocked" (WebFetch `EGRESS_BLOCKED`, curl 403 on CONNECT); the proxy was not touched. Reachable: `github.com` (pages,
through WebFetch), `raw.githubusercontent.com`, `gitlab.com` (API), the npm, PyPI, crates.io, Packagist, RubyGems and Maven
registries. Search engine results (WebSearch) only showed titles and extracts.

| # | Source (URL) | Entity / author | Date | Integral copy of the official document? | Used for |
|---|---|---|---|---|---|
| 1 | `https://raw.githubusercontent.com/Giginoparrucca/doorstep/main/docs/MANUALEALBERGHI.pdf` (936,621 bytes, SHA-256 `28857705e0c419a3911317f943ac4c1782893f6503a8c9d6484a5018b31462c6`) | Polizia di Stato, "Servizio di Invio Telematico delle Schedine Alloggiati – Guida Servizio Alloggiati Web", 35 pages with index (sections 1-12), PDF metadata: Word 365, author "CERRELLI Luca", created 2022-01-27. Copy in a third-party repository (PR #76 of `Giginoparrucca/doorstep`) | file 2022-01-27 | **Yes**, complete (portal guide; the official URL is `.../portalealloggiati/Download/Manuali/MANUALEALBERGHI.pdf`). Not compared byte for byte with the portal | section 12 and the tables at p. 34-35: layout, UTF-8, 1000 lines, order, blanks, CR+LF; p. 7, 31 arrival window; p. 19 receipts; p. 24-25 apartments |
| 2 | `https://raw.githubusercontent.com/SergioArc69/invio_schedine-alloggiatiweb/master/ManualeWS.pdf` and `.../Giginoparrucca/doorstep/main/docs/MANUALEWS.pdf` (both 595,167 bytes, identical, SHA-256 `72d6c612ef616453e417f15e723b8fe44fd271cffab38671ec40823d568763a1`) | Polizia di Stato, Centro Elettronico Nazionale, "WS_ALLOGGIATI Documento di Descrizione", **Rev. 01, 24/01/2022**, 21 pages, PDF created 2022-01-24 | 2022-01-24 | **Yes**, complete. The Questura copy RS-1 saw in a search extract says "Rev. 01 13/01/2022": the difference is unexplained | web service: 12 methods with request/response examples, objects, error examples; record tables p. 19-20 |
| 3 | `.../SergioArc69/invio_schedine-alloggiatiweb/master/Connected%20Services/AlloggiatiWeb/service.wsdl` (28,116 bytes, SHA-256 `4d3b9522893870245ed260133b53a7ceba2c08f91e8baf62b713a7bee36e9f6e`) and `.../Giginoparrucca/doorstep/main/docs/alloggiati.wsdl` (28,736 bytes, SHA-256 `36d5279a0577f0b116ac74f6664246532685bc0178b1d6c6a1f7926c11235cab`, "pulled from the live endpoint" per PR #75) | the service's own WSDL (ASP.NET ASMX), fetched by two developers at different times | 2022 / 2026 | Machine-generated by the official service; the two copies are semantically identical (only attribute order and the case of `Service.asmx` differ) | namespace `AlloggiatiService`, element names and types, SOAP 1.1 and 1.2 bindings |
| 4 | `.../cito09/alloggiati-web-app` (`api/_alloggiati.js`, `public/index.html`), no licence stated | third-party app that sends to the real portal | 2026 | No (implementation, T) | independent check of positions, padding, 34 spaces, order of family lines, SOAP envelope, **the A-Z-only rule for names** |
| 5 | `.../SergioArc69/invio_schedine-alloggiatiweb` (`Models/RecordSchedina.cs`), GPL-3.0 | third-party Windows client | n.d. | No (implementation, T). **Code not copied** (licence) | independent check of the 168 characters and of the field comments |
| 6 | `.../Giginoparrucca/doorstep` PRs #75, #76, #89, #92, #93 (descriptions only) | third-party app | 2026-09 | No (T) | token flow, 48-hour window, per-row errors (`ErroreCod=222 "Cittadinanza non valida"`) |
| — | `MANUALEPASSAGGIO.pdf` (same repo, 4 pages) | Polizia di Stato | 2022-01-13 | Yes, but irrelevant: migration from the digital certificate to the device codes | none |
| — | `CREAFILE.pdf` | Polizia di Stato | — | **not obtained** (only the extracts of RS-1); its content is covered by section 12 of source 1 | — |
| — | `HyperTesto/schedine-alloggiatiweb` (Java generator + SQLite dump of the tables), `connectis/ricestat` (XML protocol for ISTAT and tourist tax, not the Questura record), GitLab `cix99/alloggiati.cloud` (2018) | third parties | — | No | not read in depth: old or off topic |
| — | npm, PyPI, crates.io, Packagist, RubyGems | — | — | — | no package for Alloggiati exists |

I did not commit the PDFs: their redistribution licence is not stated in the copies and the official portal could not be
reached to check it. **To confirm that sources 1 and 2 are byte-identical to the portal's** (then they are the official
documents themselves), from a network that reaches the portal:

```bash
curl -sSLO https://alloggiatiweb.poliziadistato.it/portalealloggiati/Download/Manuali/MANUALEALBERGHI.pdf
curl -sSLO https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Download/Manuali/MANUALEWS.pdf
sha256sum MANUALEALBERGHI.pdf MANUALEWS.pdf   # compare with the SHA-256 above; a different one means a newer revision: diff section 12
```

### Verification of the layout (criterion: two independent sources, one an integral official copy)

| Item | Guide (1) | WS manual (2) | cito09 (4) | RecordSchedina.cs (5) | RS-1 extracts of CREAFILE | Verdict |
|---|---|---|---|---|---|---|
| 14 fields, positions DA/A, lengths, total 168 | p. 34 | p. 19 | same order and widths | same widths | lengths as in the table | **verified** |
| 34 blanks for family/group members (19-20) | p. 32, 34 | p. 19 | `' '.repeat(34)` for 19/20 | comment "riempire con blank" | yes | **verified** |
| Comune and province blank if born abroad, state always written | p. 31-32 | "Obbligatorio se Stato Nascita Italia" | blank, Italy code for Italians | same | "D" | **verified** |
| CR+LF between lines, none after the last | p. 33-35 | p. 19 | `join('\r\n')` | n.a. | yes | **verified** |
| Family/group members right after their head | p. 33 | — | head first | — | "D" | **verified** (single official source: guide) |
| UTF-8, at most 1000 lines | p. 31 | — | UTF-8 | — | 1000 | **verified** (guide + implementation) |
| Codes 16-20 split into with document (16-18) and without (19-20) | p. 34 | p. 19 | `hasDoc` 16/17/18 | comment `[16,17,18]` | T | **verified**; number-to-name mapping T2, CasaZen reads the table |
| Allowed characters in names | not stated | not stated | A-Z, space, apostrophe | uppercase | — | **not verified** (T): conservative rule, see above |
| Leading zero in the days of stay | not stated | not stated | `padNum` zero-padded | `PadRight` (space) | — | **assumption** (the portal parses a number) |
| Padding of IDAPPARTAMENTO (File Unico) | not stated | not stated | not wired | — | — | **unknown**: File Unico not generated |

### Web service: not implemented

The specification is now in hand (sources 2 and 3) and consistent: endpoint
`https://alloggiatiweb.poliziadistato.it/service/service.asmx`, namespace `AlloggiatiService`, `GenerateToken` →
`Authentication_Test` / `Test` → `Send` → `Ricevuta` the day after, request and response examples in `MANUALEWS.pdf` p. 7-18, element
names in the WSDL. Even so I did not write the client, for reasons the product owner should weigh:

1. **No sandbox and `Send` is irreversible.** The only safe check is `Test` with a real host's credentials. The manual does not
   say what a repeated `Send` of the same schedine does (duplicates?), the maximum number of schedine per call, the rate or
   lockout limits, or the token lifetime (the example is one hour), and the error table is only available with credentials
   (`Tabella(TipoErrore)`). CO-11 requires "no double resend", which cannot be guaranteed from the documents.
2. **A state is missing.** After a successful `Send` the receipt exists only the day after, for the whole day and the whole
   account (`Ricevuta(date)`, last 30 days, not the current day): there is no per-schedina receipt, and the `Ricevuta` response
   carries only the PDF (the portal's receipts list shows a protocol number, `MANUALEALBERGHI.pdf` p. 19; the web service does
   not return it). The statuses of CO-11 have no "sent, receipt pending" and D6 forbids
   "Inviato" without a real receipt. That needs a new status (migration), a daily job, a private bucket for the PDFs kept 5 years
   (`storage.md`) and a decision on what `ConfirmationNumber` holds.
3. **Product and legal decisions.** Transmitting on behalf of the host sends identity data to the police automatically,
   from stored credentials (CO-14) and a WSKEY that must be regenerated at every password change (one per day). The host's
   consent, the failure handling (partial `Send`: only the correct lines are acquired) and the 6-hour/24-hour deadlines need a
   decision, not a guess.

When the product owner decides to go ahead, the client is a thin SOAP 1.1 wrapper (`HttpClient`, `XDocument`) whose tests can
be built on the literal request and response examples of `MANUALEWS.pdf`, with `Test` before every `Send`, `Send` only when
every line is valid, `Inviato` only with the downloaded receipt, and the first run supervised on a real host account.

### Tests

- `Casazen.Tests/Unit/Regulatory/AlloggiatiRecordFileTests.cs`: positions against the official table, the examples of the guide
  (arrival 16/02/2005, birth 13/03/1973, ROSSI + 45 spaces, PAOLO + 25, AB123CD + 13, `IDENT`), family with 34 blanks, born
  abroad, days of stay, refusals (codes to complete, incomplete data, 0/31 days, names, codes that do not fit, 1001 lines),
  UTF-8 without BOM and pure ASCII.
- `Casazen.Tests/Unit/Services/AlloggiatiWebServiceTests.cs` (`BuildRecordFile_*`): the line built from the stay's guests and
  synthetic code tables, the report untouched, refusals with their codes, unknown booking.
- `Casazen.Tests/Integration/AlloggiatiRecordFilePostgresTests.cs` (PostgreSQL): download with headers, two lines, status
  unchanged, 422 without tables, 422 for a Cyrillic name without personal data in the error, 404 for another org, 401.
- `HostAuthorizationIntegrationTests`: 403 for a supplier and for a collaborator without `guest.read`, 404/403 for another org.
- Frontend: `alloggiati-guest-summary.test.tsx` (button states, download, refusal) and `alloggiati-record-file.api.test.ts`.
  The codes of the tests are **synthetic** (starting with `9`, `91`-`95` for the kinds), never official codes.

### First real check (needs a host account, nothing is sent)

1. Import the four official tables (see "Tabelle codici Alloggiati").
2. On a test stay with a complete family, download the file and upload it on the portal: "Elabora" shows the correct and wrong
   lines **without sending** until "Prosegui" (`MANUALEALBERGHI.pdf` p. 16-17); do not press "Prosegui" for a fake stay.
3. Report any rejected line (portal message and field) to the developers: the open points are the characters allowed in names
   and the two-digit days of stay. If the portal wants something else, change `AlloggiatiRecordFile` and its tests together.
