# Runbook: in-app notifications (the bell of the shell)

Task UI-12a (redesign wave, `gap/01` §4 and F12). Backend only: the entity, the job that writes it next to every push, the four
endpoints, the retention and the flag `InAppNotifications`. The bell and the drawer are UI-12b (frontend), which starts after this
is merged. The flag is **off by default**.

## 1. What it is

Until UI-12a the host and the supplier were told about an event by e-mail and by push only (`IPushNotificationService`, MO-04); the
"Dashboard" channel (`DashboardNotificationChannel`) is a stub that logs. The bell lists, for each user, what the phone was told.

```
service (BookingNotifier, ServiceRequestNotifier, ShowcaseBookingNotifier, NotificationService)
  └─ IPushNotificationService.Enqueue(deliveryKey, audience, payload)          ← the callers do not change
       └─ InAppNotificationPushDecorator
            ├─ HangfirePushQueue → PushDeliveryJob          (devices of the audience → Expo)      [always]
            └─ InAppNotificationJob.CreateAsync(key, …)     (users of the audience → InAppNotifications) [flag on]
```

* **No call site changes.** `AddCasazenPush` registers `IPushNotificationService` as the decorator over `HangfirePushQueue`
  (`Casazen.Web/Extensions/PushServiceCollectionExtensions.cs`). With the flag off the decorator only forwards: no job, no read.
* **The user is not known where the push is decided.** The audience of a push is a booking, a property or a supplier org
  (`PushAudience`); `PushDeliveryJob` turns it into devices and `InAppNotificationJob` turns it into **users**, when it runs, never
  inside the request. A user with no registered device still gets the notification: the bell does not depend on a phone.
* **Who is told** is the same rule as the push (`HostNotificationAudience` for the hosts of a booking or a property: the member in
  charge and the org's administrators, active and not deactivated; the active users linked to the supplier org
  for the supplier's inbox). An audience with nobody in it (a booking that is gone, every member deactivated) writes nothing and is
  not an error.
* **The notification does not depend on the push being queued**: a push refused for its route, or a Hangfire that took one job and
  failed the other, leave the other alone. The answer of `Enqueue` is always the push's.

## 2. The data (`InAppNotifications`)

| Column | Meaning |
|---|---|
| `Id`, `UserId` | The notification and the user who is told (`Users.Id`, the Auth0 subject). **Foreign key to `Users` `ON DELETE CASCADE`** |
| `OrgId` | The org the event belongs to: the host org of the booking or property, or the **supplier org** for the supplier's inbox. `ITenantOwned`. **Foreign key to `Orgs` `ON DELETE CASCADE`** |
| `Type` | A value of `PushTypes` (`new-booking`, `service-request-completed`, …), at most 64 characters |
| `EntityId` | The **service request** for the `service-request-*` types, the **booking** for every other one; `null` if the event names none. Not a foreign key |
| `DeliveryKey` | The key of the push of the same event (`PushDeliveryKeys`), at most 200 characters. Never sent to a client |
| `CreatedAt`, `ReadAt` | UTC instants (whole microseconds). `CreatedAt` is the instant of the event (taken when the push was queued, so a late run or a retry does not move it); `ReadAt` is `NULL` while unread |

**No text and no name.** There is no title, no body, no guest or property name: the client writes the sentence from `Type`, in the
language of the user, and builds the link from `Type` and `EntityId`. A test (`Row_HasNoFreeText_…`) fails if a column is added.

Indexes: `UIX_InAppNotifications_DeliveryKey_UserId` (**unique**: once per event and user), `IX_InAppNotifications_UserId_ReadAt_CreatedAt`
(the unread count and the list), `IX_InAppNotifications_CreatedAt` (the retention), `IX_InAppNotifications_OrgId`. The migration
`AddInAppNotifications` only creates the table; it adds nothing to existing tables.

**Once per event and user.** A retry of the job (Hangfire), the same event queued twice, a manual *Requeue*: a user who already has a
row for the key is skipped. Two runs that write the same user at the same instant lose the race on the unique index (23505), read again
and write what is still missing (at most three rounds, then Hangfire retries). Runs of the same key never overlap
(`[DisableConcurrentExecution]` on the key). Where two pushes share a key (the host's "completed" and the supplier's "paid" both use
`service-request:{id}:Completato`), a person in both audiences gets one of them, exactly as a device gets one push per key.

### Types

| `Type` | Told | `EntityId` |
|---|---|---|
| `new-booking`, `ota-stay-review` | hosts of the booking | booking |
| `guest-data-missing`, `alloggiati-deadline`, `alloggiati-overdue`, `alloggiati-failed`, `checkout-reminder` | hosts of the booking | booking |
| `service-request-taken`, `-started`, `-completed`, `-rejected`, `-time-proposed` | hosts of the property | service request |
| `service-request-cancelled` | the other party: the hosts of the property when the supplier cancels, the supplier org when the host or the customer of its showcase cancels; both when nobody answered in time | service request |
| `service-request-created`, `-paid`, `-reminder`, `-proposal-accepted`, `-proposal-rejected`, `-rescheduled` | the supplier org | service request |

A new value of `PushTypes` gives a notification with no change here; the client shows a generic sentence for a type it does not know.

## 3. The API

All four are behind `[FeatureGate(InAppNotifications)]` (404 `not_found` **before authentication** with the flag off) and
`[Authorize(Policy = Authenticated)]` (every context has a bell; the user is always the token's `sub`, never a parameter or a body
field). Answers are `Cache-Control: private, no-store`.

| Method | Path | Answer |
|---|---|---|
| `GET` | `/api/me/notifications?unread=false&page=1&pageSize=20` | `{ items, totalCount, page, pageSize }`, newest first (`CreatedAt`, then `Id`, descending); `pageSize` is held between 1 and 50, `page` at 1 or more. An item is `{ id, type, entityId, createdAt, readAt }` |
| `GET` | `/api/me/notifications/unread-count` | `{ count }` |
| `POST` | `/api/me/notifications/{id}/read` | 204 (also when it was read already: idempotent, the first `ReadAt` stays); **404** for the notification of another user, of an org the caller does not belong to, deleted by the retention or that never existed: the same answer, never 403 |
| `POST` | `/api/me/notifications/read-all` | 204 (also when there is nothing to mark) |

**Whose rows** (`InAppNotificationService.OwnNotifications`, the one place that says it): the rows of the caller whose `OrgId` is the
caller's host org (`User.OrgId`) or its supplier org (`User.SupplierOrgId`), read from the user's row in the same statement. The
tenant filter is bypassed there on purpose: it knows the host org only, and a supplier-only account has none (every tenant-filtered
table is empty for it). A user who has left an org no longer reads what happened there; its rows wait for the retention. A user with
both roles reads both in one list.

**Updates are single SQL statements** (`ExecuteUpdate`, conditioned on `ReadAt IS NULL`): no tracked row to lose a race on, nothing to
fail if the retention deletes the row meanwhile.

**Rate limit.** None of its own, like the other `api/me` endpoints (`RateLimitPolicies` are the policies of the anonymous endpoints, per
client IP): every call is an indexed read or one update of the caller's rows, `pageSize` is bounded, and the answers are small. If the
polling of the bell ever weighs on the API, add a per-user limit (the pattern is `AiRequestRateLimiter`), not a per-IP one: an office
behind one address would share it.

## 4. Jobs

| Job | When | What |
|---|---|---|
| `InAppNotificationJob.CreateAsync(key, notification)` | Queued by the decorator next to every push, with the flag on | Resolves the users of the audience and writes one row per user for the key. `[AutomaticRetry(Attempts = 5)]`, deleted after the last attempt; the arguments hold the audience, the type, the entity id and the instant, never the push text. Skips quietly (and says so in the log) if the flag was turned off meanwhile |
| `in-app-notification-retention` (`InAppNotificationRetentionJob`) | Every day at 03:45 UTC | Deletes the notifications **older than 90 days, read or not** (one `ExecuteDelete`). **Registered whatever the flag says**: rows written while it was on are purged after it is turned off; with it off the run finds nothing. See [hangfire.md §15](hangfire.md#15-in-app-notifications-ui-12a) |

Logs carry the key, the type, the audience and counts: `In-app notification <type> queued for <audience> <id> (key …)`, then
`… written for N users of …` or `… the N users were told already` or `… nobody to tell for …`; `… written concurrently or a user is gone:
read again` when it lost a race. **No user id and no push text** in any of them.

## 5. GDPR

A row is a user id, a kind of event and the id of a booking or service request: no name, no text, no address. It is still data about a
person, so:

* **Retention**: 90 days from the event (`InAppNotificationLimits.RetentionDays`), read or not, by the nightly job above.
* **Erasure of the account**: the foreign key to `Users` cascades, like `OrgMembers` and `PropertyMemberAccesses`
  (`gdpr.md` §9): deleting the user deletes its notifications. Nothing in the code deletes an account today; the test
  `DeletingTheUser_TakesItsNotificationsWithIt_NoOneElses` proves the key.
* **Erasure of the org**: the foreign key to `Orgs` cascades. The only place an org is deleted is the merge of two duplicate supplier orgs
  (`SupplierService.RemoveDuplicateOrgAsync`); a restricting key there would have kept the duplicate silently. An org with people cannot be
  deleted at all (`FK_Users_Orgs_OrgId`), so `DeletingTheOrg_…` is proved on an org nobody belongs to.
* **A member who leaves the org** (AM-02): its notifications of that org are not reachable any more and go with the retention.
* The org export (`GET /api/gdpr/org/export`) does not list them: it is the fiscal export of the org, and the notifications are the
  user's, not the org's.
* The empty-org check of the invitations (`OrgEmptinessChecker`) reads every tenant table of the model: an org with a notification in
  it is "in use", like one with a booking. A person who never did anything has none.

## 6. Turning it on, checking, rolling back

* **On** (test first): `Features__InAppNotifications=true` on the API. Needs nothing else. The frontend reads `inAppNotifications` from
  `GET /api/public/features`; until UI-12b ships the bell, turning it on only starts filling the table.
* **Check** (SQL editor, replace the schema):

```sql
-- Notifications written in the last 24 hours per type
SELECT "Type", count(*) FROM casazen_test."InAppNotifications"
WHERE "CreatedAt" > now() - interval '24 hours' GROUP BY 1 ORDER BY 2 DESC;

-- Unread per user (the top ones): a user who never opens the bell
SELECT "UserId", count(*) FROM casazen_test."InAppNotifications"
WHERE "ReadAt" IS NULL GROUP BY 1 ORDER BY 2 DESC LIMIT 10;

-- Any user told twice for the same event? (the unique index makes this impossible: it must be empty)
SELECT "DeliveryKey", "UserId", count(*) FROM casazen_test."InAppNotifications"
GROUP BY 1, 2 HAVING count(*) > 1;

-- Notification jobs that failed (they are deleted after 5 attempts)
SELECT id, statename, createdat FROM hangfire_casazen_test.job
WHERE invocationdata ->> 'Type' LIKE 'Casazen.Infrastructure.Push.InAppNotificationJob%'
  AND statename IN ('Failed', 'Scheduled', 'Enqueued') ORDER BY createdat DESC LIMIT 20;
```

* **Roll back**: set `Features__InAppNotifications=false` (or delete the variable) and redeploy. From then on the decorator only
  forwards the push, the endpoints answer 404 and the jobs already queued skip. Rows already written are harmless (user id, kind of
  event, id) and the retention removes them within 90 days; to remove them now, after the flag is off:
  `DELETE FROM casazen_test."InAppNotifications";`. The migration needs no rollback (an empty table nobody reads).
  Nothing else was changed in the existing tables or in the call sites.

## 7. For UI-12b (the bell and the drawer)

* Show the bell only with `flags.inAppNotifications` (and `enabled:` on the queries so nothing is requested while it is off).
* Poll `GET …/unread-count` with `refetchInterval` (the answer is small and cheap); fetch the list when the drawer opens; after
  `POST …/read` or `…/read-all` invalidate both. A `404` on `read` means the retention or another tab got there first: refetch.
* The text comes from `type` (i18n keys per type, plus a generic one for a type the client does not know), the link from `type` and
  `entityId` (a booking for the stay types, a service request for the `service-request-*` ones). Dates are UTC; `readAt: null` is unread.
* The list is paged by `page`/`pageSize` (newest first). New notifications shift the pages: ask for page 1 again after a poll that
  shows a higher count rather than extending the old pages.

## 8. Code and tests

`Casazen.Core/Entities/InAppNotification.cs` (+ `InAppNotificationLimits`), `Casazen.Core/Services/IInAppNotificationService.cs`,
`Casazen.Infrastructure/Push/{InAppNotificationPushDecorator,InAppNotificationJob}.cs`,
`Casazen.Infrastructure/Services/InAppNotificationService.cs`, `Casazen.Web/Controllers/MeNotificationsController.cs`,
`Casazen.Web/BackgroundJobs/InAppNotificationRetentionJob.cs`, migration `AddInAppNotifications`.
Tests: `InAppNotificationPushDecoratorTests`, `InAppNotificationJobTests`, `InAppNotificationServiceTests`,
`MeNotificationsControllerTests`, `InAppNotificationRetentionJobTests`, `InAppNotificationsFlagTests`,
`InAppNotificationSqlShapeTests`, `InAppNotificationsIntegrationTests` (HTTP, flag on and off, the chain from a push to the
notification), and on PostgreSQL `InAppNotificationsPostgresTests` (schema, the race on the unique index, the single-statement
updates and the retention, whose rows a user reaches, the cascades). The architecture tests list the entity
(`TenantQueryFilterArchitectureTests`) and the four actions (`EndpointAuthorizationArchitectureTests`).
