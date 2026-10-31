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
Task SP-14 (redesign wave) puts the supplier's Stripe Connect account behind `SupplierOnlinePayments`
(`docs/runbooks/stripe.md` § "Connect onboarding of the suppliers (SP-14)", `docs/runbooks/suppliers.md` section 25).
Task SP-15a (redesign wave) puts the **creation** of the payments of the supplier's work behind `SupplierOnlinePayments`
(`docs/runbooks/stripe.md` § "Services of the suppliers (SP-15)", `docs/runbooks/suppliers.md` section 26); SP-15b adds the
requests and reminders the daily job sends (and nothing else: the webhook, the sync and the refunds are not behind it).
Task SP-15c-a (redesign wave, decision VT-D1) adds **no flag**: the same `SupplierOnlinePayments` governs the **creation** of the payments CasaZen
holds (the block of a showcase customer's card at the booking, `ISupplierFundsService.CanAuthorizeAsync`); the block, the charge, the release, the
refunds, the webhook and the `service-funds` job of money already in flight are **not** behind it (`docs/runbooks/suppliers.md` section 38).
Task SP-16a (redesign wave) adds no flag. Two things to know when switching the two supplier flags on (`docs/runbooks/suppliers.md` section 29): the
showcase takes bookings only for a supplier whose `SupplierSettings.OnlineBookingEnabled` is true, a column that **only the showcase editor writes**
(`PUT api/supplier/settings` neither takes nor changes it, decision SP-D29), so `SupplierShowcaseBooking` alone opens nothing; and `GET api/supplier/checklist`
tells `verification` whatever the flag says and `paymentsActive` only while `SupplierOnlinePayments` is on (`null` otherwise).
Task SP-13a (redesign wave) adds no flag: the content endpoints of the supplier's showcase (`api/supplier/showcase/*`, `docs/runbooks/suppliers.md` section 33) are always on and nothing they save is public until the supplier publishes it. The
publication is the **only writer of `SupplierSettings.OnlineBookingEnabled`**, and it reads `SupplierOnlinePayments`: the bookings of a showcase open only with that flag on **and** the supplier's Stripe account able to take charges and payouts
(decision VT-D7), so with the flag off every showcase is read-only whatever `SupplierShowcaseBooking` says.
Task SP-04c (redesign wave) adds no flag: the urgent requests with a surcharge are the supplier's own settings (`api/supplier/availability/urgent`, `urgentEnabled` of a
service, off until the supplier turns them on), and what the customer sees of them (`urgent=true` on the slots, the estimate and the booking) is part of the public reads and
booking already behind `SupplierShowcaseBooking` (`docs/runbooks/suppliers.md` section 32).
Task SP-04b (redesign wave) adds no flag either: the brief's `SupplierQuotes` was not created (the functions of the wave are always on, override of 17/10). The supplier's quote
(`POST api/service-requests/{id}/quote`) and the host's answers are always available; the answers of a showcase customer (`api/public/supplier-bookings/quote/accept|reject`) are behind
`SupplierShowcaseBooking` like the rest of its area, and paying the work inside CasaZen stays behind `SupplierOnlinePayments` (`docs/runbooks/suppliers.md` section 34).
Task SP-16d (redesign wave) adds no flag: the notices of the supplier have their own three switches (columns of `SupplierSettings`, on by default, written by `PUT api/supplier/settings`), and the
reminder of the day before to the supplier is sent by the job that already exists (`docs/runbooks/suppliers.md` section 35).
Task CF-FIX (redesign wave, 2026-10-25) adds `ConcordatoFacile` (wanted by the product owner: the service is on in the test environment and
off in production until CasaZen confirms the trial run and the privacy framework to ConcordatoFacile; `docs/integrations/concordatofacile/README.md`).
Task SP-BKP (2026-10-30) adds `SupplierRequestBackups` (wanted by the product owner: a host's request goes to the supplier it chose and, at the same
moment, to up to three backups, declared as such; the chosen supplier has one hour, the first backup that accepts has the job if it does not;
`docs/runbooks/suppliers.md` section 36).

The functions of the redesign wave that had a flag of their own (the new interface, the org team and its activity log, the scheduled
change of the rental mode, the in-app notifications, the global search) are always on: they have no flag any more, and a
`Features__*` variable left in Railway for one of them is ignored. The guided lease draft of the long-term area (LR-12) never had one:
its endpoints and its retention job are always on (the one exception is `POST api/leases/drafts/concordatofacile`, the draft of the
ConcordatoFacile service, which is behind `ConcordatoFacile`). The "accesso aperto" switch (`docs/runbooks/open-access.md`) is
**not** a feature flag.

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
| `SupplierShowcaseBooking` | `Features__SupplierShowcaseBooking` | `false` (D34: booking from the public showcase of a supplier, `/fornitori/{slug}`; the product owner turns it on when the booking is ready and reviewed) | **SP-09 (public reads)**: `GET api/public/suppliers/{slug}/services`, `…/services/{serviceSlug}`, `…/slots` and `POST …/quote` (404 when off, before authentication and rate limiting), and the two members the page `GET api/public/suppliers/{slug}` gains, `services` and `medianResponseMinutes` (left out when off: the page reads exactly as before SP-09). The page itself keeps answering whatever the flag. **SP-10 (booking)**: `POST api/public/suppliers/{slug}/bookings` and `POST …/bookings/{id}/confirm-email` (404 when off, before authentication and rate limiting); the frontend booking steps read the key `supplierShowcaseBooking`. **On requires `Suppliers__Showcase__PrivacyNoticeVersion` and, outside Development and Testing, `Suppliers__CustomerIndexKey` (32+ characters): otherwise the service does not start** (`ShowcaseBookingOptionsValidator`). The jobs `service-request-expiry` and `service-request-reminders` are registered **whatever the flag says** (a booking made while it was on has to lapse and be reminded of after it is turned off). **SP-10b (the request on measure)**: `POST api/public/suppliers/{slug}/inquiries` and `POST …/inquiries/{id}/confirm-email` (404 when off, before authentication and rate limiting; no flag of their own, the override of 17 October forbids new ones: same startup requirements as SP-10; `suppliers.md` section 34). **SP-11 (the customer's own area)**: `POST api/public/supplier-bookings/lookup`, `…/cancel`, `…/reschedule`, `…/proposal/accept` and `…/proposal/reject` (404 when off, before authentication and rate limiting): **turn it on only where SP-10 and SP-11 are both deployed**, otherwise a customer can book and cannot manage the booking its e-mails send it to. `suppliers.md` sections 22, 23 and 24. **SP-10c**: `POST api/public/supplier-bookings/alternatives` (the other suppliers offered to the customer of a request nobody answered; the same gate, proof and limits as `lookup`; the list is empty while `SupplierOnlinePayments` is off, because no showcase takes bookings then; `suppliers.md` section 34.11); the reminders to the supplier of a request on measure run in the always-on `service-request-expiry` job, whatever the flag says. **The supplier's service catalog** (`api/supplier/services`, section 19) **is not behind this flag**: every supplier has one. |
| `SupplierRequestAutoCancel` | `Features__SupplierRequestAutoCancel` | `false` (D8: a new request that nobody answers is cancelled by CasaZen after the response window; it changes what happens to the requests that exist, so the product owner turns it on once the console and the mobile app know the status `Annullato`) | The recurring job `service-request-auto-cancel` (every 10 minutes; registered only with the flag on and removed with `RemoveIfExists` otherwise) and, inside `ServiceRequestAutoCancelService`, the run itself (a run triggered by hand with the flag off does nothing). With the flag off the deadline `ResponseDueAt` is still recorded on every new request, but **nothing is ever cancelled by time**. The window is `Suppliers__ServiceRequests__HostResponseMinutes` (120). Turning it on cancels at the first run every open request that is already overdue (500 per run, every 10 minutes). The manual cancellation (`POST …/cancel`) is **not** behind this flag. Frontend key `supplierRequestAutoCancel` with no consumer yet. |
| `SupplierOnlinePayments` | `Features__SupplierOnlinePayments` | `false` (D2: payment of a supplier's work inside CasaZen, direct charge on the supplier's Stripe account with the platform commission; the legal texts, D-C, and the tax treatment of the commission, D4, still need review before it goes live) | **SP-14**: `api/supplier/payments/*` (the supplier's Stripe Connect account: state, onboarding link, Express Dashboard link; 404 when off, before authentication; `stripe.md` § "Connect onboarding of the suppliers (SP-14)"). **Not behind it**: the processing of Stripe's events (`account.updated` of a connected account is applied whatever the flag says), the host's `api/connect/*`. **SP-15a also gates the creation of the payments**: with the flag off a request is never taken as an online one (`paymentMode: Manual`), a request taken online and completed after the flag was switched off falls back to `Manual` (the host marks it paid, nothing is stuck) and `POST api/supplier/requests/{id}/payment-request` is a 404 before authentication. **Not behind it, on purpose** (money in flight): the payer's page and payment sessions (`api/public/service-payments/*`), the host's session, the supplier's offline record (`payment/offline`, the exception when online payments are not available) and, from SP-15b, the webhook of the payments. **SP-15b**: the daily job `service-payment-reminders` sends the pending requests and the reminders only with the flag on (with it off it only flags the late payments), and `SendPendingPaymentRequestsJob` sends nothing. The recurring jobs are always scheduled; the processing of Stripe's events, `service-payment-sync` and the admin tools (`api/admin/supplier-payments`, refunds, the commission of a supplier, the export) are **not** behind it, because they handle money that already exists. Without the flag the manual flow ("Segna pagato" by the host) is the only one. **Keep it off in production** until the legal texts of the supplier payments (D-C) and the tax treatment of the commission (D4) are approved; with SP-15b deployed it can be tried in Staging with Stripe in test mode. Frontend key `supplierOnlinePayments`. |
| `SeoSignupCta` | `Features__SeoSignupCta` | `false` (SEO-INVITE, 2026-10-13: CasaZen is on **private invitation**, prices and the onboarding of new customers are not defined) | The call to action "Pubblica la tua casa" of the public SEO pages: with the flag off `cta` is `null` in `GET /api/public/content/affitti-brevi/{region}/{comune}` and `…/tassa-soggiorno/{comune}`, the crawler page (`api/seo` → `GET /api/public/seo/guides/…`) has no link to `/signup`, and the web app shows no button (so no `cta_click` / `signup_start` events). **It does not close the registration**: `/signup` and `/register` still exist for people who have a link. Close it where it is decided: Auth0 → Authentication → Database → "Disable Sign Ups" (invited users are created by an admin or by the invitation flow), and `Suppliers__AnyComune=false` with no `Suppliers__PilotComuni__*` so that suppliers join only by invite (`suppliers.md` §3). Turn the flag on when the product owner opens the registration. |
| `AgencyTier` | `Features__AgencyTier` | `false` (E01/F01/US01, ADR-010: B2B agency tier and features for property management agencies) | Gates all B2B agency features including property owner registry, owner portal access, monthly statements, withholding tax management, wire transfers, invoicing, internal staff activities, and trusted supplier management. With the flag off: agency endpoints (`api/agency/*`) return 404 before authentication, `agency.*` policies return 403 `feature_requires_agency_tier`, and frontend B2B routes are hidden. The `PlanTier.Agency` enum value exists regardless of the flag, but all features requiring `Org.PlanTier == PlanTier.Agency` are gated. Turn on only after agency endpoints and frontend B2B area are deployed. |
| `SupplierRequestBackups` | `Features__SupplierRequestBackups` | `false` (SP-BKP, 2026-10-29/30, wanted by the product owner: «hanno un'ora per confermare la presa in carico, altrimenti altri 3 fornitori prenderanno il lavoro al posto del fornitore selezionato»; it changes what the suppliers receive and what the host sees, so the product owner turns it on in the test environment when the console shows the backups) | **Only the creation of groups**: with the flag on, a host's request (`POST api/service-requests` and `POST api/long-rent/service-requests`, also the turnover request of a check-out) is also sent to up to `Suppliers__ServiceRequests__MaxBackupSuppliers` (3) backups, unless the org turned the rule off (`PUT api/orgs/me/service-request-settings`, on by default) or the request asks to go alone (`useBackupSuppliers: false`), nobody else in the comune is free at that time, or the supplier accepted the request automatically (SP-04d). **Not behind it, on purpose** (a group that exists has to be followed to the end): the recurring job `service-request-groups` (always registered), the suppliers' answers (`POST api/service-requests/{id}/accept-as-backup` and the take, refusal and cancellation of a member) and the `group` the host reads, so turning the flag off never leaves a request hanging. `Suppliers__ServiceRequests__PrimaryResponseMinutes` (60) and `…__BackupResponseMinutes` (60) are the two windows. For the requests that have no backup keep `Features__SupplierRequestAutoCancel` on as well: it is the rule that lapses them (`HostResponseMinutes`). Frontend key `supplierRequestBackups`. |
| `ConcordatoFacile` | `Features__ConcordatoFacile` | `false` (CF-FIX, 2026-10-25, wanted by the product owner: **on in the test environment, off in production** until CasaZen confirms to ConcordatoFacile the trial run and the privacy framework, v1.3 R24) | Every action of `ConcordatoFacileController`: `POST api/leases/{id}/concordatofacile/checkout`, `GET …/access`, `POST …/consume-gift`, `POST …/link`, `GET …/status`, and `GET api/concordatofacile/practices/{practiceId}/lease` (the lookup of the upload page), plus `POST api/leases/drafts/concordatofacile` (the route of the minimal ConcordatoFacile draft; the app's "Parto da zero" no longer uses it: it saves an ordinary draft with `POST api/leases/drafts` and `PATCH`, which are not behind the flag): 404 `not_found` while off, before authentication. **Not behind it**: the processing of the Stripe webhooks (a CF payment or refund already made is recorded whatever the flag says), the status client, the startup validation of the `ConcordatoFacile` options (`StripePriceId`, `BiennialPriceIds` and `LinkSecret` stay required outside Development and Testing), the gift granted by a biennial subscription and the admin controller (`api/admin/orgs/{orgId}/concordatofacile`). The frontend reads `concordatoFacile` from `GET /api/public/features`: off, the contract page hides the payment panel and the route `/app/long-rent/leases/:id/concordatofacile` goes back to the contract (the draft banner keeps its disabled button and its "coming soon" line), the "Parto da zero" card of the new contract is closed ("Disponibile a breve"), and the upload page opened from the partner's button falls back to choosing the contract by hand. The process, the variables and the way to try it: `docs/integrations/concordatofacile/README.md` |

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

Nothing to do for `SeoSignupCta` (off: CasaZen is on private invitation, SEO-INVITE), `OtaPartnerApi`, `AiSupplierDiscovery`, `RliProvider`, `ESignProvider`, `SupplierShowcaseBooking`,
`SupplierOnlinePayments`, `SupplierRequestAutoCancel`, `SupplierRequestBackups` and `AgencyTier`: the default is off. Do
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
it is on, `service-request-auto-cancel` appears in the recurring jobs. `Features__SupplierRequestBackups` (SP-BKP) is the switch of the backup
suppliers: set it to `true` on the **test** environment when the product owner wants to try the rule (Railway → environment `test` → Variables;
the change redeploys the service) and leave it unset in production until the web console and the apps of the suppliers show the backups
(`suppliers.md` section 36). The job `service-request-groups` is in the recurring jobs whatever the flag says.

`Features__ConcordatoFacile` (CF-FIX) is the one switch of the ConcordatoFacile service: set it to `true` on the **test** environment (Railway →
environment `test` → Variables → `Features__ConcordatoFacile=true`; the change redeploys the service) and leave it **unset or `false` in production**
until CasaZen has confirmed to ConcordatoFacile the trial run of the upload page and the privacy framework (v1.3 R24,
`docs/integrations/concordatofacile/README.md`). To turn it on, first check that the variables of `deploy-checklist.md` § 2.8 for ConcordatoFacile are set
on that environment (`ConcordatoFacile__StripePriceId`, `__BiennialPriceIds__0..2`, `__LinkSecret`, `__BaseUrl`, `__StatusApiKey`): the flag opens the
endpoints, it does not configure them, and without the status key the status API stays off with a warning in the log. A payment made while the flag was
on is still recorded after it is turned off (the webhook is not behind it).

What the functions without a flag ask of an operator is in their runbooks: [org-team.md](org-team.md) (§ 42: the authorization cache with
several instances; § 35: the privacy notice), [property-rental-mode.md](property-rental-mode.md) § 8 (the calendar block, the draft leases
that cannot be deleted yet, rollback), [in-app-notifications.md](in-app-notifications.md) § 6 (checks and rollback),
[global-search.md](global-search.md) (what the migration `AddGlobalSearchKeys` locks while it runs, the rate limit, the rollback) and
[long-rent-lease-draft.md](long-rent-lease-draft.md) § 14 (the interface must know the four new event types).
