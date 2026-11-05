# Runbook: Octorate webhooks

Task E02/F01/US01/T03 (ADR-023-R7, ADR-022-R19). How Octorate tells CasaZen that something changed, how the call is
authenticated, and what CasaZen does with it.

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

## 5. Rotating the secret

1. Generate a new secret (§ 2) and set `Octorate__WebhookSecret` on the environment (the service redeploys).
2. Update every subscription of that environment with the new address: `PUT /rest/v1/subscription/{id}` with the new `url`.
3. Between the two steps the calls with the old address are 401 and Octorate retries them (up to two days): do step 2 at once.

## 6. Open points

- Whether a secret in the address is acceptable to the product owner (risk listed in the epic): the alternative needs a signature
  that Octorate does not offer.
- What `ping` holds: once recorded, adapt `OctorateWebhookNotification` (the only reader of the body) if it carries the keys in
  another shape.
