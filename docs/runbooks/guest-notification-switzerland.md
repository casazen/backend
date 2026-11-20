# Runbook: notification of the guests to the police in Switzerland (Ticino)

**Issue:** casazen/backend#979 · **Requirements:** NF-126 (complete Ticino file), NF-127 (the 24 hours: staged alerts), NF-128 (retention), NF-132 (Swiss data at the check-in) · **Format of the file:**
[`docs/compliance/ticino-notification-format.md`](../compliance/ticino-notification-format.md) · **Analysis:** `alloggiati-ch/analisi-tecnica.md`
(decisions D1-D16) and `alloggiati-ch/specifica-ticino.md` (verified rules).

The police of canton Ticino want every guest notified **within 24 hours of the arrival**. The portal (`notifiche.ti.ch`) has no API and CasaZen never logs into it,
never keeps a password or the seed of a one-time code and never simulates a browser (decision D1): CasaZen writes the file, the host uploads it and records the
receipt. Sections 1-7 are the file, its data, the groups and the minors, and the retention of the documents (E30/F01); sections 8-13 are the staged alerts that
watch the 24 hours (E30/F02/US01/T01); sections 14-17 are the regimes of Italy and of the 26 cantons, the card of a property and the readiness of a Swiss
property (E29/F02/US01), with the catalog in [`docs/compliance/guest-registration-switzerland.md`](../compliance/guest-registration-switzerland.md).

## 1. The flow

1. A property is in canton Ticino when its country is `CH` and its Swiss municipality belongs to canton `TI`
   (`ITicinoNotificationService.IsPropertyInTicinoAsync`, the one check of the jurisdiction).
2. The guests fill the online check-in (or the host fills the guests in the wizard). For a Ticino property the check-in asks, on top of
   the Alloggiati record: the **document type and number of every adult** (family and group members included; every minor of a stay
   without adults; in a group of 6 or more only the leader, § 5), the **home address** (street, postal code, town) and the state of residence of
   each guest (optional), and the **plate
   of the car** once for the stay (optional). The public context says it with `requiredGuestFields` and `askVehiclePlate`.
3. Within 24 hours of the arrival the host opens the day (`GET api/properties/{id}/swiss/ticino-notification?date=`), sees who is in the
   file and what the others miss, and downloads the file (`POST …/ticino-notification`, CSV by default). Nothing is stored. The alerts that tell the host the day is still open are § 8-13.
4. The host uploads the file on <https://www.notifiche.ti.ch> («Notifiche → Invio», login and one-time code: CasaZen never logs in,
   decision D1) and records the receipt of the portal (`POST …/upload-receipt`, type `Police`, report date = the arrival day).

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
and no no-show) of the Ticino properties (country `CH` and a municipality of canton `TI`) that no receipt of the portal closes. **That service is the one place that says a day
is closed**: when the declaration «I uploaded the file» (E30/F02/US02) enters it, the alerts respect it with no change here. Before a stage is claimed the detail of the day is
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

## 14. The regime of a property in any canton (E29/F02/US01/T01, NF-125)

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
- **`identifierMissing`.** The reasons of § 15, the same codes the activation and the onboarding part use; empty for an Italian property.
- **No supplier name** in the texts of the card (the keys `GuestRegime_*` of `SharedResources`, Italian and English); the sources are the pages of the authorities.

## 15. The readiness of a Swiss property (E29/F02/US01/T02, NF-129): no flag

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

## 16. Checking and rolling back

```sql
-- The properties whose host has set the notification of the guests (replace the schema)
SELECT "GuestNotificationOverride", count(*) FROM casazen_test."Properties" WHERE "GuestNotificationOverride" IS NOT NULL GROUP BY 1;
```

- **Turn it on (test only, on the product owner's order)**: `Features__GuestRegistrationRegimes=true` on the `test` environment (the change redeploys).
- **Roll back**: set the flag off (or unset it): the endpoints answer 404 and the settings stop being read; the column stays, harmless. The readiness (§ 15) is not behind the flag: it is a step of the activation, a reminder
  for an active property.
- **Migration** `AddPropertyGuestNotificationOverride`: a nullable integer `GuestNotificationOverride` on `Properties` (1 = `Required`, 2 = `NotRequired`) with the CHECK `CK_Properties_GuestNotificationOverride`
  (null or one of the two); every existing row stays null. Down: drops both.

## 17. Code and tests

`Casazen.Core/Regulatory/GuestRegistration/` (`GuestRegistrationRegime`, `GuestRegistrationRegimeCatalog`, `GuestRegistrationDutyRules`, `GuestRegistrationReadiness`), `Casazen.Core/Enums/GuestNotificationOverride.cs`,
`Casazen.Core/Services/IGuestRegistrationRegimeResolver.cs`, `Casazen.Infrastructure/Services/` (`GuestRegistrationRegimeResolver`, `GuestNotificationOverrideService`, `GuestRegistrationPartEvaluator`,
`PropertyComplianceStatusService` and `ComplianceWizardService` for § 15), `Casazen.Web/Controllers/PropertyGuestRegistrationController.cs`. Tests: `GuestRegistrationRegimeCatalogTests` (every canton of the municipality directory is in
the catalog with an obligation, a channel, a certainty, a date and a source; Ticino, Appenzell Innerrhoden and Italy as above; the sentences in both languages; no supplier name), `GuestRegistrationRegimeResolverTests`
(Lugano, the unknown place, Italy, the effective-duty table, the flag, the tenant), `PropertyGuestRegistrationControllerTests` (another org, a collaborator «Solo alcuni», the missing member), `GuestRegistrationPartEvaluatorTests`,
`PropertyComplianceStatusServiceTests` (the Swiss step).
