# Runbook: client IP behind the Railway proxy and rate limiting

Task FD-10, issue #273 (audit defects A1-12, A3-12, A3-30, A3-41, A5-10, A8-22, A9-10). The code is in place;
the product owner checks the proxy chain on each Railway environment (section 3) and, if needed, sets the
variables of section 2.

## What the backend does

| Concern | Behaviour | Code |
|---|---|---|
| Forwarded headers | `app.UseForwardedHeaders()` is the **first** middleware. It processes `X-Forwarded-For` and `X-Forwarded-Proto` **right to left**, only through trusted proxies and at most `ForwardLimit` entries | `Casazen.Web/Program.cs`, `Extensions/ForwardedHeadersServiceCollectionExtensions.cs` |
| Client IP | Read only from `HttpContext.Connection.RemoteIpAddress` (after the middleware), through `ClientIp`. No code reads `X-Forwarded-For` any more | `Infrastructure/ClientIp.cs` |
| Consent evidence | `ConsentRecord.IpAddress` (onboarding), `Guest.ConsentIpAddress` (direct booking, guest check-in) store that IP | `UsersController`, `PublicBookingsController`, `PublicGuestCheckInController`, `GuestCheckInController` |
| Rate limiting | Every policy is a fixed window **partitioned per client IP** (IPv4 address, or IPv6 /64); the guest check-in policies per IP **and** SHA-256 of the token. One client using up its quota never blocks the others | `Extensions/RateLimitingServiceCollectionExtensions.cs`, `Infrastructure/RateLimitPolicies.cs` |
| 429 response | ProblemDetails (`application/problem+json`) with `code: "rate_limited"`, localized `detail`, `retryAfterSeconds`, `traceId`, plus the `Retry-After` header (seconds) | same file |
| Logs | `Rate limit exceeded: policy … on {Method} {Route}` with the trace id. The client IP is **not** logged | same file |
| Diagnostics | `GET /api/admin/diagnostics/client-ip` (role `Admin`): how the API sees **your own** request (section 3) | `Controllers/AdminDiagnosticsController.cs` |

## 1. Why never the first value of `X-Forwarded-For`

Each proxy **appends** the address it received the connection from, so the header reads
`client-written values…, address seen by proxy 1, address seen by proxy 2`. Anything on the left can be written
by the client (`curl -H "X-Forwarded-For: 1.2.3.4"`). Before FD-10 the API used the leftmost value: a client could
record any IP as GDPR consent evidence and get a fresh rate limit bucket at every request.

The only trustworthy entries are the ones appended by proxies we trust, read from the right. That is what the
middleware does: it starts from the TCP peer, and while the current address is a trusted proxy (and fewer than
`ForwardLimit` entries have been used) it takes the next entry from the right.

## 2. Configuration (Railway variables, per environment)

| Variable | Meaning | Default |
|---|---|---|
| `ForwardedHeaders__KnownNetworks` | CIDR networks of the trusted proxies, comma separated (e.g. `100.64.0.0/10`). Array form `ForwardedHeaders__KnownNetworks__0` also works | empty |
| `ForwardedHeaders__KnownProxies` | Single IP addresses of trusted proxies, comma separated | empty |
| `ForwardedHeaders__ForwardLimit` | Maximum number of proxy hops processed | `1` |

An invalid CIDR/IP or a limit below 1 stops the startup with an explicit error.

- **Networks or proxies configured**: only a peer inside them is trusted; processing stops at the first address
  outside them. A client connecting directly (not through the proxy) can never change its IP.
- **Nothing configured (default, the compromise)**: the TCP peer is trusted as the proxy, for `ForwardLimit` hops.
  With `ForwardLimit=1` the client IP is the **last** entry, the one the Railway edge appended itself: a forged
  value on the left is ignored. This is correct as long as (a) the container is reachable only through the Railway
  edge, which is the case for a Railway public domain, and (b) there is exactly one proxy hop that appends to the
  header. If Railway adds an internal hop, the last entry is a Railway address: every client behind that edge node
  then shares one rate limit bucket and consent evidence records the edge address. Section 3 detects it.

Do **not** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: it adds a second forwarded headers middleware that trusts any
peer, and the header would be processed twice.

## 3. Finding the Railway proxy networks (do it on `test`, then on `production`)

Railway's documentation of its edge headers is not consistent (community answers report both "the edge replaces
the client's `X-Forwarded-For`" and "the edge appends to it", internal proxies in `100.64.0.0/10`, and one more hop
when the CDN path is active). None of this was verified by us, so measure it:

1. Find your public IP (router page or any "what is my IP" service), e.g. `203.0.113.25`.
2. With an Admin access token:

   ```bash
   curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$RAILWAY_TEST_URL/api/admin/diagnostics/client-ip"
   ```

   The response has `clientIp`, `originalPeer` (the TCP peer, i.e. the Railway proxy the container sees),
   `unprocessedForwardedFor` (entries not used), `scheme`, `forwardLimit`, `knownNetworks`, `knownProxies`.
3. Read the result:

   | What you see | Meaning | Action |
   |---|---|---|
   | `clientIp` = your IP, `scheme` = `https` | One hop: the default works | Optional hardening: set `ForwardedHeaders__KnownNetworks` to the network of `originalPeer` (repeat the call a few times and after a redeploy to see the range) |
   | `clientIp` is a Railway address (e.g. `100.x.y.z`) and your IP is the **last** item of `unprocessedForwardedFor` | Two hops | Set `ForwardedHeaders__KnownNetworks` to the network(s) containing both `originalPeer` and that address, and `ForwardedHeaders__ForwardLimit=2`. Redeploy and repeat step 2 |
   | Anything else | Unexpected chain | Do not guess: keep the default and open an issue with the (redacted) output |

4. Check that forging does not work (your IP must not change):

   ```bash
   curl -s -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Forwarded-For: 198.51.100.1" \
     "$RAILWAY_TEST_URL/api/admin/diagnostics/client-ip"
   ```

   `clientIp` must still be your IP; `198.51.100.1` may only appear in `unprocessedForwardedFor`.
5. Repeat on `production` with `$RAILWAY_PROD_URL`, and again whenever a CDN or another proxy is put in front of
   the API (each proxy adds a hop and needs its own network in `KnownNetworks`).

## 4. Rate limiting policies

Limits are **per client IP** (and per token where noted), per fixed window. The defaults of the policies that
existed before FD-10 are the old *global* limits, now applied per IP.

| Policy | Endpoints | Default | Limit key (legacy key still honoured) |
|---|---|---|---|
| `PublicRead` | `GET api/public/orgs/{slug}`, `…/properties`, `…/properties/{id}`; `GET api/properties/search`, `GET api/properties/{id}/public`; `GET api/public/bookings/property/{id}/availability`; `GET api/public/suppliers/{slug}`; `POST api/suppliers/invites/lookup`, `GET api/suppliers/registration-options` (SU-01) | 120 / min | `RateLimiting__PublicRead__PermitLimit` |
| `PublicBookingCreate` | `POST api/public/bookings` | 10 / min | `RateLimiting__PublicBookingCreate__PermitLimit` (`DirectBooking__RateLimitPermitLimit`) |
| `PublicBookingLookup` | `POST api/public/bookings/{id}/outcome` and `…/payment-session` (the outcome page polls it, BK-07), `POST api/public/bookings/{id}/confirm-email` ("pay at the property" link, BK-06) | 30 / min | `RateLimiting__PublicBookingLookup__PermitLimit` |
| `PublicGuestBookingLookup` | `POST api/public/bookings/lookup` and `…/lookup/check-in-link` ("Le mie prenotazioni", BK-11); plus a per-email limit `GuestBookingLookupPerEmail` (5 / 15 min, `RateLimiting__GuestBookingLookupPerEmail__PermitLimit`) | 10 / 5 min | `RateLimiting__PublicGuestBookingLookup__PermitLimit` |
| `GuestCheckIn` | `GET api/public/checkin/{token}`; legacy `api/checkin/*` | 10 / min per IP+token | `RateLimiting__GuestCheckIn__PermitLimit` (`CheckIn__RateLimitPermitLimit`) |
| `GuestCheckInSubmit` | `POST api/public/checkin/{token}` | 3 / min per IP+token | `RateLimiting__GuestCheckInSubmit__PermitLimit` (`CheckIn__SubmitRateLimitPermitLimit`) |
| `PublicTouristTaxCalc` | `POST api/public/tourist-tax/calculate` | 30 / min | `RateLimiting__PublicTouristTaxCalc__PermitLimit` (`SeoTouristTax__RateLimitPermitLimit`) |
| `PublicResolveHost` | `GET api/public/resolve-host` | 60 / min | `RateLimiting__PublicResolveHost__PermitLimit` (`PublicHost__RateLimitPermitLimit`) |
| `PublicIcal` | `GET api/public/ical/{token}` (polled by the OTAs from their servers) | 60 / min | `RateLimiting__PublicIcal__PermitLimit` |
| `PublicRegistration` | `POST api/suppliers/register`, `POST api/auth/register` (shared bucket) | 5 / 10 min | `RateLimiting__PublicRegistration__PermitLimit` |
| `PublicSupplierCheckIn` | `api/public/check-in/{jobId}` (GET, check-in, check-out) | 20 / min | `RateLimiting__PublicSupplierCheckIn__PermitLimit` |

The window of every policy is `RateLimiting__{Policy}__WindowSeconds` (60, or 600 for `PublicRegistration`).

Notes:

- Counters live in memory, per instance: with N replicas a client gets up to N × the limit, and a redeploy resets them.
- Many users behind one address (hotel Wi-Fi, mobile carrier NAT) share a bucket: raise the limit of the
  affected policy rather than removing it. `Retry-After` tells the client when to retry.
- Deliberately without a limiter: `webhooks/*` (signed; Stripe and the e-sign provider send from shared IPs),
  `api/health`, `api/legal/*`, `api/orgs/plans`, SEO pages, the SEO hub `api/public/content` and `api/public/sitemap.xml` (search engine crawlers; the web app serves
  the sitemap through a CDN-cached Vercel function, see [`seo-domain.md`](seo-domain.md)).

## 5. Checks after a deploy

- [ ] Section 3 done on this environment; variables of section 2 set if needed.
- [ ] Forged `X-Forwarded-For` does not change `clientIp` (section 3, step 4).
- [ ] 31 `POST $URL/api/public/tourist-tax/calculate` with body `{}` within one minute (limit 30): the last one
      returns `429` with `code: "rate_limited"` and a `Retry-After` header, while a request from another network
      (e.g. a phone on mobile data) in the same minute is not limited.
- [ ] Complete an onboarding: the new `ConsentRecords.IpAddress` rows contain your public IP, not a `100.x`/`10.x`
      address.

Consent rows written before FD-10 contain either the Railway proxy address or a value the client could choose:
they are not reliable evidence of the consenting device.
