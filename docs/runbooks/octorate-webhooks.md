# Runbook: Octorate webhooks

Task E02/F01/US01/T03 (ADR-023-R7, ADR-022-R19). How Octorate tells CasaZen that something changed, how the call is
authenticated, and what CasaZen does with it. The endpoint sits behind `Features__OctorateConnector` (E02/F01/US02/T01): while
the flag is off it answers 404, like a route that does not exist, before anything of the call is read.

## 1. What Octorate sends

- Octorate documents **no signature** for the reservation, chat and content webhooks (source requirements OCT-08, S24), and a
  subscription carries no secret. In the sandbox (2026-11-04) a delivery was a `POST` with the headers `content-length`, `host`,
  `user-agent`, `content-type`, `x-http-method-override`, `accept` and the body `{"ping": "<string>"}`. What `ping` holds is
  **not recorded yet** (open point 1 of `kb/plan/sandbox-run-results.md`).
- The events: `RESERVATION_CREATED`, `RESERVATION_CHANGE`, `RESERVATION_CANCELLED`, `RESERVATION_CONFIRMED`,
  `CHAT_MESSAGE_RECEIVED`, `CONTENT_NOTIFICATION`, `CONTENT_PUSH`, `PORTAL_SUBSCRIPTION_CALENDAR`. There is no event for check-in,
  police, invoices or payments. No other name is accepted.
- Octorate notifies **CasaZen's own writes too**: a handler ignores the echo of a change it made.
- Retries: any answer other than 200 is retried for up to two days; a **406** stops the retries.

## 2. The address and the secret

The address registered with Octorate is

```
https://<api host>/api/webhooks/octorate/<Octorate__WebhookSecret>?event=<EVENT>
```

- `<Octorate__WebhookSecret>` is a random value of **at least 32 characters** (letters and digits; generate it with a password
  manager), different on test and production. It is compared in constant time (`OctorateWebhookVerifier`). A wrong secret is
  **401** and the log line `Security: Octorate webhook refused` (event `OctorateWebhookSecretRefused`), with the remote address and
  never the secret or the body. A missing, short or placeholder secret is **500** `octorate_webhook_not_configured`: no call is
  accepted.
- `?event=<EVENT>` is optional and only used when the body does not name the event: register one address per subscribed event,
  each with its own `event` value. Octorate keeps the whole address it is given.
- If the sandbox ever shows that Octorate signs its calls, `OctorateWebhookVerifier` is the only place that changes.

**The secret is part of the path: the request logs must not print the path of `/api/webhooks/octorate/*`.**

- The API logs no request path by itself: `Logging__LogLevel__Microsoft` is `Warning` in `appsettings.json`, which keeps the
  `Microsoft.AspNetCore.Hosting.Diagnostics` line "Request starting … /api/webhooks/octorate/…" (level Information) out of the log.
  **Do not lower `Logging__LogLevel__Microsoft`, nor set `Logging__LogLevel__Microsoft.AspNetCore` or
  `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics` to `Information` or below**, on test or production.
- The error middleware logs the route template (`api/webhooks/octorate/{secret}`), never the concrete path.
- Railway's own HTTP logs (Observability → HTTP logs) show request paths to the members of the Railway project: treat access to
  the Railway project as access to the secret, and rotate it when a member leaves.

## 3. Subscribing (once per partner and per environment)

The subscription is made **once per partner (CasaZen's Octorate application) and per environment**, not per property, with the
partner token (`POST /rest/v1/identity/apilogin` with `Octorate__ClientId` and `Octorate__ClientSecret`):

1. `GET /rest/v1/subscription/list`: the events that can be subscribed.
2. `GET /rest/v1/subscription`: what is active now (do not subscribe an event twice).
3. For each event the handlers need: `POST /rest/v1/subscription/{event}` with the form field `url` = the address of § 2
   (https only). The answer carries the id of the subscription: keep it in the password manager next to the secret.

Test environment (sandbox credentials) and production (production credentials) are subscribed separately, each with its own
address and secret. Who does it: the operator with the partner credentials (it is a call of the partner, not of a host).

## 4. What CasaZen does with a call

1. The secret of the address is checked (§ 2).
2. The body (JSON or form, at most 64 KB) is read **only for its routing keys**: the event name, the accommodation code and the
   object id (reservation, thread or content). They are looked for in the body, in a `data` object and in a `ping` that holds a
   JSON object; the event name also in `?event=`. Nothing else of the body is kept or logged.
3. The org is resolved from the accommodation code through the **confirmed** mappings (`OctorateListingMappings`, a system read
   across orgs): the subscription is the partner's, so the call does not say which org it is for. Every org that has the
   accommodation confirmed gets one `OctorateWebhookReceived` event in the outbox (payload: event name, accommodation id, object
   id, time of receipt; ids only, ADR-022-R21), and the call is answered **200** within the request.
4. A call with no known event, no accommodation, or an accommodation with no confirmed mapping (not linked to a CasaZen property
   yet, or another partner's) is answered **200** and recorded nowhere, so Octorate does not retry it for two days. While the
   content of `ping` is unknown this is what happens to every sandbox delivery: the reconciliation (E02/F02) reads the changes.
5. The outbox dispatcher delivers the event to the handlers (`OctorateWebhookReceivedEvent`), which re-read the object through
   `IOctorateApiClient` before any effect: the body is never trusted.

The log line of a recorded call is `Octorate webhook recorded: event {EventId}, type {EventType}, processed True`.

### 4b. The handlers of the reservation events (E02/F02/US01/T02)

`BookingCreatedHandler` (`RESERVATION_CREATED`, `RESERVATION_CONFIRMED`), `BookingModifiedHandler` (`RESERVATION_CHANGE`) and
`BookingCancelledHandler` (`RESERVATION_CANCELLED`) share one flow (`OctorateBookingWebhookHandler`); every other event
(`CHAT_MESSAGE_RECEIVED`, `CONTENT_PUSH`, ...) is theirs to ignore. Each runs in its own scope under the org of the event.

1. Off with `Features:OctorateConnector`: nothing is done. The mapping the event names (`EntityId`) must still be confirmed, in the
   event's org, and still be that accommodation's; otherwise the event is dropped quietly.
2. The org is opened for the client (`IOctorateOrgScope.Use`) and `IBookingImportService.ImportIncrementalAsync` reads the
   reservations of the property changed since **two days before the notification arrived** (Rome dates), the retry window of
   Octorate. The ids in the body are not relied on: the content of `ping` is not recorded, and the notification may be the echo of
   CasaZen's own write, a late one or a repeated one.
3. The service applies a reservation only if its `updateTime` is newer than the one held (`Booking.OctorateUpdatedAt`), under the
   advisory lock `OctorateReservation`, with the booking events (`Origin = Octorate`) in the same save. A repeated or out-of-order
   notification therefore changes nothing and makes no second booking; there is no table of received events (Octorate gives no event
   id). A change that came from Octorate never calls `SyncBookingStateAsync`, so nothing is echoed back.
4. An org that disconnected meanwhile ends the handler quietly; a run that stopped before the end (the rate limit of the org, or an item
   of the list that cannot be read: the list is read until a page with no item) or an error of Octorate makes the handler throw, and
   the dispatcher delivers the event again (up to five attempts, then dead letter). The reconciliation (`octorate-booking-sync`, every
   six hours) reads what is still missing.

The log line of a handled event is `Octorate webhook handled: event {EventId}, type {EventType}, processed True`: the body is never
logged.

**Arrival and departure.** Octorate sends no event for them. When the host registers an arrival or checks a stay out
(`CheckInCompleted`, `CheckOutCompleted`, written to the outbox by the stay itself, through the wizard or not),
`OctorateBookingStatePushHandler` queues `OctorateBookingStatePushJob`, which opens the org and calls
`SyncBookingStateAsync` (`CHECKIN_END` / `CHECKOUT_END`); a booking with no Octorate reservation queues nothing. The job is tried three
times (after 1, 5 and 15 minutes) and then stays failed in the Hangfire dashboard; the check-out itself is already saved.

**Cancellation (E08/F02/US02).** A booking CasaZen holds (a direct one, one the host entered) that is cancelled in CasaZen is cancelled on
Octorate after it: `OctorateBookingCancelHandler` (consumer of `BookingCancelled`, under the session lock `OctorateBookingSync` of the
booking) marks its row `Pending`, cancels the reservation (`PUT` with the status `CANCELLED`, read again to check it, `DELETE` when Octorate refuses that way or leaves the reservation alive) and
marks it `Synced`; no answer, 429 or 5xx leave it `Failed` and the dispatcher delivers the event again. The `RESERVATION_CANCELLED`
notification that follows is the echo of that write: the import finds the booking cancelled and changes nothing. A cancellation that came
from Octorate (`Origin = Octorate`) is never sent back. A property that is not mirrored now (connection lost, mapping still to decide) is never
written to, and the row is `Failed` `octorate_not_mirrored`: reconnect or confirm the mapping, then ask again from the booking (`POST
/api/bookings/{id}/octorate-sync/retry`). A booking that has no reservation yet but whose create was tried (Octorate may have made it and lost the
answer) is looked up by its booking code: found, it is linked and cancelled. A booking of a channel goes the other way round: `POST /api/bookings/{id}/cancel`
asks Octorate first and cancels in CasaZen only when it confirms, on a channel the configuration opens (`Octorate__ChannelActions__*`,
closed by default); see [TECHNICAL.md](../TECHNICAL.md) § «Changing and cancelling a booking».

### 4c. The chat handler and the passes that back it (E05/F01/US01/T03)

`ChatMessageReceivedHandler` handles `CHAT_MESSAGE_RECEIVED` only (in its own scope under the org of the event, off with
`Features:OctorateConnector`). The accommodation and the thread named by the body are read as the names of what to read, nothing more:
`IMessageSyncService.SyncThreadAsync` re-reads the thread and its messages through the API (`createdAfter`, with an overlap of five minutes) and
stores what is new, under the advisory lock `OtaMessageThread` of (org, thread). A notification without a usable thread (not a number, or none), or whose thread Octorate does not know (404: the object id of the body may
be another object), makes `ReconcileRecentAsync` read the org's listings instead. The accommodation must be a listing of the org's mapping, otherwise it is not read.

1. **Once, in any order.** A message is one row of the org by its Octorate id and `GuestMessageReceived` (key `ota-msg:{message id}`) is recorded in
   the same save, so a notification that arrives twice, late or out of order stores one message and raises one event. `OtaThreadSynced` (key
   `ota-thread:{thread}:{last message}:{flag of Octorate's assistant}`, with the ten minutes of the pass added while the flag reads on) says a thread
   was read, for the guard that keeps that assistant off (`OctorateAiAssistantGuard`, [message-assistant.md](message-assistant.md) § «Octorate's own
   assistant kept off»): an assistant that reads on is told to the guard at every pass.
   A new inbound message older than 48 hours is stored and raises no event: the import of a history must not notify.
2. **A thread of a listing nobody confirmed** is stored with no property (unassigned) and gets it at the first pass after the confirmation.
3. **Failures.** An error of Octorate, or the calls of the org being used up, makes the handler throw, and the dispatcher delivers the event again
   at its next run (the handler never waits for the calls to refill: it runs in the outbox dispatcher, which serves every org within a budget of
   seconds, so it ends at once with the rate limit; the jobs and the import wait for them, up to two minutes);
   an org that disconnected, and a listing without the chat module in its license (`OtaMessageSyncStates.LastError = chat_not_licensed`, shown to
   the host as a state and never as an empty inbox), end the handler quietly.
4. **The passes.** `ota-message-reconcile` (every 10 minutes) reads `GET /rest/v1/accommodation/recentMessages?property=` for each listing
   (the last 24 hours) and the threads it names; `ota-message-deep-reconcile` (02:30 UTC) reads the threads with activity since the last deep pass
   minus two days. Both are behind the flag and keep their position per listing in `OtaMessageSyncStates` (`ImportCursor`, `LastRecentPassAt`,
   `LastDeepPassAt`, `LastError`). Not proven on a licensed structure yet: the sandbox has no `chat` module, so the shape of the chat DTOs is the
   one of the OpenAPI and the license refusal is read from the answer of the application server that is no error of Octorate's API (no `type`): a 403
   for the chat reads, a 500 for `recentMessages` (`sandbox-run-results.md`). Both are kept as `chat_not_licensed`, and the passes leave the listing
   alone for an hour before they ask again.

The log line of a handled event is `Octorate chat webhook handled: event {EventId}, type {EventType}, ...`: never the text, a name or an address.

### 4d. The check-in link of a stay with no e-mail address (E23/F01/US01, NF-122, NF-63, decision O10)

A stay that came from a portal has an empty e-mail address, so the link of the daily job `guest-checkin-send` used to fail in silence (`no_recipient`). With the flags
`Features:PortalChatCheckInLink` and `Features:GuestContactInfo` (both off by default, both only while `Features:OctorateConnector` is on) CasaZen takes the link to the
guest and, when it cannot, says why. One row per booking, `CheckInLinkDeliveries` (`CheckInLinkDelivery`, the token of the link encrypted at rest and only while it can be
needed), is what the host sees: `GET api/bookings/{id}/checkin/link-delivery` (booking read; the link itself, «Copia link», only for a caller with `booking.write` on the
property, only while the delivery waits or failed and the link can still be opened).

| Order | Way | What it does | States |
|---|---|---|---|
| 0 | The booking has an address | The e-mail, as for any booking | `Sent` by `Email` |
| 1 | The chat (`PortalChatCheckInLink`) | The reservation thread of the booking (kind Reservation, at the property of the booking, mapping confirmed): **one** message through `IMessageReplyService` with the origin `Automation`, no user, in the language of the guest (the one chosen on the check-in page, else the one of the country on the booking, else Italian and English in the same message), key = the booking and the token. A message the channel manager refused (the state `Error`, nothing left) lets the next attempt for the same link use the next key; a message whose outcome is unknown (`ManualVerify`) is never sent again | `Sent` by `PortalChat` (only when the channel manager says the message is sent); `Sending` (the message is only created, or waits for the authorization of the channel: the link is not delivered, the row stays open with its token and the «To do» it has, and the hourly pass reads the message again: sent → `Sent`; error → `Failed` with `octorate_message_error`, the «To do» and the notice, and «Copia link» still has the link); `WaitingChatPermission` (`chat_not_licensed`: the last pass of the copy found the chat module not in the license, or the send was refused for it; no call is made while the copy says so); `WaitingThread` (`no_conversation`); `Failed` with the code (`manual_verify`, `mapping_not_confirmed`, `octorate_not_connected`, `octorate_rate_limited`, `octorate_unavailable`, ...) |
| 2 | The contact of the guest (`GuestContactInfo`) | Only when the chat did not deliver (the chat reads no personal data): the reservation is read (flags, no data) and, when Octorate says it holds an address, `POST /rest/v2/reservation/{acc}/{refer}/contactInfo` **once for the booking**, for this link only; the address is written on the guest and the link goes by e-mail; a phone number is kept for the host's view. One line of the activity log (`ContactInfoRead`, purpose `check_in_link`, no actor, never the address). Never in bulk, never into the CRM | `Sent` by `Email`; `WaitingStaticIps` (401 or 403: the static IP addresses are not registered; the read is repeated at most once a day, never in a loop); `WaitingContact` (the channel holds no address: an Airbnb guest has none) |
| 3 | The host | The row says what the link waits for, with one «To do» (`CheckInLinkToSend`, action `CopyCheckInLink`, due the day of the check-in, once per booking) while one of the flags is on, and, for a failure that no retry will change, the bell and the live update (`checkin-link-failed`, once per link) | `WaitingContact` with `Manual` |

**The state shown** is the chat's while the chat is on, and the contact's otherwise. **With both flags off** the job only writes the row (`WaitingContact`, `Manual`): no call, no
«To do», no notice, no token, and the failed e-mail on the session is what the host sees, as before.

**The retry.** `checkin-link-delivery-retry` runs at minute 30 of every hour, only while the connector and a flag are on. A row is tried again when the thing it waits for may
have changed, at most once an hour: `WaitingChatPermission` when the copy of the conversations read the listing without error since the last attempt (the license probe of the sync
runs hourly, § 4c; a wait that came from a **refused send** (an automatic message of the booking in the state `Error`) is not lifted by the copy, which moves its state at every pass: it is tried
again at most once a day); `WaitingThread` when the booking has its conversation; `WaitingStaticIps` and a refused contact a day after the last read; `Failed` with a limit or an outage up
to three attempts. A row made while the flags were off holds no token and nobody has its link: when a flag is turned on the pass issues a new link for it. A row ends when the guest
completes the check-in, the booking is cancelled or no longer takes a check-in, the link expires, or the host generates or sends a link itself; its «To do» is closed with it.
The reminder of the missing data leaves such a stay alone (a new link would replace the one the guest already has in the chat, or the one the host copies from its «To do»: an open row
whose link goes by a channel of its own, or that holds a token, is left alone; a row with no token has nothing to copy and the stay is reminded as it always was).

**An automatic message answers nobody.** `MessageOrigin.Automation` is never shown as written by a person; it marks no message of the guest as read and does not make the
conversation «answered», so a question of the guest still waits in «Da rispondere» (the copy takes the direction of the thread from the newest message that is not automatic).

**What the person has to do (static IP addresses).** Octorate gives the contact of a guest only to the static outbound IP addresses registered with it. (1) Move the Railway service
to a plan with static outbound IP addresses (Pro; they change the outbound IP of **all** the service) and note them. (2) Ask Octorate to register them for the application (the
`contactInfo` permission is the application's). (3) Set `Features__GuestContactInfo=true` in the environment (test first). To check: a booking of a portal with no address shows
`WaitingStaticIps` before the registration, and `Sent` by `Email` after it (one line `ContactInfoRead` with `{"purpose":"check_in_link"}` in `OrgActivityEntries`); the log says
`The contact of the guest of booking ... was refused by the channel manager: the static IP addresses are not registered` until then, and no address, number or name is ever in it.
The chat needs the chat permission of the application and the `chat` module in the license of the listing (§ 4c): without them the booking shows `WaitingChatPermission`, and
the inbox says so (`GET api/messages/channel-status`: `available`, `waiting_permission`, `error` for each listing of the caller, and the reason `chat_not_licensed` on the actions of
a conversation, so the reply box explains instead of failing).

## 5. Rotating the secret

1. Generate a new secret (§ 2) and set `Octorate__WebhookSecret` on the environment (the service redeploys).
2. Update every subscription of that environment with the new address: `PUT /rest/v1/subscription/{id}` with the new `url`.
3. Between the two steps the calls with the old address are 401 and Octorate retries them (up to two days): do step 2 at once.

## 6. Open points

- Whether a secret in the address is acceptable to the product owner (risk listed in the epic): the alternative needs a signature
  that Octorate does not offer.
- What `ping` holds: once recorded, adapt `OctorateWebhookNotification` (the only reader of the body) if it carries the keys in
  another shape.
