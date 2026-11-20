# Runbook: notification of the guests to the police in Switzerland (Ticino)

**Issue:** casazen/backend#979 · **Requirements:** NF-126 (complete Ticino file), NF-127 (the 24 hours: staged alerts), NF-128 (retention, declaration, duplicates), NF-132 (Swiss data at the check-in) · **Format of the file:**
[`docs/compliance/ticino-notification-format.md`](../compliance/ticino-notification-format.md) · **Analysis:** `alloggiati-ch/analisi-tecnica.md`
(decisions D1-D16) and `alloggiati-ch/specifica-ticino.md` (verified rules).

The police of canton Ticino want every guest notified **within 24 hours of the arrival**. The portal (`notifiche.ti.ch`) has no API and CasaZen never logs into it,
never keeps a password or the seed of a one-time code and never simulates a browser (decision D1): CasaZen writes the file, the host uploads it and records the
receipt, or declares the day uploaded. Sections 1-7 are the file, its data, the groups and the minors, and the retention of the documents (E30/F01); sections 8-13 are the staged alerts that
watch the 24 hours (E30/F02/US01/T01); sections 14-20 are the declaration of a day, the guard against duplicates and the audit of the files (E30/F02/US02/T01).

## 1. The flow

1. A property is in canton Ticino when its country is `CH` and its Swiss municipality belongs to canton `TI`
   (`ITicinoNotificationService.IsPropertyInTicinoAsync`, the one check of the jurisdiction).
2. The guests fill the online check-in (or the host fills the guests in the wizard). For a Ticino property the check-in asks, on top of
   the Alloggiati record: the **document type and number of every adult** (family and group members included; every minor of a stay
   without adults; in a group of 6 or more only the leader, § 5), the **home address** (street, postal code, town) and the state of residence of
   each guest (optional), and the **plate
   of the car** once for the stay (optional). The public context says it with `requiredGuestFields` and `askVehiclePlate`.
3. Within 24 hours of the arrival the host opens the day (`GET api/properties/{id}/swiss/ticino-notification?date=`), sees who is in the
   file and what the others miss, and downloads the file (`POST …/ticino-notification`, CSV by default). Nothing is stored, but every generation leaves an audit row without personal data
   (§ 17), and the file of a day that is already closed asks for a confirmation (§ 16). The alerts that tell the host the day is still open are § 8-13.
4. The host uploads the file on <https://www.notifiche.ti.ch> («Notifiche → Invio», login and one-time code: CasaZen never logs in,
   decision D1) and records the receipt of the portal (`POST …/upload-receipt`, type `Police`, report date = the arrival day) **or**, with no receipt, declares the day
   uploaded (`POST …/ticino-notification/declare`, § 14-15). Either closes the day.

## 2. Configuration

| Key (environment variable) | Default | Values | Without it |
|---|---|---|---|
| `GuestNotifications:TicinoCsvEncoding` (`GuestNotifications__TicinoCsvEncoding`) | `windows-1252` | `windows-1252`, `utf-8` (case-insensitive) | the default; any other value stops the application at startup (`ValidateOnStart`) |

The committed `appsettings.json` has the default. Nothing else is needed for the file: no flag (it completes a flow already in `develop`), no
secret (CasaZen keeps no credential of the portal). The alerts of § 8-13 have their own flag and options (§ 11).

## 3. The encoding of the CSV

- **Windows-1252** (default, decision D8 ⚖ to confirm with the police): the official model `D-Modello_2.CSV` is single-byte (its «à» is
  the byte `0xE0`), and CasaZen writes the header of the model byte for byte. A character Windows-1252 cannot hold is written without its
  accent (`Ł` → `L`, `ș` → `s`, `ř` → `r`) or, as the last resort, `?` (a Cyrillic, Greek or Chinese letter): never a broken byte. The text is
  first composed (NFC): a letter typed as a base and a combining mark (`e` + U+0301) is the one letter `é`, which Windows-1252 holds, and is not counted.
- **UTF-8** (`GuestNotifications__TicinoCsvEncoding=utf-8`): every letter as typed, no byte order mark. Use it only if the portal shows
  broken accents with Windows-1252 (see § 4).
- The day says, before the download, the encoding (`csvCharset`) and how many characters of its rows the encoding cannot hold
  (`transliteratedCount`), so the page can warn before the host uploads the file. The `charset` of the content type of the file says the
  same.

## 4. Verify the first real upload

There is no test environment of the portal: the first upload is a real notification. Do it with a real arrival, never with invented
guests (it would notify invented persons to the police).

1. **Accents.** Choose an arrival whose guests have accented names (`à`, `é`, `ü`). After the upload open the notification on the portal
   («Ricerca») and compare the names. Broken letters (`Ã©` instead of `é`): the portal reads UTF-8; set `GuestNotifications__TicinoCsvEncoding=utf-8`
   on Railway, redeploy, and check the next arrival. Letters fine: keep the default and write the result in
   `alloggiati-ch/specifica-ticino.md` § 4 (question 1).
2. **Duplicates.** The manual says nothing about a file loaded twice. Never upload the same day twice to find out: if a day must be uploaded
   again (a guest added late), check on the portal («Ricerca», the code of the booking is in «Numero bollettino») whether the guests already
   there are listed twice, and delete the duplicates on the portal.
3. **Optional columns.** Check that a guest without address was accepted (the portal's summary of the upload says per row «salvata» or the
   error), and that «Numero bollettino» and «Numero autovettura» are shown on the notification.

## 5. Groups and minors without an adult (decision D11 ⚖)

The rule of the canton (`alloggiati-ch/specifica-ticino.md` § 1): a group of at least 6 persons is notified with the data of its leader and the
number of members over and under 14, plus a sheet with the first name, last name, birth date and nationality of each member, kept 5 years; a minor
without an adult needs the written authorization of the parents, kept 5 years. How CasaZen applies it (the counting is ⚖ to confirm with the
police, question 4 of the draft request):

- **A group** is a stay whose guests are all typed «capogruppo» (`HeadOfGroup`) or «membro del gruppo» (`GroupMember`), with exactly one head of
  group, 6 or more persons with the leader. A family («capofamiglia», «familiare») is never a group; a smaller group keeps the rule of the adults.
- The file of the day holds **one row**, the leader's (`kind: group`, `groupSize`), with every other member counted in «Mag14» (14 or more,
  adults included) and «Min14» (under 14). The check-in asks the document of the leader only. A member without a birth date keeps the group out of
  the file (it cannot be counted); a member without a name or a nationality does not, but the day warns `group_sheet_incomplete`.
- **The group sheet**: `GET api/properties/{id}/swiss/ticino-notification/group-sheet?bookingId=` (permission `guest.read`, never cached, never
  stored): CSV in UTF-8 with byte order mark, `Nome;Cognome;Data di nascita;Nazionalità`, the leader first. The host keeps it 5 years, best by
  uploading it to the archive (`POST …/upload-receipt`, type `GroupSheet`, report date = the arrival day). The names are typed by the guests and
  the host opens the sheet in a spreadsheet, so it is written by the one writer of those files (`CsvWriter`): a name that starts with `=`, `+`, `-`
  or `@` (or a tab) gets a leading apostrophe and is never run as a formula, and a cell with a semicolon or a quote is quoted.
- **Minors without an adult**: each minor is a row (as before), and the day warns `parental_authorization_needed`. The host files the written
  authorization of the parents in the archive (type `ParentalAuthorization`, report date = the arrival day). Without it the police is to be told at
  once (the rule of the canton): CasaZen never contacts the police.

## 6. Retention of the documents (NF-128)

- The daily job `swiss-compliance-retention` (03:55 UTC, always registered, see [hangfire.md](hangfire.md)) deletes the file (private bucket)
  and the row of every document of type `Police`, `GroupSheet` or `ParentalAuthorization` whose report date is more than 5 years ago (cut day =
  today in Europe/Zurich minus 5 years: a document dated on the cut day is kept until the next day).
- **OTR reports are never deleted**: they are tax evidence of the overnight stays and no retention rule is decided for them (⚖ with the accountant).
- **A date that is already past is refused at the upload**: `POST …/upload-receipt` answers 400 `swiss_receipt_date_too_old` for a `Police`, `GroupSheet` or
  `ParentalAuthorization` document whose report date is before the cut day (`SwissComplianceReceipts.IsPastRetention`, the same cut as the job), because the job
  would delete it the night it is recorded: a mistyped date never destroys a legal proof in silence. An OTR report is never refused for its date.
- A file that cannot be deleted keeps its row and fails the run (Hangfire repeats it): nothing past its time is kept in silence. The log has counts
  and the cut day only.
- Nothing to configure. To check a run: Hangfire dashboard, job `swiss-compliance-retention`, and the log line «Swiss compliance retention
  (documents dated before …): N removed, M could not be removed».

## 7. Data protection

- The home address of the guests and the plate are personal data that nothing searches: they are encrypted at rest with the document
  (`EncryptedColumns`, purpose `Casazen.Guest.Document`), never written in a log, and erased with the guest (anonymization, the
  retention of the Alloggiati data). No image of a document is kept for a Swiss property (decision D6).
- The file is rebuilt on every request and never stored; its download is never cached (`Cache-Control: private, no-store`) and asks
  `guest.read`.

## 8. The alerts of the 24 hours: what they are

Task E30/F02/US01/T01 (NF-127, decision D9 of `alloggiati-ch/analisi-tecnica.md`). This part **watches the 24 hours** of the notification: the host is told in time, once
for each stage, and no more when the day is closed.

The third kind of alert of `ComplianceAlertService` (the one of the ISTAT and tourist tax periods and of the connection to the channel manager, E03/F02/US02/T01), run by the
same hourly `stay-alerts` job ([hangfire.md § 9](hangfire.md#9-stay-alerts-co-10)). It reuses the staged state `ComplianceAlertState`, the compare-and-set that makes a stage
go out once, the e-mail templates, the push chain and the bell with its live update ([in-app-notifications.md](in-app-notifications.md) § 9-10). The placeholder
`TicinoReminderJob` (a TODO that was never registered) no longer exists; `StayAlertService` stays Italian only.

Behind its own flag, **`Features:TicinoNotificationAlerts`, off by default** ([feature-flags.md](feature-flags.md)): off, the job reads no Ticino day, writes no row and
queues nothing. It does not need `Features:ComplianceTrafficLight`; with both off the whole run does nothing.

## 9. Which days, which stages

**Which days.** Only `ITicinoNotificationService.ListPendingDaysAsync` says (last 14 days, every org): the arrival days of the stays that owe a notification (an active status
and no no-show) of the Ticino properties (country `CH` and a municipality of canton `TI`) that no receipt of the portal and no declaration «I uploaded the file» (§ 14) closes. **That
service is the one place that says a day is closed**: the alerts respect the declaration with no change in them. Before a stage is claimed the detail of the day is
read from the same service, so a day closed meanwhile sends nothing. `DueAtUtc` is its deadline: 24 hours from the first recorded arrival, else the end of the arrival day.

**Which stages** (`ComplianceAlertSchedule.DueTicinoStep`; the time zone is the one of the property, `Property.Timezone`, Europe/Zurich; every number is an option):

| Stage | Const | When | Kind | Push type |
|---|---|---|---|---|
| 1 | `TicinoFileToPrepare` | the arrival day: `Compliance:TicinoArrivalAlertDelayHours` (2) after the **first expected check-in** of the day (the earliest `Booking.ExpectedArrivalTime` of its stays, `HH:mm`; `late` and blanks say nothing), or at `TicinoArrivalAlertDefaultHourLocal` (18:00) when none; never after 23:00 | «prepare the file» | `ticino-notification-due` |
| 2 | `TicinoDeadlineApproaching` | `TicinoDueWarningHours` (6) before `DueAtUtc` | the deadline is near | `ticino-notification-due` |
| 3 | `TicinoOverdue` | at `DueAtUtc` | overdue | `ticino-notification-overdue` |
| 4, 5 | `TicinoOverdueReminder(n)` | `DueAtUtc` + n × `TicinoOverdueReminderHours` (24), at most `TicinoMaxOverdueReminders` (2), until the day is closed | overdue, «reminder n of 2» | `ticino-notification-overdue` |

- **Once.** The state row is `(PropertyId, Type = 4 TicinoNotification, ReferenceKey = arrival day yyyy-MM-dd)`. The brief wrote `{propertyId}:{day}`; the property is already a
  column of the unique index and 36 characters of id would not fit the 32 of `ReferenceKey`, so the key is the day alone. A stage is claimed with one conditional `UPDATE` before it
  is sent: a retry, a second run or a second server never sends it twice; a delivery that fails after the claim is logged and not repeated (at most once, like the other alerts).
- **No backlog.** Only the highest stage due is sent: a run after an outage sends one message for the day, and the last stage is dropped a day after its time. A day whose
  deadline passed more than three days ago is silent.
- **Daylight saving.** Stage 1 is a wall-clock time of the property: 18:00 is 17:00 UTC in winter and 16:00 UTC in summer; a time in the hour the clocks skip (02:00-02:59 on the last Sunday of
  March) is read one hour later on the clock and one in the hour they repeat (October) is its first occurrence (`RomeCalendar.ToUtc`). The deadline and the reminders are absolute durations.
- **A known coincidence.** With no recorded arrival the deadline is the end of the arrival day (00:00), six hours before it is 18:00, which is also the default time of stage 1: that
  day only the higher stage goes out. Registering the arrival (or an expected check-in before 16:00) separates them.

## 10. Who is told, how, and what it says

- **E-mail**: one message to the **contact address of the organization** (`ComplianceAlertRecipientResolver.ForPropertyAsync` gives that single address as `Emails`, like the stay alerts
  and the ISTAT alerts; the people it also lists are read for the popup of the router, which the Ticino alerts do not use). There is **no e-mail to a single person and no narrowing by
  reach** on this channel; nobody of another org is told. Texts `TicinoNotificationAlert_*` of `EmailTexts.resx` and `EmailTexts.en.resx` (**Italian and English only**: German, French and
  Spanish hold the guest check-in e-mails and nothing else, `EmailTextsParityTests`); the button leads to `/app/short-rent/compliance`.
- **Push, bell and live update** through `IPushNotificationService.Enqueue` with `PushAudience.PropertyHosts(propertyId)` (the **person in charge of the property**, while they still reach it
  (a collaborator «Solo alcuni» only for a property they were given), and the **administrators of the org**: `HostNotificationAudience`): the types `ticino-notification-due` and
  `ticino-notification-overdue`, route `/properties`. The decorator writes the bell entry and the job publishes `notificationsChanged`; both carry the **property** as `entityId` (the id of the
  audience: an arrival day is not an entity, so the entry names the property and not the day, and the client opens the Ticino page of the property on its first day to notify). The web
  client is to show the pop-up and read the cockpit again on those two types (the frontend slice of the notification types, E30/F02/US03/T01: until it maps them the bell shows the generic
  sentence for a type it does not know).
- **Known limit, like the ISTAT alerts.** There is no «compliance» permission in the catalog, and the people told by push and bell are not narrowed to who holds `alloggiati.submit` (the permission
  that records the receipt, `SwissComplianceController` `upload-receipt`): the person in charge may be a collaborator, who does not have it, and hears about a duty they cannot close.
- **No «To do» and no popup of the router**: the cockpit lists the day in its own section (`ticinoNotifications`), outside the feed of the «To do» on purpose.
- **Privacy.** The text names the property, the arrival day, the deadline and the **types** of field the data of the day miss: the codes of `TicinoMissing` (`firstName`, `lastName`,
  `birthDate`, `nationality`, `documentType`, `guests`) and `structureId` (the identifier of the structure is not entered), translated. Never the name of a guest, a value, a document
  number or a booking code, in the e-mail, the push, the key, the state or the log (logs carry ids, the day and counts). A day with no complete row still alerts. A code a later version
  of the file plan adds is told as «other data» until it has its own `TicinoMissing_<code>` text in both `.resx`.

## 11. Configuration of the alerts

| Variable | Default | Meaning |
|---|---|---|
| `Features__TicinoNotificationAlerts` | `false` | The flag |
| `Compliance__TicinoArrivalAlertDelayHours` | `2` | Hours after the first expected check-in for stage 1 |
| `Compliance__TicinoArrivalAlertDefaultHourLocal` | `18` | Hour (0-23) of stage 1 when no stay of the day has an expected check-in |
| `Compliance__TicinoDueWarningHours` | `6` | Hours before the deadline for stage 2 |
| `Compliance__TicinoMaxOverdueReminders` | `2` | Reminders after «overdue» (`0` = none) |
| `Compliance__TicinoOverdueReminderHours` | `24` | Hours between them (at least 1) |

They are proposals the product owner may change (listed in [deploy-checklist.md](deploy-checklist.md) § 2). A value out of its range is held to it, never an error.

## 12. Checking and rolling back the alerts

```sql
-- The Ticino alerts of the last days, one row per property and arrival day (replace the schema)
SELECT "PropertyId", "ReferenceKey" AS arrival_day, "Stage", "AlertCount", "LastAlertAt"
FROM casazen_test."ComplianceAlertStates"
WHERE "Type" = 4 ORDER BY "UpdatedAt" DESC LIMIT 50;

-- The bell entries of those alerts, per type
SELECT "Type", count(*) FROM casazen_test."InAppNotifications"
WHERE "Type" IN ('ticino-notification-due', 'ticino-notification-overdue') AND "CreatedAt" > now() - interval '7 days' GROUP BY 1;
```

- **Turn it on (test only, on the product owner's order)**: `Features__TicinoNotificationAlerts=true` on the `test` environment (the change redeploys). Properties of canton Ticino need
  a municipality of the canton (`Property.SwissMunicipalityCode`) and the structure identifier; a day needs a stay that arrived and no receipt.
- **Roll back**: set the flag off (or unset it): the next run reads and sends nothing. The state rows are harmless; to start over for a day delete its row
  (`DELETE FROM casazen_test."ComplianceAlertStates" WHERE "Type" = 4 AND "PropertyId" = '<id>' AND "ReferenceKey" = 'yyyy-MM-dd';`), which makes the stages due at that moment go out once more.
  No migration: `ComplianceAlertStates.Type` is an integer with no CHECK, and value 4 is new.

## 13. Code and tests of the alerts

`Casazen.Core/Entities/ComplianceAlertState.cs` (`ComplianceAlertType.TicinoNotification = 4`), `Casazen.Core/Services/IComplianceAlertService.cs` (`ComplianceAlert`,
`ComplianceAlertKind`, `ComplianceAlertStage`), `Casazen.Core/Services/ComplianceAlertSchedule.Ticino.cs` (the rules), `Casazen.Core/Options/ComplianceOptions.cs`,
`Casazen.Core/Features/FeatureFlags.cs`, `Casazen.Core/Services/IPushNotificationService.cs` (`PushTypes.TicinoNotificationDue|Overdue`),
`Casazen.Infrastructure/Services/ComplianceAlertService.Ticino.cs` (the run), `Casazen.Infrastructure/Services/NotificationService.cs`,
`Casazen.Infrastructure/Email/Templates/EmailTemplates.TicinoNotification.cs`. Tests: `ComplianceAlertTicinoTests` (the schedule with the two daylight saving changes, each stage once,
a closed day stops the alerts, another org is never alerted, flag off sends nothing, the payload holds no personal data, the registration with `ValidateScopes`, the enumerations of
`ComplianceAlertType` and `PushTypes`).

## 14. The declaration «I uploaded the file» and the one rule of a closed day

Task E30/F02/US02/T01 (NF-128, decision D10 of `alloggiati-ch/analisi-tecnica.md`). Until now a Ticino day closed only with a receipt of the portal, so a host without a receipt stayed
stuck, and the file could be generated again as often as the host liked although the portal may create duplicates when a day is uploaded twice (`specifica-ticino.md` § 3). Three things
are added, with **no flag** (they complete a flow already in `develop`):

1. **The declaration**: a person of the org says the day was uploaded to the portal, with or without a receipt.
2. **A guard against duplicates**: the file of a closed day is generated again only on an explicit confirmation (§ 16).
3. **An audit row for every file generated**, with no personal data (§ 17).

**A day is closed** when a `Police` receipt is recorded for it (§ 1) **or** a declaration of it exists. `ITicinoNotificationService.ListPendingDaysAsync` is the one place that applies the
rule: a closed day is not listed, so the cockpit section `ticinoNotifications` and the alerts of § 8-13, which read nothing else, stop at once. A receipt still closes the day after the
declaration is undone. A declaration with a regime code belongs to another canton (§ 19) and never closes a day of the Ticino police.

## 15. The API: declare, undo and what the day says

| Route | Permission | Body or query | Answer |
|---|---|---|---|
| `POST api/properties/{id}/swiss/ticino-notification/declare` | `alloggiati.submit` (the permission that records a receipt), on top of the `booking.read` of the controller | `{ "arrivalDay": "2026-11-19", "receiptId": null }` | 200 `{ propertyId, arrivalDay, declaredAt, declaredBy, receiptId, created }`, never cached, **no guest data** |
| `DELETE api/properties/{id}/swiss/ticino-notification/declare?arrivalDay=2026-11-19` | the same | the day in the query | 204, also when the day was not declared |
| `GET api/properties/{id}/swiss/ticino-notification?date=` | `guest.read` (unchanged) | — | the day, which gains `declaredAt`, `declaredBy`, `fileGeneratedAt`, `fileGeneratedCount` and `alreadyDeclared` |
| `POST api/properties/{id}/swiss/ticino-notification` | `guest.read` (unchanged) | `{ "reportDate": "2026-11-19", "format": "csv", "confirmRegenerate": false }` | the file, or 409 `ticino_day_already_declared` (§ 16) |

- **Idempotent.** `POST …/declare` on a day that is declared already answers the declaration that exists (`created: false`), whoever repeats the call: `declaredAt` and `declaredBy` are the
  first person's, and nothing is told again. Two hosts pressing at the same moment: the unique index (§ 19) lets one in and the other gets the declaration that won (23505 is handled, not
  an error). A `receiptId` the declaration does not have yet is **attached** to it (a host that declared first and uploaded the receipt later); a call without one never removes it.
- **`receiptId`** is optional (decision D10) and must be a `Police` receipt of this property whose report date is the arrival day, as `POST …/upload-receipt` records it: another property's,
  another day's or another type's is refused with 422 `ticino_declaration_receipt_invalid`.
- **A day that has not started** (after today in Europe/Zurich) cannot be declared: 422 `ticino_declaration_day_in_future`. It would close a day whose guests have not arrived and silence its
  alerts; a past day with no stay is harmless and accepted.
- **Undo** is by any person of the same org with the same permission (`alloggiati.submit`); the row is deleted and the day is to notify again unless a receipt closes it.
- **Who may call, as for the receipt** (`SwissComplianceController.AuthorizeAsync`): a property of another org is a 404 (nothing is declared or read), one the caller's scope does not reach (a
  collaborator «Solo alcuni» not given it) is a 403. The person in the token (`sub`) is the one written on the declaration; the client never names it.
- **The day** (`GET`, which already lists the guests and so asks `guest.read`): `declaredAt` (UTC) and `declaredBy` (first and last name of the person, `null` while nobody declared and when
  the account no longer exists), `fileGeneratedAt` / `fileGeneratedCount` (the CSV and the XML of the day, § 17) and `alreadyDeclared`, which is **the same rule as the pending list**: the day is
  declared **or** has a receipt (`receiptCount` > 0). `alreadyDeclared` is the flag a page reads to ask the confirmation of § 16 before it sends `confirmRegenerate: true`.
- Errors: 400 `validation_error` (a missing `arrivalDay`), 401 `unauthorized` (a token with no subject), 403 `forbidden`, 404 `property_not_found`, 422 `property_not_in_ticino`,
  `ticino_declaration_day_in_future`, `ticino_declaration_receipt_invalid`; texts `SwissArrivalDayRequired`, `TicinoDeclarationDayInFuture`, `TicinoDeclarationReceiptInvalid` in `SharedResources.resx` and `.en.resx`.

## 16. The guard against duplicates

The portal does not say what it does with a file loaded twice (`specifica-ticino.md` § 4, question 2), so CasaZen does not make it easy. `POST …/ticino-notification` for a day that is **closed**
(declared, or with a receipt: § 14) answers **409 `ticino_day_already_declared`** (`TicinoDayAlreadyDeclared`, Italian and English) unless the body says **`"confirmRegenerate": true`**; no file is
generated and no audit row is written for the 409. A day that is not closed is never asked, and the confirmation changes nothing else: the file is the same.

- The checks of the data come first: a closed day with nothing to write answers 422 `ticino_notification_empty` (or the other data errors), not a confirmation for a file that would not exist.
- **Receipts count.** A receipt is the proof of the same fact as the declaration (decision D10: the receipt is the optional proof of the upload), so a day with only a receipt is also asked: the
  risk of duplicates is the same, and the rule of a closed day is one for the alerts, the cockpit and this guard. A consequence to know: the old page of the console, which downloads again after a
  receipt, gets the 409 until the slice of the frontend (E30/F02/US03/T01) sends `confirmRegenerate`.
- The group sheet (`GET …/group-sheet`) is kept by the host and never uploaded as a notification, so it has no guard (it only writes its audit row).

## 17. The audit of the files (`SwissNotificationFileEvents`)

Every file CasaZen generates for the police writes **one row**, whoever asked and whether or not the file was uploaded afterwards, in the same request: a row that cannot be written fails the
request, so no file leaves unaudited.

| Column | Meaning |
|---|---|
| `OrgId`, `PropertyId` | the org (tenant filter, TN-2) and the property (cascade) |
| `ArrivalDay` | the day of the file (the arrival day of its stay for a group sheet; the first day of the period for the register of E31) |
| `Format` | `csv`, `xml`, `register` or `groupSheet` (text, `CK_SwissNotificationFileEvents_Format`) |
| `RowCount` | the persons in the file (the members of a sheet) |
| `Sha256` | the hash of the bytes of the file, lowercase hexadecimal: the file is never stored, so the hash is how a file is recognized later |
| `CreatedByUserId`, `CreatedAt` | the person (Auth0 subject, `null` when no person asked) and the instant |

**No personal data**: never a name, a document, an address, a plate, a booking code or the name of the person (`TicinoDeclarationTests.FileEvent_NoPersonalData_Test` writes a file full of them and
looks for them in every column; a test also fixes the list of columns, so a new one makes somebody decide again). The day counts only the **CSV and XML** rows (`fileGeneratedCount`, `fileGeneratedAt`): a
group sheet is kept by the host and a register is not a notification.

## 18. Live update for the other hosts

When a declaration is **created** or **removed** the hosts of the property learn it without a refresh, through the chain of the alerts of § 10: `TicinoDeclarationLiveRefresh` (the one class that tells
them, like `BookingFlowLiveRefresh` for a stay) calls `IPushNotificationService.Enqueue` with
`PushAudience.PropertyHosts(propertyId)` (the person in charge while they reach the property and the administrators of the org: the person who acted is one of them, and their own page already has
the answer), type **`ticino-notification-declared`** (one type for both; `PushTypes.IsTicinoNotification`), route `/properties`. It is **not one of the eight kinds of event of the notification
preferences** (NF-65): it is the refresh of what a co-host did, so it must reach the open pages of every host of the property whatever channel they chose for the deadlines; for that reason the
class is listed, with the other live refreshes, in the legacy list of `HostNotifyingPathsArchitectureTests` («none planned (outside the eight kinds of event of NF-65)»). The decorator writes the bell entry (**`entityId` = the property**, like the
two alerts) and the job publishes `notificationsChanged`, on which the open pages read the cockpit and the day again. The text of the push tells the declaration from the undo (`Push_TicinoNotificationDeclared_*`
and `Push_TicinoNotificationUndone_*` in `EmailTexts.resx` and `.en.resx`) and names the property and the day only: never a guest, a value or the person who acted (the cockpit shows who).

The delivery key is `ticino-declaration:{propertyId:N}:{yyyyMMdd}:{declared|undone}:{yyyyMMddHHmmssfff}`: a day declared, undone and declared again is three events. Nothing is told for a repeated declaration, for an
undo with nothing to undo or when only a receipt is attached. The push is queued **after** the row is saved and is best effort: a queue that is down or that refuses is logged with ids
(`Ticino declaration push of property … could not be prepared / not queued`) and never fails the person who declared.

## 19. The data, the key and the other cantons

- **`SwissNotificationDeclarations`**: `OrgId` (restrict), `PropertyId` (cascade), `ArrivalDay` (`date`), `DeclaredByUserId` (the Auth0 subject, no foreign key: the declaration outlives the account),
  `DeclaredAt`, `Channel` (`portal`, text, default `portal`), `ReceiptId` (nullable, **SET NULL**: a receipt removed by the retention of § 6 leaves the declaration without it, never deletes it) and `RegimeCode`
  (nullable, 12 characters).
- **The key.** One declaration for each property, day and regime. `RegimeCode` **null is the notification to the police of Ticino**; another canton (E31) writes its own code and shares the table **with no
  migration**. PostgreSQL takes two NULLs as different values, so the key is **two partial unique indexes** (the pattern of `MarketplaceActivation`):
  `UX_SwissNotificationDeclarations_Property_Day` on (`PropertyId`, `ArrivalDay`) where `RegimeCode IS NULL`, and `UX_SwissNotificationDeclarations_Property_Day_Regime` on
  (`PropertyId`, `ArrivalDay`, `RegimeCode`) where it is not. The Ticino rows are the ones **without** a code (`TicinoNotificationService` reads and writes only those); another regime must not use `NULL`.
- **`SwissNotificationFileEvents`**: § 17. Both tables are `ITenantOwned`: every read names the org of the property, and the global filter scopes the caller.
- **Retention.** Neither table holds guest data (a user id, codes, counts, a hash and dates), so the job of § 6 does not touch them: the rows live as long as their property. An undo deletes the
  declaration (the log of the application and the live message say who did what).
- **Migration**: one, `AddSwissNotificationTracking` (after `AddSwissGuestFields`), two tables and their indexes; `Down` drops them. No data to backfill, no flag.

## 20. Checking, rolling back, code and tests

```sql
-- The declarations of the last days (replace the schema); a null regime is the Ticino police
SELECT "PropertyId", "ArrivalDay", "DeclaredAt", "Channel", "ReceiptId", "RegimeCode"
FROM casazen_test."SwissNotificationDeclarations" ORDER BY "DeclaredAt" DESC LIMIT 50;

-- How many files were generated for each day, and when last
SELECT "PropertyId", "ArrivalDay", "Format", count(*) AS files, max("CreatedAt") AS last
FROM casazen_test."SwissNotificationFileEvents" GROUP BY 1, 2, 3 ORDER BY max("CreatedAt") DESC LIMIT 50;
```

- **Make a day to notify again** (a wrong declaration nobody can undo): `DELETE FROM casazen_test."SwissNotificationDeclarations" WHERE "PropertyId" = '<id>' AND "ArrivalDay" = 'yyyy-MM-dd' AND "RegimeCode" IS NULL;`
  (the day is listed again at the next read; a receipt of the day would still close it).
- **Roll back the release**: the previous version ignores both tables; the declarations then close nothing and the days they closed are listed again. To drop the tables, `dotnet ef migrations remove` before
  the migration is applied, or a migration that reverts it (its `Down` drops the two tables).
- **Code**: `Casazen.Core/Entities/SwissNotificationDeclaration.cs`, `SwissNotificationFileEvent.cs` (with `SwissNotificationChannels` and `SwissNotificationFileFormats`),
  `Casazen.Core/Services/ITicinoNotificationService.cs` (`DeclareDayAsync`, `UndeclareDayAsync`, `TicinoFileRequest`), `Casazen.Infrastructure/Services/TicinoNotificationService.cs`,
  `Casazen.Infrastructure/Data/Configurations/SwissNotification*Configuration.cs`, `Casazen.Web/Controllers/SwissComplianceController.cs`, `Casazen.Web/DTOs/SwissCompliance/SwissComplianceDtos.cs`,
  `Casazen.Core/Services/IPushNotificationService.cs` (`PushTypes.TicinoNotificationDeclared`, `PushDeliveryKeys.TicinoNotificationDeclared`),
  `Casazen.Infrastructure/Services/TicinoDeclarationLiveRefresh.cs` (the push), `Casazen.Infrastructure/Email/Templates/EmailTemplates.TicinoNotification.cs`. **Tests**: `TicinoDeclarationTests` (idempotent declare
  and the race on the key, undo, the pending list and the day, a receipt also closes the day, isolation between organizations, the regeneration guard, the audit rows and their privacy, the live message,
  the model of the key, the JSON names of the contract), `SwissComplianceControllerTests` (`Declare_OtherOrganizationForbidden_Test`, the permission of both routes, the confirmation and the caller passed to
  the service) and `HostNotifyingPathsArchitectureTests` (the live refresh is listed as outside the eight kinds of event of NF-65).
