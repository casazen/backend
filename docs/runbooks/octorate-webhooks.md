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
from Octorate (`Origin = Octorate`) is never sent back. A booking of a channel goes the other way round: `POST /api/bookings/{id}/cancel`
asks Octorate first and cancels in CasaZen only when it confirms, on a channel the configuration opens (`Octorate__ChannelActions__*`,
closed by default); see [TECHNICAL.md](../TECHNICAL.md) § «Changing and cancelling a booking».

## 5. Rotating the secret

1. Generate a new secret (§ 2) and set `Octorate__WebhookSecret` on the environment (the service redeploys).
2. Update every subscription of that environment with the new address: `PUT /rest/v1/subscription/{id}` with the new `url`.
3. Between the two steps the calls with the old address are 401 and Octorate retries them (up to two days): do step 2 at once.

## 6. Open points

- Whether a secret in the address is acceptable to the product owner (risk listed in the epic): the alternative needs a signature
  that Octorate does not offer.
- What `ping` holds: once recorded, adapt `OctorateWebhookNotification` (the only reader of the body) if it carries the keys in
  another shape.
