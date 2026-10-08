# Runbook: feature flags

Task FD-20 (defects A2-09, A9-17, R-10 webhook part, A8-16 OTA part, A9-15 OTA jobs; decision D10).
Task FD-21 adds `AiSupplierDiscovery` (defects A8-01, A8-14, A8-15, A4-26, A8-16 AI part; decision D11).
Task LT-01 adds `RliProvider` (defects A7-01, A7-21; decision D15; `docs/runbooks/rli.md`).
Task LT-02 adds `ESignProvider` (defects A7-02, A7-16, A7-20; decision D15; `docs/runbooks/rli.md` § Contract signature).
Task SP-02 (redesign wave) adds `SupplierShowcaseBooking` and `SupplierOnlinePayments` (decisions D34 and D2;
`docs/runbooks/suppliers.md` section 19).
Task SP-04 (redesign wave) adds `SupplierRequestAutoCancel` (decision D8; `docs/runbooks/suppliers.md` section 21).
Task SP-09 (redesign wave) puts the public reads of the supplier showcase behind `SupplierShowcaseBooking` (`docs/runbooks/suppliers.md` section 22).

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
| `SupplierShowcaseBooking` | `Features__SupplierShowcaseBooking` | `false` (D34: booking from the public showcase of a supplier, `/fornitori/{slug}`; the product owner turns it on when the booking is ready and reviewed) | **SP-09 (public reads)**: `GET api/public/suppliers/{slug}/services`, `…/services/{serviceSlug}`, `…/slots` and `POST …/quote` (404 when off, before authentication and rate limiting), and the two members the page `GET api/public/suppliers/{slug}` gains, `services` and `medianResponseMinutes` (left out when off: the page reads exactly as before SP-09). The page itself keeps answering whatever the flag. SP-10 will gate the booking endpoints with the same flag, and the frontend booking steps read the key `supplierShowcaseBooking`. `suppliers.md` section 22. **The supplier's service catalog** (`api/supplier/services`, section 19) **is not behind this flag**: every supplier has one. |
| `SupplierRequestAutoCancel` | `Features__SupplierRequestAutoCancel` | `false` (D8: a new request that nobody answers is cancelled by CasaZen after the response window; it changes what happens to the requests that exist, so the product owner turns it on once the console and the mobile app know the status `Annullato`) | The recurring job `service-request-auto-cancel` (every 10 minutes; registered only with the flag on and removed with `RemoveIfExists` otherwise) and, inside `ServiceRequestAutoCancelService`, the run itself (a run triggered by hand with the flag off does nothing). With the flag off the deadline `ResponseDueAt` is still recorded on every new request, but **nothing is ever cancelled by time**. The window is `Suppliers__ServiceRequests__HostResponseMinutes` (120). Turning it on cancels at the first run every open request that is already overdue (500 per run, every 10 minutes). The manual cancellation (`POST …/cancel`) is **not** behind this flag. Frontend key `supplierRequestAutoCancel` with no consumer yet. |
| `SupplierOnlinePayments` | `Features__SupplierOnlinePayments` | `false` (D2: payment of a supplier's work inside CasaZen, direct charge on the supplier's Stripe account with the platform commission; the legal texts, D-C, and the tax treatment of the commission, D4, still need review before it goes live) | **Nothing yet**: SP-02 only introduces and exposes the flag. It will gate the creation of the payment requests and the supplier's Stripe onboarding (SP-14, SP-15); the public payment page and the webhook of money already in flight will stay active. Without it the manual flow ("Segna pagato" by the host) is the only one. Frontend key `supplierOnlinePayments`. |

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
`SupplierOnlinePayments` and `SupplierRequestAutoCancel`: the default is off. Do
**not** set `Features__OtaPartnerApi` (D10) or `Features__AiSupplierDiscovery` (D11) on test or production while the
decisions are in force, nor `Features__RliProvider` / `Features__ESignProvider` before a real provider client exists
(`docs/runbooks/rli.md`). `Features__SupplierShowcaseBooking` makes the public showcase of the suppliers show their services,
free times and price estimate (SP-09, `suppliers.md` section 22) and, with SP-10, take bookings: leave it unset until the
frontend screens (SP-12, SP-06) and the booking (SP-10) are deployed and the product owner has reviewed the texts about VAT and
prices (D4). `Features__SupplierOnlinePayments` has no effect until its task (SP-15) is deployed; leave it unset (until the legal
texts of the supplier payments, D-C, are approved and the tax treatment of the commission, D4, is reviewed). After the first deploy with
FD-20, the Hangfire dashboard (if enabled) no longer lists `ota-sync-all` and `booking-pull-all`. Leave
`Features__SupplierRequestAutoCancel` unset until the web console and the mobile app show `Annullato` (`suppliers.md` section 21.8 and 21.11); after
it is on, `service-request-auto-cancel` appears in the recurring jobs.
