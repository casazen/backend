# Runbook: feature flags

Task FD-20 (defects A2-09, A9-17, R-10 webhook part, A8-16 OTA part, A9-15 OTA jobs; decision D10).
Task FD-21 adds `AiSupplierDiscovery` (defects A8-01, A8-14, A8-15, A4-26, A8-16 AI part; decision D11).
Task LT-01 adds `RliProvider` (defects A7-01, A7-21; decision D15; `docs/runbooks/rli.md`).

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

Nothing to do for `OtaPartnerApi`, `AiSupplierDiscovery` and `RliProvider`: the default is off. Do **not** set
`Features__OtaPartnerApi` (D10) or `Features__AiSupplierDiscovery` (D11) on test or production while the decisions are
in force, nor `Features__RliProvider` before a real provider client exists (`docs/runbooks/rli.md`). After the first deploy with FD-20, the Hangfire dashboard (if enabled) no longer lists
`ota-sync-all` and `booking-pull-all`.
