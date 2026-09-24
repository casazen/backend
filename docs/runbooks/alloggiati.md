# Runbook: Alloggiati Web, honest status and manual submission

Task CO-11 (audit defects A5-01, A9-05, A5-03, A5-37, A5-35; decision D6) and CO-12 (A5-02: guests of the stay and
official code tables, sections at the end). CO-11 needs no external configuration; CO-12 needs an admin to import the
official code tables (see "Tabelle codici Alloggiati"). This page explains what the code does, what the migrations
change and what hosts see. The web service client
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

-- Sessions still marked "Alloggiati sent" (expected: 0 until CO-13).
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
  (`PUT /api/alloggiati/{bookingId}/stay-guests`, `booking.write`) for walk-in guests, corrections and codes.
- **Record export**: the summary reports `dataComplete` and `exportReady`. A missing code blocks only `exportReady`
  (the future record export, CO-13), never the data entry nor the manual submission on the portal. CasaZen still
  generates no record file: positions are not verified (RS-1, CO-13).

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

The same tables come from the web service method `Tabella` (CSV, needs a host's credentials, CO-13).

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
