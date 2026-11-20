# Runbook: the guests of Switzerland (Ticino): the 24 hours of the notification

Task E30/F02/US01/T01 (NF-127, decision D9 of `alloggiati-ch/analisi-tecnica.md`, casazen/backend#979, «Ticino and Switzerland in general»). The police of canton Ticino
want every guest notified **within 24 hours of the arrival**. The portal (`notifiche.ti.ch`) has no API and CasaZen never logs into it, never keeps a password or the seed
of a one-time code and never simulates a browser (D1): CasaZen writes the file (`TicinoPoliceFile`, [ticino-notification-format.md](../compliance/ticino-notification-format.md)),
the host uploads it and records the receipt. This runbook is the part that **watches the 24 hours**: the host is told in time, once for each stage, and no more when the day
is closed.

## 1. What it is

The third kind of alert of `ComplianceAlertService` (the one of the ISTAT and tourist tax periods and of the connection to the channel manager, E03/F02/US02/T01), run by the
same hourly `stay-alerts` job ([hangfire.md § 9](hangfire.md#9-stay-alerts-co-10)). It reuses the staged state `ComplianceAlertState`, the compare-and-set that makes a stage
go out once, the e-mail templates, the push chain and the bell with its live update ([in-app-notifications.md](in-app-notifications.md) § 9-10). The placeholder
`TicinoReminderJob` (a TODO that was never registered) no longer exists; `StayAlertService` stays Italian only.

Behind its own flag, **`Features:TicinoNotificationAlerts`, off by default** ([feature-flags.md](feature-flags.md)): off, the job reads no Ticino day, writes no row and
queues nothing. It does not need `Features:ComplianceTrafficLight`; with both off the whole run does nothing.

## 2. Which days, which stages

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

## 3. Who is told, how, and what it says

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

## 4. Configuration

| Variable | Default | Meaning |
|---|---|---|
| `Features__TicinoNotificationAlerts` | `false` | The flag |
| `Compliance__TicinoArrivalAlertDelayHours` | `2` | Hours after the first expected check-in for stage 1 |
| `Compliance__TicinoArrivalAlertDefaultHourLocal` | `18` | Hour (0-23) of stage 1 when no stay of the day has an expected check-in |
| `Compliance__TicinoDueWarningHours` | `6` | Hours before the deadline for stage 2 |
| `Compliance__TicinoMaxOverdueReminders` | `2` | Reminders after «overdue» (`0` = none) |
| `Compliance__TicinoOverdueReminderHours` | `24` | Hours between them (at least 1) |

They are proposals the product owner may change (listed in [deploy-checklist.md](deploy-checklist.md) § 2). A value out of its range is held to it, never an error.

## 5. Checking, rolling back

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

## 6. Code and tests

`Casazen.Core/Entities/ComplianceAlertState.cs` (`ComplianceAlertType.TicinoNotification = 4`), `Casazen.Core/Services/IComplianceAlertService.cs` (`ComplianceAlert`,
`ComplianceAlertKind`, `ComplianceAlertStage`), `Casazen.Core/Services/ComplianceAlertSchedule.Ticino.cs` (the rules), `Casazen.Core/Options/ComplianceOptions.cs`,
`Casazen.Core/Features/FeatureFlags.cs`, `Casazen.Core/Services/IPushNotificationService.cs` (`PushTypes.TicinoNotificationDue|Overdue`),
`Casazen.Infrastructure/Services/ComplianceAlertService.Ticino.cs` (the run), `Casazen.Infrastructure/Services/NotificationService.cs`,
`Casazen.Infrastructure/Email/Templates/EmailTemplates.TicinoNotification.cs`. Tests: `ComplianceAlertTicinoTests` (the schedule with the two daylight saving changes, each stage once,
a closed day stops the alerts, another org is never alerted, flag off sends nothing, the payload holds no personal data, the registration with `ValidateScopes`, the enumerations of
`ComplianceAlertType` and `PushTypes`).
