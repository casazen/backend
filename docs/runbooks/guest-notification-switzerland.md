# Runbook: notification of the guests to the police in Switzerland (Ticino)

**Issue:** casazen/backend#979 · **Requirements:** NF-126 (complete Ticino file), NF-127 (the 24 hours: staged alerts), NF-128 (retention, declaration, duplicates), NF-130 (the guest register), NF-132 (Swiss data at the check-in) · **Format of the file:**
[`docs/compliance/ticino-notification-format.md`](../compliance/ticino-notification-format.md) · **Analysis:** `alloggiati-ch/analisi-tecnica.md`
(decisions D1-D16) and `alloggiati-ch/specifica-ticino.md` (verified rules).

The police of canton Ticino want every guest notified **within 24 hours of the arrival**. The portal (`notifiche.ti.ch`) has no API and CasaZen never logs into it,
never keeps a password or the seed of a one-time code and never simulates a browser (decision D1): CasaZen writes the file, the host uploads it and records the
receipt, or declares the day uploaded. Sections 1-7 are the file, its data, the groups and the minors, and the retention of the documents (E30/F01); sections 8-13 are the staged alerts that
watch the 24 hours (E30/F02/US01/T01); sections 14-20 are the declaration of a day, the guard against duplicates and the audit of the files (E30/F02/US02/T01); sections 21-24 are the regimes of Italy
and of the 26 cantons, the card of a property and the readiness of a Swiss property (E29/F02/US01), with the catalog in [`docs/compliance/guest-registration-switzerland.md`](../compliance/guest-registration-switzerland.md);
section 25 is the guest register of any Swiss property, Ticino included, a table to keep and to print (E31/F01/US01);
section 26 is the assisted notification of the guests to the authority of a canton other than Ticino: the days, the data ready to copy, the declaration and the alerts (E31/F02/US01/T01).

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
  a municipality of the canton (`Property.SwissMunicipalityCode`) and the structure identifier; a day needs a stay that arrived and neither a receipt of the portal nor a declaration (§ 14).
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
  an error). A `receiptId` is **attached** only to a declaration that has none (a host that declared first and uploaded the receipt later); a receipt already attached is never replaced by another, and a call without one never removes it.
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
  risk of duplicates is the same, and the rule of a closed day is one for the alerts, the cockpit and this guard. It breaks no page of the console: the one already in `develop` (frontend PR #649,
  `SwissCompliancePage.tsx`) shows the download buttons only while the day has no receipt (`receiptCount` is 0), so it never asks for the file of a day with a receipt; the frontend slice
  (E30/F02/US03/T01) sends `confirmRegenerate` when it asks for the file of a closed day.
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

## 21. The regime of a property in any canton (E29/F02/US01/T01, NF-125)

Ticino is one of 28 regimes: Italy, the 26 cantons and the Swiss place nobody knows (`CH-UNKNOWN`), with obligation, channel, deadline, retention, private rentals, **certainty**, sources and
what CasaZen does (catalog and rules: [`docs/compliance/guest-registration-switzerland.md`](../compliance/guest-registration-switzerland.md)). `IGuestRegistrationRegimeResolver` decides which one a
property is under (country, then the canton the place resolver gives, then the catalog), and the Ticino code above keeps working: it reads the same canton from the same resolver.

| Variable | Default | Meaning |
|---|---|---|
| `Features__GuestRegistrationRegimes` | `false` | The flag |

- **Behind the flag.** `GET api/properties/{id}/guest-registration` → `{ regime, effectiveDuty, override, place, identifierMissing }` and `PUT` the same route with `{ "override": "Required" | "NotRequired" | null }`
  (the permissions `property.read` and `property.write`, the row check of the property: another org's property is a 404, one outside the caller's scope a 403; the member `override` must be in the body).
  Off, both answer 404 before authentication and **nothing reads `Properties.GuestNotificationOverride`** (the resolver answers as if it were empty): Ticino behaves exactly as before.
- **`effectiveDuty`.** `Required` (raised), `NotRequired` (not raised) or `Ask` (the host decides; nothing in red). The setting of the host wins; without it Ticino and a verified obligation to report that covers
  private rentals are `Required`, every other canton `Ask` (table in the compliance page). An Italian property cannot be given a setting (422 `guest_registration_override_not_applicable`).
- **`identifierMissing`.** The reasons of § 22, the same codes the activation and the onboarding part use; empty for an Italian property.
- **No supplier name** in the texts of the card (the keys `GuestRegime_*` of `SharedResources`, Italian and English); the sources are the pages of the authorities.

## 22. The readiness of a Swiss property (E29/F02/US01/T02, NF-129): no flag

A Swiss property lacks one thing at a time, in this order (`GuestRegistrationReadiness`, the one definition): `swiss_municipality_missing` (no municipality stored, none derived from the postal code and the city,
or several left open: the canton, and with it the regime, is not known) and, when the municipality is in Ticino, `ticino_structure_id_missing` (no valid «Numero identificativo LEAR / NO-LEAR»: the same
rule as the file, `TicinoStructureId.IsValid`). The host's setting is never read here.

- **The activation of the property.** `PropertyComplianceStatusService` adds the step `guest-registration` to every Swiss property (never to an Italian one). For a property that is **not active** (pending, or
  suspended for another reason) what is missing **blocks the activation**: the property stays pending with the reason, and the cockpit lists it in the `Missing` of the existing `propertiesPending` section
  (`code` = the reason, `field` = `guest-registration`; no new cockpit action). A Swiss property is listed there with its name, address and city entered, as an Italian one is with its CIN.
  For an **active** property the step is a reminder that blocks nothing: the nightly check and the re-evaluation after an edit never suspend a published house because its structure number is not entered yet.
- **Creating a property** accepts `ticinoStructureId`, validated and stored normalized as on the update. The municipality is still only on the update (the create request has no country).
- **The onboarding part «Guest registration»** (`guestRegistration`, key 12, plan order 11 before the activation, launch target `guestRegistration`): done only when every **active Swiss** property the caller reaches has its
  municipality and, in Ticino, the structure number. `done` of `total` count those properties; `missing` lists (at most 50) the properties that lack something with `{ propertyId, reason, code }`, the `code` being the step of
  the activation wizard to reopen. An organization with no active Swiss property sees the part as `notAvailable` (`no_swiss_property`); nothing requires it, so it can be put off.

## 23. Checking and rolling back

```sql
-- The properties whose host has set the notification of the guests (replace the schema)
SELECT "GuestNotificationOverride", count(*) FROM casazen_test."Properties" WHERE "GuestNotificationOverride" IS NOT NULL GROUP BY 1;
```

- **Turn it on (test only, on the product owner's order)**: `Features__GuestRegistrationRegimes=true` on the `test` environment (the change redeploys).
- **Roll back**: set the flag off (or unset it): the endpoints answer 404 and the settings stop being read; the column stays, harmless. The readiness (§ 22) is not behind the flag: it is a step of the activation, a reminder
  for an active property.
- **Migration** `AddPropertyGuestNotificationOverride`: a nullable integer `GuestNotificationOverride` on `Properties` (1 = `Required`, 2 = `NotRequired`) with the CHECK `CK_Properties_GuestNotificationOverride`
  (null or one of the two); every existing row stays null. Down: drops both.

## 24. Code and tests

`Casazen.Core/Regulatory/GuestRegistration/` (`GuestRegistrationRegime`, `GuestRegistrationRegimeCatalog`, `GuestRegistrationDutyRules`, `GuestRegistrationReadiness`), `Casazen.Core/Enums/GuestNotificationOverride.cs`,
`Casazen.Core/Services/IGuestRegistrationRegimeResolver.cs`, `Casazen.Infrastructure/Services/` (`GuestRegistrationRegimeResolver`, `GuestNotificationOverrideService`, `GuestRegistrationPartEvaluator`,
`PropertyComplianceStatusService` and `ComplianceWizardService` for § 22), `Casazen.Web/Controllers/PropertyGuestRegistrationController.cs`. Tests: `GuestRegistrationRegimeCatalogTests` (every canton of the municipality directory is in
the catalog with an obligation, a channel, a certainty, a date and a source; Ticino, Appenzell Innerrhoden and Italy as above; the sentences in both languages; no supplier name), `GuestRegistrationRegimeResolverTests`
(Lugano, the unknown place, Italy, the effective-duty table, the flag, the tenant), `PropertyGuestRegistrationControllerTests` (another org, a collaborator «Solo alcuni», the missing member), `GuestRegistrationPartEvaluatorTests`,
`PropertyComplianceStatusServiceTests` (the Swiss step).

## 25. The guest register of any Swiss property (E31/F01/US01/T01, NF-130, decision D12b)

The host's own record of its guests for a period, as a clean table to keep and to print: what the cantons that only ask for a **register** (kept, and shown to the police) expect, and a table to copy
from for the cantons that notify on a portal. It is the same for **every Swiss regime, Ticino included**; an Italian property has none (its register is the Alloggiati file). CasaZen writes a table and
gives **no legal advice**: how long the host keeps it is the hint of the regime (§ 21), not a rule of the file. It sends nothing to anybody and never stores the file.

| Variable | Default | Meaning |
|---|---|---|
| `Features__GuestRegistrationRegimes` | `false` | The flag of § 21: off, both endpoints answer 404 `not_found` before authentication |

- **The endpoints** (`SwissComplianceController`, under `api/properties/{propertyId}/swiss`):
  - `GET guest-register?from=&to=&format=csv`: the file. `from` and `to` are required (`yyyy-MM-dd`), both days **included**, by the **arrival day** of the stay; at most **366 days**, both ends counted
    (400 `swiss_period_too_long`; `to` before `from`: 400 `swiss_period_invalid`; the same codes and words as the OTR report). `format` is `csv` or absent (another: 400 `guest_register_format_invalid`).
  - `GET guest-register/info?from=&to=`: `{ propertyId, from, to, rowCount, retentionYears, regimeCode }`, what the screen reads before it downloads. No file, no audit row, no guest in it.
  - **Permission** `guest.read` on top of the `booking.read` of the class, and the row check of the property (another org's property: 404 `property_not_found`; one the caller does not reach, a collaborator
    «Solo alcuni» not given it: 403). `Cache-Control: private, no-store`. A property that is not in Switzerland (Italy, another country): 404 `guest_register_not_available`.
  - **`X-Retention-Years`** (the file only): the `RetentionYears` of the regime of the property, the same value as `retentionYears` of the info. **Absent** when the sources do not say (Thurgau, Zurich, Uri...) and
    for a property whose municipality is not known (`CH-UNKNOWN`: the register is made all the same, with no canton and no number).
- **The columns** (`GuestRegisterFile.HeaderKeys`; one header line and nothing else): arrival, departure, last name, first name, date of birth, nationality, document type, document number, street, postal code,
  city, country, vehicle plate, booking code, minor and **signature** (always empty, for the printed copy). Dates are `dd.MM.yyyy`; nationality and country are **alpha-3** (`TicinoCountryCodes`; a nationality that is
  not in the table is left empty, never guessed); the document type is passport, identity card or other; the plate and the booking code (`XXXXX-XXXXX`) are repeated on every line of the stay so that each line stands
  on its own; **minor** is yes under 18 at the arrival, no from 18, and empty when the check-in has no date of birth. The header and the words yes, passport, identity card and other are resource keys of
  `SharedResources` (`GuestRegisterCol*`, `GuestRegisterYes`...), Italian or English by the language of the request (any other is Italian); the word **no** reads the same in both languages, so it is a constant of
  the code (`GuestRegisterFile.NoWord`) and not a key (the localization test of the resource files refuses a key whose English text is the Italian one); the codes and the dates never change.
- **The file**: written by the one writer of the files the host opens in a spreadsheet (`CsvWriter`, SR-10): **UTF-8 with its byte order mark**, `;`, CRLF after every line, a text that a spreadsheet would run as a
  formula (it starts with `= + - @`, a tab or a CR) written with a leading apostrophe, a cell with a semicolon, a quote or a line break quoted. **No comment, title or note line**: it stays a table. A period with
  no guest is the header line alone (200, never an error). At most 9,999 guests (the writer takes 10,000 lines, the header included: 422 `csv_too_many_rows`). Name: `registro-ospiti-ch-{from}-{to}.csv`.
- **Which guests**: every guest of every stay of the property that **arrived** in the period (the stored arrival day), adults and minors, the rows of its check-in or, while it has none, the booker (the same reading as
  the Ticino file, `TicinoStayFacts.From`); in the order of the arrival day, then the stay, then its check-in. A stay counts only if it owes a police communication
  (`AlloggiatiStatusRules.ActiveBookingStatuses`: confirmed, checked in, checked out) and is not a no-show: **a request, a hold, a cancelled booking and a no-show are never listed**. A **future arrival** is listed
  with its date (a host who prints the register for the next days gets the lines to sign at the arrival). A guest whose data are incomplete is **still a line**, with empty cells (unlike the Ticino file, nobody is left
  out for a missing value); a stay with fewer guests recorded than the booking counts lists the recorded ones. A guest whose data the retention erased (`Gdpr:Retention:AlloggiatiData`) is still a line, with the
  placeholder the erasure left.
- **What CasaZen keeps, and what the regime says**: the register is rebuilt from the check-in on every request, and CasaZen erases the guest data 5 years after the check-out (`Gdpr:Retention:AlloggiatiData`). Where the
  regime says a longer time (`retentionYears` above 5, a canton with 10), it is the **file the host downloaded** that has to be kept that long, in the host's own storage.
- **The audit**: one row of `SwissNotificationFileEvents` for every file (§ 17), format `register`, `ArrivalDay` = the **first day of the period**, `RowCount` = the guests listed, `Sha256` of the bytes of the file, the
  person who asked and the instant; no name, no document, no address, no plate, no booking code. A row that cannot be written fails the request. The info writes nothing. The day of a Ticino notification does not
  count it (`SwissNotificationFileFormats.PoliceFile` is `csv` and `xml`).
- **The log** holds ids, dates, the number of lines and the code of the regime, never a guest.

### 25.1 Checking and rolling back

```sql
-- The registers made in the last days (replace the schema): counts and hashes, no name
SELECT "PropertyId", "ArrivalDay", "RowCount", "CreatedByUserId", "CreatedAt", "Sha256"
FROM casazen_test."SwissNotificationFileEvents" WHERE "Format" = 'register' ORDER BY "CreatedAt" DESC LIMIT 50;
```

- **Try it (test only, on the product owner's order)**: `Features__GuestRegistrationRegimes=true` on `test`; on a Swiss property with a stay, `GET api/properties/{id}/swiss/guest-register/info?from=2026-01-01&to=2026-12-31`,
  then the file; open it in a spreadsheet set to Italian (the accents, the empty signature column).
- **Roll back**: set the flag off, both endpoints answer 404 and nothing else changes. **No migration and no new variable**: the table of § 17 already accepts the format `register`.

### 25.2 Code and tests

`Casazen.Core/Regulatory/GuestRegistration/GuestRegisterFile.cs` (the line, the columns, the writer, the period rule), `Casazen.Core/Services/IGuestRegisterService.cs`, `Casazen.Infrastructure/Services/GuestRegisterService.cs`,
`SwissComplianceController` (`DownloadGuestRegister`, `GetGuestRegisterInfo`) with `GuestRegisterQuery` and `GuestRegisterInfoResponse` in `SwissComplianceDtos.cs`. The reading of a stay shared with the Ticino file is
`TicinoStayFacts.From` and `TicinoGuestFacts.From` (`TicinoPoliceFile.cs`). Tests: `GuestRegisterFileTests` (exact content, both languages, accents, the formula guard, minors, the home, the period rule),
`GuestRegisterServiceTests` (the guests, `GuestRegister_ExcludesCancelledAndNoShow_Test`, the period by arrival day, the booker, the regime and the retention hint, an Italian property, another organization,
`GuestRegister_Audit_NoPersonalData_Test`, nothing personal in the log) and `SwissComplianceControllerTests` (`GuestRegister_OtherOrganizationForbidden_Test`, the permission, the flag, the period and the format, the headers).

## 26. The assisted notification of the other cantons (E31/F02/US01/T01, NF-131)

In a canton other than Ticino whose regime (§ 21) is an **obligation to report to an authority** and for which CasaZen's support is the assisted notification (today Basel-Stadt, Fribourg, Geneva, Neuchâtel,
Nidwalden, Schaffhausen, Thurgau, Zug and Zurich: `SwissAssistedNotification.Covers`) CasaZen prepares what the host needs and watches the deadline, and **transmits nothing** (decision D1): the host copies the data onto the
official portal of the canton (or sends the paper, the e-mail, the way the canton asks) and **declares the day notified**. A canton that only asks the host to keep a register is the register's (E31/F01), whatever the
host says; Ticino keeps its own flow (§ 1-20) and never appears here. The whole of it is behind **`Features:GuestRegistrationRegimes`** (off by default); the alerts need `Features:TicinoNotificationAlerts` as well.

- **Which days are raised.** A day is listed (cockpit, alerts) only when **all** of this holds: the flag is on; the property is Swiss and the one resolver (`IGuestRegistrationRegimeResolver`, § 21) puts it under a covered
  regime; its **effective duty is `Required`** (the host said so with the setting of § 21, or the regime is verified as covering private rentals: today Schaffhausen and Zug), never `Ask` or `NotRequired`; and a stay arrived
  on a day of the last 14 that is not declared (a stay owes a notification when its status is confirmed, checked in or checked out and it is not a no-show: `AlloggiatiStatusRules`, the one definition). An uncertain canton is
  `Ask`: nothing in red until the host answers. `ISwissNotificationService.ListPendingDaysAsync` is **the one place** that decides it; the cockpit and the alerts read nothing else.
- **The cockpit.** `GET api/compliance/summary` has a section `swissNotifications` (the action `NotifyGuestsAuthority`, the property as target, the arrival day `yyyy-MM-dd` in `period`, the deadline in `dueAt`, `null`
  where the regime has none), oldest first, like `ticinoNotifications`. It is not an item of the traffic light and not in the Home «Da fare» (`ComplianceActivityFeeder.NotFedActions`,
  `HostTodoPriority.CockpitActionsOutsideTheDay`); it is filled whatever `ComplianceTrafficLight`.
- **The deadline** (`SwissAssistedNotification.DueAtUtc`, on the clock of the property, `Property.Timezone`; a deadline is never guessed):

  | Kind (regimes) | Due |
  |---|---|
  | `AtArrival` (Geneva, Zug) | the arrival moment: the first arrival recorded among the stays of the day, else the end of the arrival day |
  | `DailyByTime` (Schaffhausen) | the day after the arrival at the time of the regime: Schaffhausen 02:00 (`GuestRegistrationRegime.DeadlineTime`; the brochure does not say which day the time refers to, and the note of the card says so) |
  | `DailyByTime` with no time in the source (Basel-Stadt, Neuchâtel: «every day», no cut-off) | none: `DeadlineTime` is empty, so, as for `None`, the day is listed without `dueAt` and **no alert is sent**; the end of the arrival day is **not** taken as a deadline (it would be a time nobody wrote) |
  | `Weekly` (Nidwalden) | Sunday 23:59 of the arrival week |
  | `None` (Fribourg, Thurgau, Zurich) | none: the day is listed without `dueAt` and **no alert is sent** |
  | `Hours24FromArrival` | not used here (Ticino and Italy have their own flows); the rule is the one of Ticino, so it is total |

- **The day** (`GET api/properties/{id}/swiss/notification-day?date=`, `guest.read`, row check, never cached): the guests of the stays that arrive that day **as data ready to copy** (`bookingCode`, `arrival`,
  `departure`, `firstName`, `lastName`, `birthDate`, `placeOfBirth`, `nationality` as entered and `nationalityCode` ISO alpha-3 where known, `documentType` `passport` / `identityCard` / `other`, `documentNumber`, `street`,
  `postalCode`, `city`, `residenceCountryCode`, `plate`; a field not recorded is `null`), `issues` (what lacks, with the codes of the Ticino day: every guest needs a first name, a last name, a date of birth and a
  nationality, an adult or a guest of unknown age also a document type, a stay with fewer guests than the booking counts lacks `guests`), the portal (`portalUrl`), the `deadlineKind`, its `deadlineNote`, the
  `certainty` of the regime, the `effectiveDuty`, `dueAt`, who declared the day and when, and `pendingDays`. A guest with a gap stays in the list: the host types what is there and completes the rest. **Never in it**:
  the tourist tax exemption (health-related data), a photograph of the document (D6), the contact of the booker. Without `date` the day is the oldest to notify, or today. It works for a property whose duty is `Ask` or
  `NotRequired` too (it says so in `effectiveDuty`) but raises nothing; 422 `swiss_notification_not_applicable` for a property that is not covered (not Swiss, Ticino, a register-only canton, an unknown place).
- **The declaration.** `POST api/properties/{id}/swiss/notification-declare` `{ "arrivalDay": "2026-11-19" }` (`alloggiati.submit`: the owner and the property managers) closes the day and `DELETE …?arrivalDay=` reopens it,
  with the rules of § 14-15: idempotent, no day in the future (422 `swiss_declaration_day_in_future`), no guest data in the answer, `private, no-store`. The row is a `SwissNotificationDeclaration` with `RegimeCode` =
  the code of the regime (`CH-SH`) and `Channel = assisted`: **no migration** (§ 19). One declaration for each property, day and regime, so a declaration made under another canton (the property changed municipality)
  or of the Ticino police closes nothing here. The other hosts of the property are told live (`swiss-notification-declared`, route `/properties`, the property as `entityId`, `TicinoDeclarationLiveRefresh.TellSwiss`,
  best effort after the row is saved, listed in the one legacy entry of `HostNotifyingPathsArchitectureTests`).
- **The alerts** (`ComplianceAlertType.SwissNotification` = 5, `ComplianceAlertService.Swiss.cs`, the hourly `stay-alerts` job, flag `TicinoNotificationAlerts`): the **three stages of § 9 against the deadline of the
  regime**, once each (state row of `ComplianceAlertStates` with the arrival day as reference, the type tells it from the Ticino rows), `ComplianceAlertSchedule.DueSwissStep`, which is `DueTicinoStep` with the Swiss type and
  kinds: the arrival day (two hours after the first expected check-in, else 18:00, never after 23:00), six hours before the deadline, the deadline, then at most two reminders 24 hours apart. The thresholds are the **same
  five options** `Compliance:Ticino*` (§ 11): no new configuration. Where the deadline is the arrival (`AtArrival`) a day whose arrival is recorded is overdue from that moment, so its first alert is the overdue one, at
  the first hourly run after it. Push types `swiss-notification-due` and `swiss-notification-overdue`, the same audience, channels, privacy and bell entry as § 10 (the property as `entityId`); e-mail to the contact address of
  the org; the text names the property, the day, the deadline and the types of field that miss, **never a guest**, and says «autorità cantonale» (no supplier is named). **Italian and English only**
  (`SwissNotification*` of `EmailTexts.resx` and `.en.resx`): `EmailTextsParityTests` allows the guest languages to hold only the guest check-in texts, and a host alert is never sent in them. No «To do», no popup.
- **Checking.** `SELECT "PropertyId", "ReferenceKey", "Stage", "AlertCount", "LastAlertAt" FROM casazen_test."ComplianceAlertStates" WHERE "Type" = 5 ORDER BY "UpdatedAt" DESC LIMIT 50;` and the declarations with
  `"RegimeCode" IS NOT NULL` (§ 20). The bell entries: `"Type" IN ('swiss-notification-due', 'swiss-notification-overdue', 'swiss-notification-declared')`.
- **Rolling back.** Turn `GuestRegistrationRegimes` off: the routes answer 404, the section is empty and the alerts stop (the service lists no day); nothing else changes, no migration to undo. To make a day to notify again
  (a wrong declaration nobody can undo): `DELETE FROM casazen_test."SwissNotificationDeclarations" WHERE "PropertyId" = '<id>' AND "ArrivalDay" = 'yyyy-MM-dd' AND "RegimeCode" = 'CH-SH';`. To send a stage again, delete the
  row of the day in `ComplianceAlertStates` (type 5).
- **Code**: `Casazen.Core/Regulatory/GuestRegistration/SwissAssistedNotification.cs` (which regimes, the deadlines, the guests and what they lack), `Casazen.Core/Services/ISwissNotificationService.cs`,
  `ComplianceAlertSchedule.Swiss.cs`, `Casazen.Infrastructure/Services/SwissNotificationService.cs`, `ComplianceAlertService.Swiss.cs`, `ComplianceWizardService.cs` (the section),
  `GuestRegistrationRegimeResolver.cs` (`ResolveManyAsync`: the regime of many properties with at most two queries), `Casazen.Infrastructure/Email/Templates/EmailTemplates.SwissNotification.cs`,
  `Casazen.Web/Controllers/SwissNotificationController.cs` (a controller of its own on the route of `SwissComplianceController`, behind the flag at class level) and `Casazen.Web/DTOs/SwissCompliance/SwissNotificationDtos.cs`.
  **Tests**: `SwissAssistedNotificationTests` (the regimes, the deadlines on the clock of the property, the plan of the guests), `SwissNotificationServiceTests` (`Assisted_AskOrNotRequired_NeverRaisesItem_Test`,
  `Assisted_Ticino_NotInThisSection_Test`, the closed day, the flag, isolation), `SwissNotificationControllerTests` (`Assisted_OtherOrganizationForbidden_Test`, the permissions, the flag gate), `ComplianceAlertSwissTests`,
  `ComplianceWizardSwissNotificationsTests` (the section, the reach of a collaborator), `GuestRegistrationRegimeResolverManyTests` and the tests that list the cockpit actions, the alert types, the push types and
  the registrations.
