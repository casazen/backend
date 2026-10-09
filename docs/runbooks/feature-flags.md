# Runbook: feature flags

Task FD-20 (defects A2-09, A9-17, R-10 webhook part, A8-16 OTA part, A9-15 OTA jobs; decision D10).
Task FD-21 adds `AiSupplierDiscovery` (defects A8-01, A8-14, A8-15, A4-26, A8-16 AI part; decision D11).
Task LT-01 adds `RliProvider` (defects A7-01, A7-21; decision D15; `docs/runbooks/rli.md`).
Task LT-02 adds `ESignProvider` (defects A7-02, A7-16, A7-20; decision D15; `docs/runbooks/rli.md` § Contract signature).
Task SP-02 (redesign wave) adds `SupplierShowcaseBooking` and `SupplierOnlinePayments` (decisions D34 and D2;
`docs/runbooks/suppliers.md` section 19).
Task SP-04 (redesign wave) adds `SupplierRequestAutoCancel` (decision D8; `docs/runbooks/suppliers.md` section 21).
Task SP-09 (redesign wave) puts the public reads of the supplier showcase behind `SupplierShowcaseBooking` (`docs/runbooks/suppliers.md` section 22).
Task SP-10 (redesign wave) puts the booking from the showcase behind the same flag (`docs/runbooks/suppliers.md` section 23).
Task SP-11 (redesign wave) puts the customer's own area of a booking (find, cancel, move, answer a proposed time) behind the same flag
(`docs/runbooks/suppliers.md` section 24): **the flag requires SP-10 and SP-11 both deployed**.
Task BL-01 (redesign wave) adds `UiRedesign` (decision 01-D8; the same task adds the "accesso aperto" switch,
`docs/runbooks/open-access.md`, which is **not** a feature flag).
Task SP-14 (redesign wave) puts the supplier's Stripe Connect account behind `SupplierOnlinePayments`
(`docs/runbooks/stripe.md` § "Connect onboarding of the suppliers (SP-14)", `docs/runbooks/suppliers.md` section 25).
Task AM-01 adds `OrgTeam` (org team, wave redesign; decisions D1, D15; `docs/runbooks/org-team.md`).
Task SP-15a (redesign wave) puts the **creation** of the payments of the supplier's work behind `SupplierOnlinePayments`
(`docs/runbooks/stripe.md` § "Services of the suppliers (SP-15)", `docs/runbooks/suppliers.md` section 26); SP-15b adds the
requests and reminders the daily job sends (and nothing else: the webhook, the sync and the refunds are not behind it).

## How it works

| Piece | Where | Behaviour |
|---|---|---|
| Configuration | section `Features` (`appsettings.json`), overridden by Railway variables `Features__<Name>` | One boolean per flag. **Missing or not `true`/`false` means off.** Read at every check: changing a Railway variable redeploys the service and the new value applies. |
| Flag names | `Casazen.Core/Features/FeatureFlags.cs` | A constant per flag plus the list `FeatureFlags.All` exposed to the frontend. |
| Code checks | `IFeatureFlags.IsEnabled(FeatureFlags.X)` (`Casazen.Core.Features`, singleton `ConfigurationFeatureFlags`) | For services, background jobs, recurring job registration. Code that runs before the container exists (service registration) uses `ConfigurationFeatureFlags.IsEnabled(configuration, flag)`. |
| Endpoints | `[FeatureGate(FeatureFlags.X)]` on a controller or an action (`Casazen.Web/Infrastructure/FeatureGateAttribute.cs`) | With the flag off the endpoint answers **404 `not_found`**, the same response as a route that does not exist, before authentication, rate limiting and model binding (`FeatureGateMiddleware`, right after `UseErrorHandling`). |
| Frontend | `GET /api/public/features` (anonymous, rate limit `PublicRead`) → `{ "otaPartnerApi": false, "aiSupplierDiscovery": false }` | One camelCase key per flag. The SPA loads it once (`FeatureFlagsProvider`, `src/config/feature-flags.ts`); while loading, or if the call fails, every flag is **off**. |

## Flags

| Flag | Railway variable | Default | What it controls |
|---|---|---|---|
| `OtaPartnerApi` | `Features__OtaPartnerApi` | `false` (D10: Airbnb / Booking.com partner API in freeze, #31-35) | `api/ota`, `api/properties/{id}/ota-integrations`, `POST /webhooks/ota/{platform}` (404 when off, also without `OTA:WebhookSecret`); OTA adapters, their HTTP clients and rate limiter (not registered in DI); recurring jobs `ota-sync-all` and `booking-pull-all` (not registered, and removed with `RemoveIfExists` from the Hangfire schema of earlier deploys); frontend menu "Canali OTA", routes `/app/short-rent/ota*`, dashboard OTA widget, OTA card in the property detail. **iCal import/export is not behind this flag.** |
| `AiSupplierDiscovery` | `Features__AiSupplierDiscovery` | `false` (D11: AI supplier discovery, US-014 in freeze) | `POST /api/service-requests/match-supplier` (404 when off, before authentication); inside the services, with the flag off: no web search (`IWebSearchClient`), no LLM extraction of "nearby businesses", no AI match reason, so **nothing reaches the AI provider**. Frontend: key `aiSupplierDiscovery` with no consumer: the web AI match flow ("Raccomandazione AI", "più votati su Google") was unreachable and has been removed. **The manual service request** (`POST /api/service-requests` with the supplier chosen in the marketplace) **is not behind this flag.** |
| `RliProvider` | `Features__RliProvider` | `false` (D15: RLI filing through a provider needs a real provider client, the legal opinion and the cost approval, `docs/integrations/rli-esign.md` §5) | `POST /api/leases/{id}/registration` (404 when off); recurring job `lease-registration-status-poll` (not registered, removed with `RemoveIfExists`); in the service, nothing reaches the provider. **On is not enough**: the provider must also be configured (`ILeaseRegistrationProvider.IsConfigured`), and today the only one registered is `UnconfiguredLeaseRegistrationProvider`, so the path stays unavailable (409 `rli_provider_unavailable`). Frontend: key `rliProvider` with no direct consumer; the lease page reads `providerFilingAvailable` from the RLI checklist. **Manual registration** (`POST /api/leases/{id}/registration/manual`) **is not behind this flag** and is the default. |
| `ESignProvider` | `Features__ESignProvider` | `false` (D15: a lease signed electronically needs at least the FEA; no provider client is written and the budget and the legal opinion are open, `docs/integrations/rli-esign.md` §3–§5) | `POST /api/leases/{id}/signing` and `POST /webhooks/esign` (404 when off, before anything is read); recurring job `lease-sign-status-poll` (not registered, removed with `RemoveIfExists`); queued webhook events are dropped. **On is not enough**: the provider must also be configured (`ILeaseESignService.IsConfigured`), and today the only one registered is `UnconfiguredLeaseESignService`, so the path stays unavailable (409 `esign_provider_unavailable`). **On requires `ESign__WebhookSecret`** (at least 16 characters, no placeholder): otherwise the service does not start (`ESignOptionsValidator`). Frontend: key `eSignProvider` with no direct consumer; the lease page reads `providerSigningAvailable` from `GET /api/leases/{id}/signers`. **The offline signature** (`GET contract.pdf`, `POST signed-document`, `POST stipula`) **is not behind this flag** and is the default. |
| `SupplierShowcaseBooking` | `Features__SupplierShowcaseBooking` | `false` (D34: booking from the public showcase of a supplier, `/fornitori/{slug}`; the product owner turns it on when the booking is ready and reviewed) | **SP-09 (public reads)**: `GET api/public/suppliers/{slug}/services`, `…/services/{serviceSlug}`, `…/slots` and `POST …/quote` (404 when off, before authentication and rate limiting), and the two members the page `GET api/public/suppliers/{slug}` gains, `services` and `medianResponseMinutes` (left out when off: the page reads exactly as before SP-09). The page itself keeps answering whatever the flag. **SP-10 (booking)**: `POST api/public/suppliers/{slug}/bookings` and `POST …/bookings/{id}/confirm-email` (404 when off, before authentication and rate limiting); the frontend booking steps read the key `supplierShowcaseBooking`. **On requires `Suppliers__Showcase__PrivacyNoticeVersion` and, outside Development and Testing, `Suppliers__CustomerIndexKey` (32+ characters): otherwise the service does not start** (`ShowcaseBookingOptionsValidator`). The jobs `service-request-expiry` and `service-request-reminders` are registered **whatever the flag says** (a booking made while it was on has to lapse and be reminded of after it is turned off). **SP-11 (the customer's own area)**: `POST api/public/supplier-bookings/lookup`, `…/cancel`, `…/reschedule`, `…/proposal/accept` and `…/proposal/reject` (404 when off, before authentication and rate limiting): **turn it on only where SP-10 and SP-11 are both deployed**, otherwise a customer can book and cannot manage the booking its e-mails send it to. `suppliers.md` sections 22, 23 and 24. **The supplier's service catalog** (`api/supplier/services`, section 19) **is not behind this flag**: every supplier has one. |
| `SupplierRequestAutoCancel` | `Features__SupplierRequestAutoCancel` | `false` (D8: a new request that nobody answers is cancelled by CasaZen after the response window; it changes what happens to the requests that exist, so the product owner turns it on once the console and the mobile app know the status `Annullato`) | The recurring job `service-request-auto-cancel` (every 10 minutes; registered only with the flag on and removed with `RemoveIfExists` otherwise) and, inside `ServiceRequestAutoCancelService`, the run itself (a run triggered by hand with the flag off does nothing). With the flag off the deadline `ResponseDueAt` is still recorded on every new request, but **nothing is ever cancelled by time**. The window is `Suppliers__ServiceRequests__HostResponseMinutes` (120). Turning it on cancels at the first run every open request that is already overdue (500 per run, every 10 minutes). The manual cancellation (`POST …/cancel`) is **not** behind this flag. Frontend key `supplierRequestAutoCancel` with no consumer yet. |
| `SupplierOnlinePayments` | `Features__SupplierOnlinePayments` | `false` (D2: payment of a supplier's work inside CasaZen, direct charge on the supplier's Stripe account with the platform commission; the legal texts, D-C, and the tax treatment of the commission, D4, still need review before it goes live) | **SP-14**: `api/supplier/payments/*` (the supplier's Stripe Connect account: state, onboarding link, Express Dashboard link; 404 when off, before authentication; `stripe.md` § "Connect onboarding of the suppliers (SP-14)"). **Not behind it**: the processing of Stripe's events (`account.updated` of a connected account is applied whatever the flag says), the host's `api/connect/*`. **SP-15a also gates the creation of the payments**: with the flag off a request is never taken as an online one (`paymentMode: Manual`), a request taken online and completed after the flag was switched off falls back to `Manual` (the host marks it paid, nothing is stuck) and `POST api/supplier/requests/{id}/payment-request` is a 404 before authentication. **Not behind it, on purpose** (money in flight): the payer's page and payment sessions (`api/public/service-payments/*`), the host's session, the supplier's offline record (`payment/offline`, the exception when online payments are not available) and, from SP-15b, the webhook of the payments. **SP-15b**: the daily job `service-payment-reminders` sends the pending requests and the reminders only with the flag on (with it off it only flags the late payments), and `SendPendingPaymentRequestsJob` sends nothing. The recurring jobs are always scheduled; the processing of Stripe's events, `service-payment-sync` and the admin tools (`api/admin/supplier-payments`, refunds, the commission of a supplier, the export) are **not** behind it, because they handle money that already exists. Without the flag the manual flow ("Segna pagato" by the host) is the only one. **Keep it off in production** until the legal texts of the supplier payments (D-C) and the tax treatment of the commission (D4) are approved; with SP-15b deployed it can be tried in Staging with Stripe in test mode. Frontend key `supplierOnlinePayments`. |
| `UiRedesign` | `Features__UiRedesign` | `false` (01-D8: gradual rollout of the new interface; the product owner turns it on when the frontend work is deployed and reviewed) | **Nothing in the backend**: BL-01 only introduces and exposes the flag, no endpoint, service or job reads it. Frontend: key `uiRedesign` (to add to `src/config/feature-flags.ts`, UI-01), which switches the new design tokens, shell and navigation on (`html[data-ui='v2']`) or off for everybody. It changes what **every** user sees, so there is no per-org or per-user switch in this flag. |
| `OrgTeam` | `Features__OrgTeam` | `false` (the web app of today does not know the `account` context; the team screens arrive with AM-04) | AM-01: `GET /api/me/contexts` **does not list the `account` context** with the flag off (the authorization does not depend on the list: memberships, policies and the 403 `member_inactive` of a deactivated member work with the flag off). AM-02 gates the invitation endpoints on it. Frontend: key `orgTeam` with no consumer yet. **Turn it on together with the account screens, never before** (`docs/runbooks/org-team.md` § 7). |

### Before turning `AiSupplierDiscovery` on

D11 keeps it off. If the product owner ever lifts the freeze: the endpoint comes back with the FD-21 guards (category
allowlist, per-user and per-org rate limit, platform AI budget checked before every call, no host notes in the prompt,
no LLM rating, only `https` Google Maps links), but the web UI must be rebuilt behind `flags.aiSupplierDiscovery`, the
DeepSeek `web_search` tool support must be verified (A8-14: otherwise the "businesses" are hallucinated) and the
external provider must be declared as a subprocessor with its legal details (docs/runbooks/ai.md).

### Before turning `OtaPartnerApi` on

The flag only hides code that is still in freeze; turning it on brings back the known defects: `ota-sync-all` /
`booking-pull-all` run with `Guid.Empty` (no-op plus a warning at every run, A9-15), `OtaManager.SyncPlatformAsync` /
`PullBookingsAsync` do nothing, the frontend OTA pages call endpoints that do not exist (`/ota/sync/all`, `POST /ota`,
`/ota/{id}/validate`). Removed for good by FD-20 (they do not come back with the flag): `PUT /api/ota/pricing`
(rewrote `NightlyRate` without validation), `POST /api/ota/validate?apiKey=` (API key in the query string),
`POST /api/ota/sync-platform` (no ownership check, A9-17).

### Before turning `UiRedesign` on

Nothing to do now: the default is off, and until the frontend ships the new interface (UI-01 onwards) the flag has no
consumer, so turning it on changes nothing. When it does have one, turn it on **on `test` first**, look at the main
journeys on a phone and on a desktop, and only then on `production` (`Features__UiRedesign=true`; delete the variable or set
`false` to go back: by design of 01-D8 the new tokens live under `html[data-ui='v2']` only, so the old look comes back at
once). It is one value for everybody: `GET /api/public/features` is anonymous and global, there is no per-org or per-user
value. It is independent of the "accesso aperto" switch ([`open-access.md`](open-access.md)).

## Adding a flag

1. Backend, `Casazen.Core/Features/FeatureFlags.cs`: `public const string MyFeature = "MyFeature";` and add it to `All`.
2. Backend, `Casazen.Web/appsettings.json`: `"Features": { ..., "MyFeature": false }` (documentation of the default;
   a missing value is off anyway).
3. Backend, gate the code:
   - endpoints: `[FeatureGate(FeatureFlags.MyFeature)]` on the controller or the action;
   - services / jobs: inject `IFeatureFlags` and check `IsEnabled(FeatureFlags.MyFeature)`;
   - recurring jobs: register them in `RecurringJobsRegistration.Configure` only when the flag is on and call
     `RemoveIfExists(id)` otherwise;
   - DI registrations: `ConfigurationFeatureFlags.IsEnabled(configuration, FeatureFlags.MyFeature)`.
4. Frontend, `src/config/feature-flags.ts`: add `myFeature` to `FeatureFlagKey` and `false` to `DEFAULT_FEATURE_FLAGS`.
5. Frontend, gate the UI:
   - routes and menu entries: `featureFlag: 'myFeature'` on the `ROUTE_MANIFEST` entry (hidden from every menu, the
     route redirects to the context home);
   - components: `const { flags } = useFeatureFlags(); if (!flags.myFeature) ...`, and `enabled: flags.myFeature` on
     the queries of the hidden feature, so no request leaves while it is off.
6. Tests: endpoint 404 with the flag off (see `OtaPartnerApiFeatureFlagTests`), jobs not registered
   (`RecurringJobsFeatureFlagTests`), frontend menu without the entry (`route-manifest-nav.test.ts`).
7. Railway (test, then production): set `Features__MyFeature=true` only where the feature must be visible.

## Product owner steps (Railway)

Nothing to do for `OtaPartnerApi`, `AiSupplierDiscovery`, `RliProvider`, `ESignProvider`, `SupplierShowcaseBooking`,
`SupplierOnlinePayments`, `SupplierRequestAutoCancel` and `OrgTeam`: the default is off (`Features__OrgTeam` waits for
the account screens, AM-04). Do
**not** set `Features__OtaPartnerApi` (D10) or `Features__AiSupplierDiscovery` (D11) on test or production while the
decisions are in force, nor `Features__RliProvider` / `Features__ESignProvider` before a real provider client exists
(`docs/runbooks/rli.md`). `Features__SupplierShowcaseBooking` makes the public showcase of the suppliers show their services,
free times and price estimate (SP-09, `suppliers.md` section 22) and, with SP-10, take bookings (section 23): leave it unset until the
frontend screens (SP-12, SP-06), the booking (SP-10) and the customer's own area (SP-11) are deployed, `Suppliers__Showcase__PrivacyNoticeVersion`
and `Suppliers__CustomerIndexKey` are set, and the product owner has reviewed the texts about VAT and prices (D4) and the privacy notice (D14).
`Features__SupplierOnlinePayments` opens the suppliers' Stripe account routes (SP-14) and the creation of the payments (SP-15a): a payment
made online is recorded as paid by the webhook and the sync job of SP-15b (the Connect endpoint must listen to the events of `INFRA.md`). Leave it unset in production (in particular until the legal texts of the
supplier payments, D-C, are approved and the tax treatment of the commission, D4, is reviewed); set it to `true` on the test environment to
try the supplier's Stripe onboarding (`stripe.md`). After the first deploy with FD-20, the Hangfire dashboard (if enabled) no longer lists
`ota-sync-all` and `booking-pull-all`. Leave
`Features__SupplierRequestAutoCancel` unset until the web console and the mobile app show `Annullato` (`suppliers.md` section 21.8 and 21.11); after
it is on, `service-request-auto-cancel` appears in the recurring jobs.
