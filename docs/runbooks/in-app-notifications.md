# Runbook: in-app notifications (the bell of the shell)

Task UI-12a (redesign wave, `gap/01` §4 and F12). Backend only: the entity, the job that writes it next to every push, the four
endpoints and the retention; later, the real-time channel (SignalR, § 9) and the notification of the guest messages (§ 10). The bell
and the drawer are UI-12b (frontend). There is no switch: everything below is always on.

## 1. What it is

Until UI-12a the host and the supplier were told about an event by e-mail and by push only (`IPushNotificationService`, MO-04); the
"Dashboard" channel (`DashboardNotificationChannel`) is a stub that logs. The bell lists, for each user, what the phone was told.

```
service (BookingNotifier, ServiceRequestNotifier, ShowcaseBookingNotifier, NotificationService)
  └─ IPushNotificationService.Enqueue(deliveryKey, audience, payload)          ← the callers do not change
       └─ InAppNotificationPushDecorator
            ├─ HangfirePushQueue → PushDeliveryJob          (devices of the audience → Expo)
            └─ InAppNotificationJob.CreateAsync(key, …)     (users of the audience → InAppNotifications)
```

* **No call site changes.** `AddCasazenPush` registers `IPushNotificationService` as the decorator over `HangfirePushQueue`
  (`Casazen.Web/Extensions/PushServiceCollectionExtensions.cs`).
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
| `guest-message` | hosts of the booking (every message of the guest) | booking |
| `guest-data-missing`, `alloggiati-deadline`, `alloggiati-overdue`, `alloggiati-failed`, `checkout-reminder` | hosts of the booking | booking |
| `service-request-taken`, `-started`, `-completed`, `-rejected`, `-time-proposed` | hosts of the property | service request |
| `service-request-cancelled` | the other party: the hosts of the property when the supplier cancels, the supplier org when the host or the customer of its showcase cancels; both when nobody answered in time | service request |
| `service-request-created`, `-paid`, `-reminder`, `-proposal-accepted`, `-proposal-rejected`, `-rescheduled` | the supplier org | service request |

A new value of `PushTypes` gives a notification with no change here; the client shows a generic sentence for a type it does not know.

## 3. The API

All four are behind `[Authorize(Policy = Authenticated)]` (every context has a bell; the user is always the token's `sub`, never a
parameter or a body field). Answers are `Cache-Control: private, no-store`.

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
| `InAppNotificationJob.CreateAsync(key, notification)` | Queued by the decorator next to every push | Resolves the users of the audience and writes one row per user for the key. `[AutomaticRetry(Attempts = 5)]`, deleted after the last attempt; the arguments hold the audience, the type, the entity id and the instant, never the push text |
| `in-app-notification-retention` (`InAppNotificationRetentionJob`) | Every day at 03:45 UTC | Deletes the notifications **older than 90 days, read or not** (one `ExecuteDelete`). See [hangfire.md §15](hangfire.md#15-in-app-notifications-ui-12a) |

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

## 6. Checking, rolling back

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

* **Roll back**: redeploy the previous version of the application. From then on the push goes out alone, the endpoints no longer
  exist and a job already queued for the bell may show as failed in the Hangfire dashboard (delete it there). Rows already written
  are harmless (user id, kind of event, id) and the retention removes them within 90 days while this version runs; to remove them
  now: `DELETE FROM casazen_test."InAppNotifications";`. The migration needs no rollback (a table nobody reads).
  Nothing else was changed in the existing tables or in the call sites.

## 7. For UI-12b (the bell and the drawer)

* Show the bell in every context (there is no flag to read).
* Poll `GET …/unread-count` with `refetchInterval` (the answer is small and cheap); fetch the list when the drawer opens; after
  `POST …/read` or `…/read-all` invalidate both. A `404` on `read` means the retention or another tab got there first: refetch.
* The text comes from `type` (i18n keys per type, plus a generic one for a type the client does not know), the link from `type` and
  `entityId` (a booking for the stay types, a service request for the `service-request-*` ones). Dates are UTC; `readAt: null` is unread.
* The list is paged by `page`/`pageSize` (newest first). New notifications shift the pages: ask for page 1 again after a poll that
  shows a higher count rather than extending the old pages.

## 8. Type to screen (what a client opens)

The row carries `type` and `entityId` only; the client derives the screen. The **mobile route** is the one the push carries in
`data.route` (`PushRoutes`, `mobile/src/notifications/notification-routes.ts` keeps the same list). The **web route** is the host app
route of the same screen (the links the e-mails use are built in `PublicSiteLinks`).

| `type` | `entityId` is | Mobile route (`data.route`) | Web route |
|---|---|---|---|
| `new-booking`, `ota-stay-review`, `guest-data-missing`, `alloggiati-deadline`, `alloggiati-overdue`, `alloggiati-failed` | booking | `/bookings/{bookingId}` | `/app/short-rent/bookings/{bookingId}` |
| `checkout-reminder` | booking | `/bookings/{bookingId}/checkout` | `/app/short-rent/bookings/{bookingId}` |
| `guest-message` | booking | `/bookings/{bookingId}/messages` | `/app/short-rent/bookings/{bookingId}?tab=messages` |
| `service-request-taken`, `-started`, `-completed`, `-rejected`, `-time-proposed`, `-cancelled` (host side) | service request | `/bookings/{bookingId}` when the request has a booking, else `/properties` | the service request of the host (by `entityId`) |
| `service-request-created`, `-auto-accepted`, `-paid`, `-reminder`, `-proposal-accepted`, `-proposal-rejected`, `-rescheduled`, `-cancelled` (supplier side) | service request | `/properties` | supplier inbox, `/app/supplier/inbox` (the request by `entityId`) |

Notes: an in-app row has no booking id for the `service-request-*` types (its `entityId` is the **service request**); the booking
of the request is only in the push. A client that needs it reads the request. A type the client does not know opens the list of
notifications. `PushRoutes.IsAppRoute` accepts `/properties`, `/bookings/{guid}`, `/bookings/{guid}/checkout` and
`/bookings/{guid}/messages`; the mobile app must have the last one before the push is sent to its users (an old app ignores a route it
does not know).

## 9. Real time (SignalR)

The bell no longer has to poll: the server tells the open sessions of a user when their unread count changes.

| | |
|---|---|
| Hub | `NotificationsHub`, `/hubs/notifications` (WebSockets, falling back to server-sent events and long polling) |
| Auth | `[Authorize(Policy = Authenticated)]`, the same Auth0 JWT as the API. Browsers cannot set a header on a WebSocket or SSE request, so for paths under `/hubs` the token is also read from `?access_token=` (`JwtBearerEvents.OnMessageReceived`, `HubAccessToken`); anywhere else a token in the query is ignored. Without a token the negotiate request is **401** |
| Group | one per user: `user:{sub}`, with `sub` as `User.GetUserId()` in `MeNotificationsController`. A connection joins its own group only; the hub has no method a client can call |
| Server → client | method **`notificationsChanged`**, one argument, camelCase JSON: `{ "unreadCount": 3, "type": "guest-message", "entityId": "<guid>" }`. `type` and `entityId` are `null` when only the count changed (read, read all). No text, no name |
| CORS | the configured origins, now with `Access-Control-Allow-Credentials: true` **for the paths under `/hubs` only** (the SignalR client negotiates with credentials), and the header `X-SignalR-User-Agent` allowed. The API stays without credentials |
| Caching, compression, rate limit | None is registered for the app (the rate limiter has named per-endpoint policies only, and the hub has none), and the hub answers set no `Cache-Control` of their own, so nothing breaks the WebSocket |

Client (JS): `new HubConnectionBuilder().withUrl(apiBase + '/hubs/notifications', { accessTokenFactory: () => token }).withAutomaticReconnect().build()`,
then `connection.on('notificationsChanged', ({ unreadCount, type, entityId }) => …)`. After every (re)connect refetch
`GET /api/me/notifications/unread-count` once: a message sent while the socket was down is lost (the channel is a hint, the database is
the truth). Keep a slow poll as a fallback.

**Who publishes** (`IInAppNotificationPublisher`, `Casazen.Core/Services`; the implementation is `SignalRInAppNotificationPublisher` in
`Casazen.Web`, the default is a no-op for tests and other hosts):

* `InAppNotificationJob`, for every user whose row it has just written (`type` and `entityId` of the event);
* `POST /api/me/notifications/{id}/read` and `read-all` (`type` and `entityId` `null`);
* reading a guest thread (§ 10), `type` `guest-message` and `entityId` the booking.

The implementation counts the unread notifications in a scope of its own, sends, and **never throws**: a failure is logged
(`Real-time update of the notifications … could not be published`) and the notification stays stored. The Hangfire job runs in the
same process as the web app, so `IHubContext` reaches the sockets.

**Single instance.** Connections live in the memory of the process. With one Railway instance (today) every message reaches the
right sockets. With **two or more instances** a message published on one is not delivered to the sockets of the others: scaling out
needs a backplane (for example the Redis backplane of SignalR, or Azure SignalR Service) and sticky sessions or WebSockets-only
clients (skip negotiation). Until then keep the web service at one replica; a client that misses a message catches up with the
refetch on reconnect and the slow poll.

## 10. Messages of the guest

A message of the guest of a direct booking (`GuestMessageService.SendAsGuestAsync`, after it is saved, never on a replayed send) queues
a push to `PushAudience.BookingHosts(bookingId)`: type `guest-message`, delivery key `booking-message:{messageId:N}` (**one push and
one bell entry for every message**; the e-mail stays throttled to one every ten minutes), route `/bookings/{id}/messages`. The
lock-screen text is the property only: *Nuovo messaggio da un ospite* / *«{property}»: un ospite ti ha scritto.* (`Push_GuestMessage_*`
in `EmailTexts.resx` and `.en.resx`); **no guest name and no text of the message**. It is best effort like the other pushes: a failure
is logged with the ids and never fails the send.

**Reading clears the notification.** `POST /api/bookings/{id}/messages/read` and the host's reply
(`POST /api/bookings/{id}/messages`, answering is reading) mark the caller's unread `guest-message` notifications of that booking as read
(`IInAppNotificationService.MarkEntityReadAsync`: one `ExecuteUpdate` through `OwnNotifications`, conditioned on `ReadAt IS NULL`, so
the same ownership rules as the bell) and, when some changed, publish the new count (§ 9). Only the caller's rows: a colleague keeps
theirs until they read the thread themselves.

**Badges per booking.** `GET /api/messages/unread` (same policy `booking.read` and host scope as `GET /api/messages`):
`{ "items": [ { "bookingId": "<guid>", "unreadCount": 2 } ] }`, only open threads with something unread, the latest guest message first,
at most 200 (`GuestMessageRules.UnreadThreadsMax`). Never any text. Use it for the badges of the calendar and the lists, and refetch it on
`notificationsChanged` with `type: "guest-message"`.

## 11. Code and tests

`Casazen.Core/Entities/InAppNotification.cs` (+ `InAppNotificationLimits`), `Casazen.Core/Services/IInAppNotificationService.cs`,
`Casazen.Infrastructure/Push/{InAppNotificationPushDecorator,InAppNotificationJob}.cs`,
`Casazen.Infrastructure/Services/InAppNotificationService.cs`, `Casazen.Web/Controllers/MeNotificationsController.cs`,
`Casazen.Web/BackgroundJobs/InAppNotificationRetentionJob.cs`, migration `AddInAppNotifications`; real time:
`Casazen.Core/Services/IInAppNotificationPublisher.cs`, `Casazen.Web/Hubs/NotificationsHub.cs`,
`Casazen.Web/Infrastructure/{SignalRInAppNotificationPublisher,HubAccessToken}.cs`; guest messages:
`Casazen.Infrastructure/Services/GuestMessageService.cs`, `Casazen.Web/Controllers/HostMessagesController.cs`.
Tests: `InAppNotificationPushDecoratorTests`, `InAppNotificationJobTests`, `InAppNotificationServiceTests`,
`MeNotificationsControllerTests`, `InAppNotificationRetentionJobTests`,
`InAppNotificationSqlShapeTests`, `InAppNotificationsIntegrationTests` (HTTP, the chain from a push to the
notification), and on PostgreSQL `InAppNotificationsPostgresTests` (schema, the race on the unique index, the single-statement
updates and the retention, whose rows a user reaches, the cascades), `InAppNotificationJobPublishTests`, `NotificationsHubTests`,
`GuestMessagePushTests`, `HostMessagesControllerTests`, `CorsPolicyTests` (hub), and on PostgreSQL `GuestMessageNotificationsPostgresTests`. The architecture tests list the entity
(`TenantQueryFilterArchitectureTests`) and the four actions (`EndpointAuthorizationArchitectureTests`).
