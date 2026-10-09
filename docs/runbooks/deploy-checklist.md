# Runbook: deploy checklist (every variable, where it goes, what happens without it)

Task DEPLOY-CFG (decision D9: code + runbook, the app reports what is missing, the product owner applies it). This is the
**single list** of the configuration of the three repositories. It was derived from the code (`appsettings*.json`,
`Program.cs`, the `ValidateOnStart` validators, the health checks, `vite.config.ts`, `app.config.ts`, `config-rules.js`), not
from memory, and `DeployChecklistConsistencyTests` fails when a variable written here does not exist in the code or when a
configuration section of the code is not mentioned here. Detail per area stays in the linked runbooks; where a runbook and
this file disagree, this file was checked against the code later (see § 9 for the corrections made).

> **Hosting in revisione (2026-10-02):** il PO ha cancellato Railway; dove questa checklist dice "Railway" va letto "l'host del backend", in attesa dell'analisi del task HOSTING (`free-hosting-analysis.md`). Le variabili restano quelle lette dal codice.

Nothing in this file is a secret: examples are placeholders. Real values live only in the panels named in the "Where" column
and in the password manager, never in the repository (`.claude/rules/security.md`).

## 0. How to read it

| Word | Meaning |
|---|---|
| **Startup** | The container exits with the list of problems and Railway keeps the previous deployment. Applies outside `Development`/`Testing`, i.e. on both Railway environments ([`health-checks.md`](health-checks.md)). |
| **degraded** | The API starts; `GET /api/health/ready` answers 200 and the named check is `degraded` (admins see the description with the variable names, anonymous callers only the status). CI prints a warning per degraded check. |
| **build** | The Vercel or EAS build fails and the previous deployment stays live. |
| **inert** | Present in a committed file but read by no code: setting it changes nothing (§ 8). |

| Place | Environments |
|---|---|
| **Railway** | environment `test` = `ASPNETCORE_ENVIRONMENT=Staging`, environment `production` = `Production`; the variables of § 2, set per environment (Variables tab). Never the same value in both when the column says "per environment". |
| **Vercel** | **Preview** (PR previews and the `develop` test deployment) and **Production** (`main`). Set the variable per environment, never "All environments" ([`auth0.md`](auth0.md) § 1.1). |
| **EAS** | environments `preview` and `production` of the Expo project (`APP_VARIANT` of the build profile, `eas.json`). |
| **GitHub** | repository Variables / Secrets (Settings → Secrets and variables → Actions). |

Verified-against-code files: `Casazen.Web/Program.cs` and `Extensions/*`, `Configuration/*`, `HealthChecks/*`;
`Casazen.Infrastructure/{Storage,Email,Services,External,Push}`; frontend `vite.config.ts` and `src/config/*`; mobile
`app.config.ts` and `src/config/*`. Committable examples that mirror this file: `backend/secrets/railway.test.variables.example.json`,
`backend/secrets/railway.prod.variables.example.json`, `backend/secrets/vercel.variables.example.json`,
`backend/Casazen.Web/appsettings.Development.example.json`, `frontend/.env.example`, `mobile/.env.example`
(`DeployChecklistConsistencyTests` checks that every key of the Railway and Vercel examples is documented below).

## 1. Pre-deploy blockers (do in this order)

Steps 1-9 make the API start and the apps build; steps 10-13 are what the health check reports as `degraded` and what the
product owner has to decide; 14-17 are mobile, CI, one-off data and the optional custom domains. Work on **test first**, check the "Done when" column, then repeat on
production. Items marked **PO** need a decision or a text from the product owner, not a panel click.

| # | Blocker | Where | What to set / do | If skipped | Done when | Task · runbook |
|---|---|---|---|---|---|---|
| 1 | **Environment name** | Railway, both | `ASPNETCORE_ENVIRONMENT=Staging` on `test`, `Production` on `production` | Unset = ASP.NET default `Production`: the test environment with test-mode Stripe keys does not start (`… is a Stripe test-mode key but ASPNETCORE_ENVIRONMENT is Production`). `Development`/`Testing` switch **every** check below off and open Swagger and the Hangfire dashboard: never on Railway | Startup log shows no billing error; `/api/health/ready` answers | PL-11 · [`stripe.md`](stripe.md) § Environments |
| 2 | **Database and migrations** | Supabase + Railway | One project, schemas `casazen_test` / `casazen_prod` (or a separate project for production with the right GRANTs); `ConnectionStrings__DefaultConnection` with `SearchPath=` of the environment. The API applies pending EF migrations at startup (`db.Database.Migrate()`), so the role needs DDL rights on its schema | Empty value: **startup**. Unreachable / paused project / pending migration: `database: unhealthy` (503) | `database: healthy` | FD-11, FD-12 · [`INFRA.md`](../INFRA.md) § Supabase |
| 3 | **Hangfire schema** | Railway, both | `Hangfire__Schema=hangfire_casazen_test` / `hangfire_casazen_prod` (different per environment; derived from `SearchPath` when unset). Check and clear the jobs of the old shared schema `hangfire` first | Production without `SearchPath` and without the variable, or `hangfire` together with a `SearchPath`: **startup**. A shared schema = test and production consume the same queue | Log `Hangfire storage schema: hangfire_casazen_…`; `hangfire: healthy` | FD-11 · [`hangfire.md`](hangfire.md) § 1-2 |
| 4 | **Auth0 tenants, API, M2M** | Auth0 + Railway | One tenant per environment. `Auth0__Domain` (host only, `tenant.eu.auth0.com`), `Auth0__Audience` (API identifier), and the M2M client `Auth0__ManagementClientId` / `Auth0__ManagementClientSecret` (+ `Auth0__ManagementApiDomain` only with a custom domain). Create the **Native** application for the mobile app and test the login on a real device. Repair by hand the users that lost a role before the fix | `Domain`/`Audience`: **startup**. M2M missing: `auth0: degraded`, onboarding answers `rolesSynced: false`, role-management endpoints answer 502 `auth0_management_not_configured` | `auth0: healthy`; mobile login works | FD-14, MO-01, PL-11 · [`auth0.md`](auth0.md) § 1-7, § 9 |
| 5 | **Data Protection certificate** (**test and production**) | Railway, both | One `.pfx` per environment, base64 in `DataProtection__CertificatePfxBase64`, password in `DataProtection__CertificatePassword`; keep both in the password manager (a lost certificate = the encrypted columns cannot be read again) | **Startup** (CO-14): the key ring that protects guest documents and Questura credentials would sit in clear next to the data | Log `Data Protection keys: persisted in the database, encrypted with the configured certificate.` | CO-14, FD-07 · [`storage.md`](storage.md) § 4, [`encryption.md`](encryption.md) |
| 6 | **Object storage** | Supabase + Railway, both | Two buckets per environment (public for photos, private for documents) and an S3 access key; the eight `Storage__*` variables of § 2.6 | **Startup**; `Storage__Provider=FileSystem` is refused outside Development/Testing | `storage: healthy`; upload a photo | FD-07 · [`storage.md`](storage.md) |
| 7 | **Email (Resend)** | Resend + Railway, both | Verify the sending domain (SPF/DKIM), then `Email__ApiKey` (`re_…`) and `Email__FromAddress` (an address of that domain, never `@resend.dev`) | **Startup** | `email: healthy`; send a test mail | FD-13 · [`email.md`](email.md) |
| 8 | **Public domain of the web app** (D3) | Railway + Vercel | **PO: choose the final production domain before the release PR to `main`.** `App__PublicSiteBaseUrl` (https, no path) on each Railway environment; the same host as `VITE_PUBLIC_SITE_URL` on Vercel Production. Leave `Seo__PublicBaseUrl` unset (alias) | Backend: **startup** (also if the alias differs). Frontend Production: **build** fails (`robots.txt` declares the sitemap on it) | `email: healthy`; `GET /api/public/sitemap.xml` uses the domain | SE-02, SE-03 · [`seo-domain.md`](seo-domain.md) |
| 9 | **CORS** | Railway, both | `Cors__AllowedOrigins` = the web app origin(s) of this environment (the origin of `App__PublicSiteBaseUrl` is always added); `Cors__VercelPreviewPattern` on **test only** | Neither an origin nor `App__PublicSiteBaseUrl`: **startup**. Malformed origin or regex: **startup**. A missing preview pattern: browser calls from PR previews are rejected | Login from the web app works; no CORS error in the console | FD-17 · [`cors-security-headers.md`](cors-security-headers.md) |
| 10 | **Public URL of this API** | Railway, both | `App__ApiBaseUrl` = the Railway public URL **of this environment** (https, no trailing path). Different on test and production | `api-url: degraded`. The iCal export links that hosts paste into Airbnb/Booking would point to `https://localhost:5001` (before this task: to the production host on every environment) | `api-url: healthy`; `GET /api/properties/{id}/ical/export-url` shows this host | DEPLOY-CFG · [`ical.md`](ical.md) |
| 11 | **Stripe** | Stripe + Railway | Test mode on `test`, live mode on `production`: `Stripe__SecretKey`, `Stripe__PublishableKey`, the **two** webhook endpoints with their signing secrets `Stripe__WebhookSecret` / `Stripe__ConnectWebhookSecret` (API version `2025-12-15.clover`), and the three plan prices `Billing__Prices__Starter/Pro/Scale` (same mode as the keys). **PO: live prices and the billing/e-invoicing prerequisites of PL-13** | Keys / secrets missing: `stripe: degraded`; webhook answers 500 `stripe_webhook_not_configured` (Stripe retries); wrong mode: **startup**. Production with a secret key and a plan without a Price id: **startup**; test: that plan answers 422 `billing_plan_unavailable`. Production live checkout also stops at the billing gate (409 `billing_gate_closed`) until the PL-13 prerequisites are set | `stripe: healthy`; both webhook endpoints answer 200 to a test event | PL-10, PL-11, PL-13 · [`stripe.md`](stripe.md), [`INFRA.md`](../INFRA.md) § Stripe |
| 12 | **Legal documents and subprocessors** | repo + Railway | **PO: provide the texts** (ToS, Privacy, DPA, D14) as files `Casazen.Web/LegalDocuments/{tos,privacy,dpa}/{version}.it.html` (or an external copy `Legal__Documents__{Tos,Privacy,Dpa}__DocumentUrl`), the date in force `Legal__Documents__{…}__EffectiveAt`, and decide the regions of the Supabase project, Auth0 tenant and Railway service for the subprocessor list; verify the committed entity / transfer facts (PL14-SUBP) | `legal: degraded` naming the missing text, date or provider. Public pages say "in preparazione"; hosts accept a version nobody can read | `legal: healthy`; `/legale/*` pages show the text | PL-14, PL14-SUBP · [`legal-documents.md`](legal-documents.md) |
| 13 | **Vercel variables** | Vercel | Preview and Production: `VITE_API_BASE_URL`, `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE`; Production also `VITE_PUBLIC_SITE_URL`. The two environments point to **different** API hosts and Auth0 tenants | **build** fails (`src/config/vercel-build-env.ts`, DEPLOY-CFG); before this task a missing API URL silently called the production API | Preview and Production deployments succeed; `prod-deploy-smoke` passes | FD-17, SE-02, PL-11 · [`INFRA.md`](../INFRA.md) § Vercel |
| 14 | **Mobile (EAS)** | Expo + Firebase + Apple | Per EAS environment `preview` / `production`: the five `EXPO_PUBLIC_*`; project-wide `EAS_PROJECT_ID`; Android `GOOGLE_SERVICES_JSON` (file variable) + FCM v1 key and APNs key in the EAS credentials | **build** fails (production Android also without `GOOGLE_SERVICES_JSON`); a release bundle without the variables shows only `CONFIG_ENV_INVALID` | Install the build: login screen, calendar loads, push token registers | MO-01, MO-03, MO-04 · [`mobile-release.md`](mobile-release.md) |
| 15 | **CI and Railway settings** | GitHub + Railway | GitHub Variables `RAILWAY_TEST_URL`, `RAILWAY_PROD_URL`; Railway: **Wait for CI OFF** on both environments, Healthcheck Path `/api/health/ready`; branch protection on `develop`/`main` requiring the CI check | `verify-test` / `verify-prod` fail at once without the URLs. **Wait for CI ON deadlocks the deploy** (the verify job waits for the deploy, the deploy waits for it): this file and `INFRA.md` are right, the open-questions register item that says "Attivare Wait for CI" is not | Push to `develop`: `verify-test` green | FD-01, FD-12 · [`health-checks.md`](health-checks.md), [`ci-backend.md`](ci-backend.md) |
| 16 | **Data to load / one-off checks** | Supabase + admin API | Before the production deploy: `SELECT count(*) FROM "SupplierJobs"` (the table is dropped); the other destructive migrations of § 7; before the deploy that contains `AddOrgMembership` (it backfills the owners), the read-only dry-run queries of [`org-team.md`](org-team.md) § 6. After: import the official Alloggiati Web code tables (`POST /api/admin/alloggiati/code-tables/{table}`), the ISTAT comuni registry (SU-04, **blocked**: needs the official CSV) | Data loss on the dropped table; Alloggiati validation without code tables; comuni registry stays incomplete | Queries of the runbooks return the expected rows | SU-11, CO-12, SU-04, AM-01 · [`suppliers.md`](suppliers.md) § 10.3, [`alloggiati.md`](alloggiati.md), [`org-team.md`](org-team.md) |
| 17 | **Custom domains for Pro hosts** (optional) | Vercel + Railway | `Vercel__ApiToken` (a token scoped to the web app project), `Vercel__ProjectId` (+ `Vercel__TeamId`); optionally `PublicHost__BaseDomain` with its wildcard DNS record for CasaZen subdomains | `vercel: degraded`; custom domains stay `Pending`; without `PublicHost__BaseDomain` the "subdomain" mode answers 422 `subdomains_not_configured` | `vercel: healthy`; a test domain turns `Verified` and is added to the Vercel project | BK-17, SE-03 · [`seo-domain.md`](seo-domain.md) § 10 |

## 2. Backend: Railway variables

Format: Railway uses the `<Section>__<Key>` form of `Section:Key` (a double underscore for each colon; array items as `<Section>__<Key>__0`).
"Req." column: **S** = startup fails without it on Railway, **D** = starts but a health check is `degraded` or a feature is
off, **O** = optional, **PO** = a decision of the product owner with no default (the feature is off until set).
"Test / Prod" says whether the value differs per environment. Examples are placeholders, never real values.

### 2.1 Platform and runtime

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | S | `Staging` / `Production` | `Staging` | See § 1 step 1 | PL-11 · [`stripe.md`](stripe.md) |
| `ASPNETCORE_URLS` | O | same | `http://+:8080` | Set by the `Dockerfile`; Railway terminates TLS, the container listens on plain HTTP. The service's networking target port is 8080 | [`INFRA.md`](../INFRA.md) |
| `PORT` | O | same | `8080` | Set by the `Dockerfile`; the app listens on `ASPNETCORE_URLS`, not on `PORT` | [`INFRA.md`](../INFRA.md) |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | never | — | — | **Do not set**: it would replace the trusted-proxy handling of `ForwardedHeaders__*` | FD-10 · [`proxy-ip.md`](proxy-ip.md) |
| `RAILWAY_GIT_COMMIT_SHA` | set by Railway | — | (hex SHA) | Exposed as `commit` by the health endpoints; without it (a `railway up` deploy) `verify-deploy.sh` cannot verify the deployment. `GIT_COMMIT_SHA` is the alternative for another host | FD-12 · [`health-checks.md`](health-checks.md) |
| `RAILWAY_PROJECT_ID`, `RAILWAY_ENVIRONMENT_NAME` | set by Railway | — | — | Their presence lists Railway among the subprocessors | PL14-SUBP · [`legal-documents.md`](legal-documents.md) |
| `RAILWAY_REPLICA_REGION` | set by Railway | — | `europe-west4` | Read as the processing region of the Railway subprocessor entry; never typed by hand | PL14-SUBP |

### 2.2 Database and background jobs

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `ConnectionStrings__DefaultConnection` | S | per environment | `Host=aws-0-<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<ref>;Password=<pw>;SearchPath=casazen_test;SSL Mode=Require` | Empty: **startup**. Wrong or paused database: `database: unhealthy` (503). A `Host=localhost` default exists only for local work | FD-12 · [`INFRA.md`](../INFRA.md) |
| `Hangfire__Schema` | S\* | per environment | `hangfire_casazen_test` | \*Required when the connection string has no `SearchPath` (production: **startup**); otherwise derived as `hangfire_<SearchPath>`. Set it explicitly anyway. Lowercase letters, digits, `_`; never `hangfire` with a `SearchPath` | FD-11 · [`hangfire.md`](hangfire.md) |
| `Hangfire__DistributedLockTimeoutMinutes` | O | same | `30` | Default 30; must exceed the longest recurring job | FD-11 |
| `Hangfire__DashboardEnabled` | O | `false` / `false` | `false` | Default off (on in Development). When on: reachable only with the `X-Hangfire-ApiKey` header or an Admin role | FD-11 |
| `Hangfire__DashboardApiKey` | O | per environment | `<random 32+ chars>` | Secret. Only meaningful with the dashboard enabled | FD-11 |
| `Hangfire__WorkerCount` | O | same | `4` | Hangfire workers, default **4** (Hangfire's own default is 20). Whole number 1-50, anything else: **startup**. The free Supabase session pooler grants about 15 connections, and with 20 workers the API opened 26 at rest | HOSTING · [`free-hosting-analysis.md`](free-hosting-analysis.md) |
| `Database__MaxPoolSize` | O | same | `6` | Npgsql pool of the EF contexts (requests and jobs), default **6**, 1-500. A `Maximum Pool Size` written in the connection string wins (use it for a direct database) | HOSTING |
| `Database__MinPoolSize` | O | same | `0` | Default 0; must not exceed `Database__MaxPoolSize` (**startup**) | HOSTING |
| `Database__HangfireMaxPoolSize` | O | same | `8` | Pool of Hangfire's storage (its own pool, `Application Name=casazen-hangfire`). Default `2 x Hangfire__WorkerCount`. Below `WorkerCount + 2` the readiness check warns | HOSTING |
| `Database__ConnectionBudget` | O | same | `15` | Connections the database grants to this user. Default: 15 on a Supabase session pooler host, 60 on a direct `db.<ref>.supabase.co` host, unchecked elsewhere (`0`). When the pools can exceed it, `db-connections` is `degraded` | HOSTING |
| `Seo__BootstrapOnStartup` | O | `true` default | `true` | `true`: the first start with no SEO page queues draft generation of every registry comune, once per environment (drafts only). `false` skips it | FD-21, SE-01 · [`ai.md`](ai.md), [`seo-domain.md`](seo-domain.md) |

### 2.3 Authentication (Auth0)

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Auth0__Domain` | S | different tenant per environment | `<tenant>.eu.auth0.com` | Host only (no `https://`, no path), not a placeholder: **startup**. JWT issuer is `https://{Domain}/` | FD-12, PL-11 · [`auth0.md`](auth0.md) § 1 |
| `Auth0__Audience` | S | same identifier | `https://casazen-api` | **Startup**. Must equal `VITE_AUTH0_AUDIENCE` and `EXPO_PUBLIC_AUTH0_AUDIENCE` | FD-12 |
| `Auth0__ManagementClientId`, `Auth0__ManagementClientSecret` | D | per tenant | `<M2M client id>` / secret | M2M client of the tenant. Missing: `auth0: degraded`, no role sync (502 `auth0_management_not_configured`, `rolesSynced: false`) | FD-14 · [`auth0.md`](auth0.md) § 4-5 |
| `Auth0__ManagementApiDomain` | O | per tenant | `<tenant>.eu.auth0.com` | Needed only when `Auth0__Domain` is a custom domain: the Management API audience is always the canonical domain | FD-14 |
| `Auth0__ManagementApiToken` | O | — | — | Deprecated static token, read only without the M2M client; `auth0: degraded` while it is the only credential. Delete it | FD-14 |
| `Authorization__UserCacheSeconds` | O | same | `60` | Per-user authorization snapshot cache; `0` disables it | A4-30 · [`auth0.md`](auth0.md) |

### 2.4 Public URLs, CORS, proxy and rate limits

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `App__PublicSiteBaseUrl` | S | per environment | `https://<web app host>` | https, no query: **startup**. Base of every email link, SEO canonical URL, sitemap, Stripe return page and a CORS origin. No default in code (D3) | SE-02 · [`seo-domain.md`](seo-domain.md) |
| `Seo__PublicBaseUrl` | O | — | — | Alias of the above, used only when it is empty; a different value: **startup**. Leave unset | SE-02 |
| `App__ApiBaseUrl` | D | per environment | `https://<railway host of this environment>` | `api-url: degraded`; iCal export links built on `https://localhost:5001`. No default in `appsettings.json` since DEPLOY-CFG | DEPLOY-CFG · [`ical.md`](ical.md) |
| `Cors__AllowedOrigins` | S\* | per environment | `https://<web app host>` | \*Required unless `App__PublicSiteBaseUrl` already gives the origin. Exact origins, comma separated or `…__0`; no path, no wildcard; the committed example `https://your-frontend…` counts as missing | FD-17 · [`cors-security-headers.md`](cors-security-headers.md) |
| `Cors__VercelPreviewPattern` | O | test only | `<project>-git-[a-z0-9-]+-<team-slug>` | Regex of the whole label before `.vercel.app` of our own project; empty = no preview allowed. A bad regex: **startup** | FD-17 |
| `PublicHost__BaseDomain` | O | per environment | `<wildcard domain>` | Base domain of org booking subdomains; unset = the "subdomain" mode answers 422 `subdomains_not_configured`. The other `PublicHost__*` keys are optional tuning with defaults in `PublicHostOptions`: `PublicHost__ReservedSubdomains`, `PublicHost__VercelCnameTarget` (`cname.vercel-dns.com`), `PublicHost__AcceptedCnameSuffixes` (`.vercel-dns.com`), `PublicHost__VercelAddresses` (the A-record addresses shown to hosts: confirm them in the Vercel dashboard), `PublicHost__RateLimitPermitLimit`, `PublicHost__DnsLookupTimeoutSeconds`, `PublicHost__ResolveCacheSeconds`, `PublicHost__TxtRecordPrefix`, and the periodic domain re-check `PublicHost__RecheckPendingMinutes` (30), `PublicHost__RecheckVerifiedHours` (24), `PublicHost__MaxPendingDays` (14), `PublicHost__FailuresBeforeDemotion` (2), `PublicHost__RecheckBatchSize` (50) | SE-03, BK-17 · [`seo-domain.md`](seo-domain.md) |
| `Vercel__ApiToken`, `Vercel__ProjectId` | D | per environment | `<token scoped to the web app project>` / `prj_<id>` | Secret token and project id of the Vercel project that serves the web app: with them the platform adds a host's verified custom domain to the project (certificate included). Missing: `vercel: degraded`, a custom domain stays `Pending` ("activation not available"). An optional Pro feature | BK-17 · [`seo-domain.md`](seo-domain.md) § 10 |
| `Vercel__TeamId`, `Vercel__ApiBaseUrl`, `Vercel__TimeoutSeconds` | O | per environment | `team_<id>` | Team id when the project belongs to a team; API base URL (default `https://api.vercel.com`) and timeout (10 s) | BK-17 |
| `ForwardedHeaders__KnownNetworks`, `__KnownProxies`, `__ForwardLimit` | O | same | `ForwardLimit=1` | Client IP behind the Railway edge (default: trust the peer, last `X-Forwarded-For` entry). Invalid CIDR/IP or limit < 1: **startup** | FD-10 · [`proxy-ip.md`](proxy-ip.md) |
| `RateLimiting__{Policy}__PermitLimit`, `__WindowSeconds` | O | same | `RateLimiting__PublicRead__PermitLimit=120` | Per-IP limits of the anonymous endpoints, safe defaults; < 1: **startup**. Policies listed in `proxy-ip.md`; also `RateLimiting__AiPerUser__*`, `RateLimiting__AiPerOrg__*`, `RateLimiting__GuestBookingLookupPerEmail__*` | FD-10, FD-21, BK-11 · [`proxy-ip.md`](proxy-ip.md), [`ai.md`](ai.md) |
| `SafeExternalHttp__AllowedPorts`, `__TimeoutSeconds`, `__MaxResponseBytes`, `__MaxRedirects` | O | same | defaults 443 / 15 s / 5 MiB / 3 | Anti-SSRF client for user-supplied URLs (iCal feeds) | FD-16 · [`external-fetch.md`](external-fetch.md) |
| `ICalImport__RecurrenceMonthsAhead`, `__RecurrenceMonthsBack`, `__MaxFeedsPerProperty` | O | same | defaults 18 / 1 / 10 | Recurrence window and feed limit of the iCal import (clamped) | PC-10 · [`ical.md`](ical.md) |

### 2.5 Email

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Email__Provider` | S | same | `Resend` | Only `Resend` exists; another value: **startup** | FD-13 · [`email.md`](email.md) |
| `Email__ApiKey` | S | per environment | `re_<key>` | Secret, must start with `re_`: **startup**. Legacy name `Email__ResendApiKey` still read | FD-13 |
| `Email__FromAddress` | S | per environment | `noreply@<verified domain>` | A plain address of the domain verified on Resend, not `@resend.dev`: **startup** | FD-13 |
| `Email__FromName` | O | same | `CasaZen` | Display name (default `CasaZen`) | FD-13 |

### 2.6 Object storage (Supabase Storage, S3 API)

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Storage__Provider` | S | `S3` | `S3` | Empty = `S3` outside Development/Testing; `FileSystem` is refused there (**startup**) | FD-07, D8 · [`storage.md`](storage.md) |
| `Storage__PublicBaseUrl` | S | per environment | `https://<ref>.supabase.co/storage/v1/object/public/<public-bucket>` | Absolute https URL of the public bucket: **startup** | FD-07 |
| `Storage__S3__ServiceUrl` | S | per project | `https://<ref>.storage.supabase.co/storage/v1/s3` | Absolute https S3 endpoint: **startup** | FD-07 |
| `Storage__S3__Region` | S | per project | `<region shown in Supabase>` | **Startup**. Also read as the region of the Supabase subprocessor entry | FD-07, PL14-SUBP |
| `Storage__S3__AccessKeyId`, `Storage__S3__SecretAccessKey` | S | per environment | `<S3 access key>` | Secrets: **startup** | FD-07 |
| `Storage__S3__PublicBucket`, `Storage__S3__PrivateBucket` | S | per environment | `casazen-<env>-public` / `-private` | Two different buckets: **startup** | FD-07 |
| `Storage__S3__ForcePathStyle` | O | same | `true` | Default `true` (required by Supabase) | FD-07 |
| `Storage__SignedUrlTtlMinutes` | O | same | `5` | 1-60, default 5; outside the range: **startup** | FD-07 |
| `Storage__FileSystem__RootPath` | O | Development only | — | Only with the FileSystem provider | FD-07 |
| `ImageStorage__LocalPath`, `ImageStorage__BaseUrl`, `GuestDocumentStorage__LocalPath`, `GuestDocumentStorage__BaseUrl` | O | one-off | — | Read **only** by the `storage:migrate-legacy` command that moves old local files to the buckets | FD-07 · [`storage.md`](storage.md) § 5 |

### 2.7 Data Protection (key ring encryption)

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `DataProtection__CertificatePfxBase64` | S | per environment | `<base64 of the .pfx>` | **Startup** (CO-14); not valid base64 or no private key: **startup** | CO-14, FD-07 · [`storage.md`](storage.md) § 4 |
| `DataProtection__CertificatePassword` | S | per environment | `<pfx password>` | Wrong password: **startup** | CO-14 |
| `DataProtection__PreviousCertificatePfxBase64`, `DataProtection__PreviousCertificatePassword` | O | rotation only | — | Still decrypts keys written before a certificate rotation | CO-14 · [`encryption.md`](encryption.md) |

### 2.8 Stripe and billing

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Stripe__SecretKey` | D | `sk_test_…` / `sk_live_…` (or `rk_…`) | `sk_test_<key>` | Missing: `stripe: degraded`, payments off. **Wrong mode stops the startup** (live key outside Production, test key in Production) | PL-11 · [`stripe.md`](stripe.md) |
| `Stripe__PublishableKey` | D | `pk_test_…` / `pk_live_…` | `pk_test_<key>` | Same mode as the secret key. Missing: the checkout cannot load Stripe | PL-11 |
| `Stripe__WebhookSecret` | D | per endpoint and mode | `whsec_<secret>` | Platform endpoint `/webhooks/stripe` (subscriptions, invoices). Missing or placeholder: 500 `stripe_webhook_not_configured`, `stripe: degraded` | PL-10 · [`INFRA.md`](../INFRA.md) § Stripe |
| `Stripe__ConnectWebhookSecret` | D | per endpoint and mode | `whsec_<secret>` | Connect endpoint `/webhooks/stripe/connect`. Missing: **no direct booking is ever confirmed** | A3-24 |
| `Stripe__ConnectDefaultCountry` | O | same | `IT` | Country of new connected accounts (default `IT`) | [`stripe.md`](stripe.md) |
| `Billing__Prices__Starter`, `__Pro`, `__Scale` | D | per mode | `price_<id>` | Stripe Price ids, none in code. Production with `Stripe__SecretKey`: any missing/placeholder/duplicate id is **startup**; elsewhere that plan is not purchasable (422 `billing_plan_unavailable`, `stripe: degraded`) | PL-11 · [`stripe.md`](stripe.md) |
| `Billing__Display__{Tier}__Name`, `__PriceMonthly`, `__Features` | O | same | defaults in `appsettings.json` (29 / 79 / 199 EUR) | What the plans page shows; must match the Stripe prices | PL-11 |
| `SupplierPayments__CommissionPercent` | PO | same (**provisional**) | `10` | The commission CasaZen keeps on the services paid inside CasaZen, as a percentage of the price the payer pays (0 to 50, at most two decimals). **Provisional**: 10 is the product owner's hypothesis (D3); it is configuration only, never in code, and the code has no default for it. Missing or out of range: **startup**. A payment keeps the percentage it was created with (a later change never rewrites it); a supplier can have its own (`SupplierProfiles.CommissionPercentOverride`, set by an admin with `PUT api/admin/suppliers/{orgId}/commission`, optionally until a date; SP-15b). The VAT on the commission and DAC7 are open (`[CONSULENTE FISCALE]`, D4): no rate lives here | SP-15a · [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)", [`suppliers.md`](suppliers.md) § 26 |
| `SupplierPayments__LateAfterDays`, `__PaymentLinkValidityDays`, `__MinAmountCents`, `__ReminderDays__{0,1}` | O | same | `7` / `30` / `50` / `2`, `7` | Days after the request on which a payment is flagged late (the daily job of SP-15b; 1 to 365), days a payment link stays valid from its email (1 to 365: past it the link answers the same 404 as a wrong one), the smallest amount charged inside CasaZen in cents (50 is Stripe's minimum for euro; a completed job below it is paid by hand), and the days after the request on which the reminders go (the daily job of SP-15b; 1 to 365 each; the default is two reminders, and a payment gets three emails with a link in all). Out of range: **startup** | SP-15a · [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)" |
| `SupplierPayments__CommissionVatPercent` | PO | same | *(empty)* | The VAT rate of CasaZen's commission, 0 to 100 with at most two decimals, **empty until the tax consultant decides** (D4, `[CONSULENTE FISCALE]`: whether the commission percentage includes VAT or VAT is added to it). Configuration only: it is not in `appsettings.json` and the code has no rate. It is only repeated in the `vat_percent` column of the monthly commission export (`GET api/admin/supplier-payments/export`); no VAT amount is computed and nothing is sent to the tax authority. Out of range: **startup** | SP-15b · [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)" |
| `Billing__PastDueGraceDays` | O | same | `7` | Days a past-due subscription keeps its tier | A1-11 |
| `Entitlement__OpenAccess__Enabled`, `__Tier` | O | **leave unset** (off) until the product owner decides | `false` / `Scale` | "Accesso aperto" (D-A, D33): `true` serves every org as the `__Tier` plan **at least** (`Starter`, `Pro` or `Scale`, default `Scale`; never lower than the plan the org pays for), whatever its subscription. Only the answer changes: prices, stored plans, Stripe and the plan change rules do not. A value that is not `true`/`false`, or a tier that is not a plan name, is **startup**, also while the switch is off. Log line `Open access is ON` while on | BL-01 · [`open-access.md`](open-access.md) |
| `Entitlement__Tiers__{Tier}__MaxProperties` | O | same | `3` | Override of the property limit of a plan (positive integer; otherwise the catalogue default) | — |
| `Entitlement__Tiers__{Tier}__MaxSeats` | O | same | `5` | Override of the people a plan allows (positive integer; otherwise Starter 2, Pro 10, Scale unlimited) | AM-02 · [`org-team.md`](org-team.md) § 13 |
| `OrgTeam__InvitationRetentionDays` | O | same | `30` | Days after which a closed invitation is deleted with the name and email of the invitee (minimum 1) | AM-02 · [`org-team.md`](org-team.md) § 15 |
| `Billing__VatNumber` | PO | per environment | — | CasaZen's VAT number. Billing entry gate of Production (live key), together with the e-invoicing decision below: without it the plan checkout answers 409 `billing_gate_closed`; the `einvoicing` ready check reports `degraded` and names the missing variable (never the value). Also the fallback of the legal documents' `Legal__Controller__VatId` | PL-13 · [`billing-tax.md`](billing-tax.md) |
| `Sdi__ManualIssuanceAccepted` | PO | per environment | `false` | `true` = the product owner accepts to issue the platform e-invoices by hand from the admin queue (no SDI provider in this build: `UnconfiguredSdiEInvoiceProvider`). With `Billing__VatNumber` it opens the live plan checkout; missing or `false`: the checkout stays closed (409 `billing_gate_closed`), `einvoicing: degraded`, paid invoices are not sent to SDI. Stripe Tax needs no variable (Dashboard settings). The old PL-11 gate keys are listed as inert at the end of this file: delete them if still set | PL-13 · [`billing-tax.md`](billing-tax.md) |
| `DirectBooking__PendingTtlMinutes`, `__ConsentVersion`, `__RateLimitPermitLimit` | O | same | `30` / `2026-06-direct-checkout-v1` / `10` | Checkout hold length, consent version the public checkout must send, legacy rate-limit key | BK-21 · [`direct-booking.md`](direct-booking.md) |
| `DirectBooking__OnSiteApprovalHours`, `__OnSiteEmailVerificationMinutes`, `__OnSiteMaxNights` | PO | same | `24` / hold TTL / `30` | **Provisional defaults** of "Paga in struttura" (BK-06), to be confirmed | BK-06 · [`direct-booking.md`](direct-booking.md) |
| `DirectBooking__DeferredChargeMaxAttempts`, `__DeferredChargeCancelAfterDays` | PO | same | `3` / `3` | **Provisional defaults** of the deferred charge (BK-08), to be confirmed | BK-08 |

### 2.9 Legal documents and consents

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Legal__ContentPath` | O | same | `LegalDocuments` | Folder of the text files, relative to the API binaries | PL-14 · [`legal-documents.md`](legal-documents.md) |
| `Legal__Documents__{Tos,Privacy,Dpa}__{Version,EffectiveAt,DocumentUrl}` | PO | same | `2026-06-v1` / `2026-11-15` / `https://<official copy>` | Version the consents refer to (default `1.0` if unset); date in force; optional external https copy. No text and no URL: pages say "in preparazione", `legal: degraded` | PL-14 |
| `Legal__Documents__Subprocessors__Version`, `__EffectiveAt` | PO | same | `2026-10-v1` | Version of the list (gets `+ai-<provider>` with an active AI provider) and its date in force | PL-14, PL14-SUBP |
| `Legal__Documents__Subprocessors__Providers__{Supabase,Auth0,Stripe,Resend,Expo,Railway,Vercel}__{Entity,Region,TransferMechanism,Source}` | PO | same | committed defaults with their sources | Public facts committed for each provider (PL14-SUBP); the region of Supabase / Auth0 / Railway is read from the configuration, not typed. A missing field = "in definizione", `legal: degraded` |
| `Legal__Documents__Subprocessors__Providers__{Railway,Vercel}__Enabled` | O | same | `true` | Forces a hosting provider into or out of the list when it cannot be detected (a custom domain hides Vercel) | PL14-SUBP · [`legal-documents.md`](legal-documents.md) § 3 |
| `Gdpr__PrivacyNoticeVersion`, `Gdpr__MarketingConsentVersion` | PO | same | `<version of the text>` | Check-in works without them but the notice shown is not recorded (warning at each check-in); marketing consent is not offered | CO-15 · [`gdpr.md`](gdpr.md) |
| `Gdpr__Retention__{DocumentScans,AlloggiatiData,Marketing,FiscalData,LeaseParties,SupplierCustomers}__{Years,Months,Days,Source}` | PO | same | — | **No default**: a category without period **and** `Source` is not applied (nothing is anonymized); the GDPR tab shows "Periodo non configurato" | CO-15, LT-12, SP-10 · [`gdpr.md`](gdpr.md) |

### 2.10 Compliance, check-in and fiscal parameters

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Cin__ExposureDeadline` | PO | same | `2026-03-01` | Date `yyyy-MM-dd` or empty (default empty = no deadline alert); other format: **startup** | CO-20 · [`cin-format.md`](cin-format.md) |
| `Cin__AlertDaysBefore` | O | same | `[30,7,1]` | Days before the deadline (1-366); out of range: **startup** | CO-20 |
| `Compliance__CinGuidanceUrl`, `__CheckoutReminderHourLocal`, `__RequiredDocuments__{default,LOM,LAZ}`, `__StatusCheck__NotifyOnFirstCheck` | O | same | committed defaults | CIN guidance link, reminder hour (Europe/Rome), required documents per region, first-check notification | CO-06, CO-20 · [`compliance.md`](compliance.md) |
| `CheckIn__SendWindowDays`, `__SessionLifetimeDays`, `__RateLimitPermitLimit`, `__SubmitRateLimitPermitLimit` | O | same | `3` / `7` / `10` / — | Guest check-in link window and lifetime (1-60 days) and legacy limit keys | CO-09 · [`alloggiati.md`](alloggiati.md) |
| `StayAlerts__GuestDataReminderHourLocal`, `__DeadlineWarningHours`, `__MaxOverdueReminders`, `__OverdueReminderHourLocal` | O | same | `10` / `12` / `2` / `9` | Alloggiati deadline reminders | CO-10 · [`hangfire.md`](hangfire.md) § 9 |
| `ShortStayFiscal__*`, `CedolareAdvisory__*` | O | same | committed with their official sources | Fiscal rules of short stays and the LTR advisory, each parameter with its source; an invalid or sourceless value: **startup** (`CedolareAdvisory`, `ShortStayFiscal`). Do not override without the accountant | CO-18, LT-08 · [`rli.md`](rli.md) |
| `Rli__TosVersion`, `Rli__AttestationText` | PO | same | committed draft | Version and text of the RLI delegation attestation (a draft, to confirm with the lawyer) | LT-01 · [`rli.md`](rli.md) |
| `LeaseTemplates__TemplatesDirectory`, `LeaseTemplates__Variants__{CedolareSecca,RegimeOrdinario,CanoneConcordato}__{VersionId,Approved,ApprovalReference,ApprovedAt}` | PO | same | `Approved=false` | Contract templates approved by the lawyer; leave unset (signing/PDF answers 422). `Approved=true` with a stub version, no reference/date or a missing file: **startup** | LT-03 · [`lease-contract-templates.md`](lease-contract-templates.md) |
| `Suppliers__PilotComuni__{n}__Code`, `__Name` | PO | same | `<ISTAT or cadastral code>` / `<comune>` | Self-serve supplier registration only in the listed comuni; empty = invites only. Repeated or incomplete entry: **startup** | SU-01 · [`suppliers.md`](suppliers.md) |
| `Suppliers__ServiceRequests__HostResponseMinutes`, `__FinalAmountTolerancePercent`, `__RemindIntervalHours` | O | same | `120` / `20` / `6` | Minutes a host's request waits for the supplier's answer (10 to 4320; the deadline `ResponseDueAt` is set on every new request, only `Features__SupplierRequestAutoCancel` makes it cancel), percentage above the quote that flags the final amount for the customer's confirmation (0 to 100), hours between two reminders of the host (1 to 72). A value out of range: **startup** | SP-04 · [`suppliers.md`](suppliers.md) § 21 |

### 2.11 Features, AI and integrations (off by default)

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Features__OtaPartnerApi`, `Features__AiSupplierDiscovery` | O | **leave unset** (off) | — | D10 / D11: partner OTA API and AI supplier discovery stay frozen. Turning `OtaPartnerApi` on needs the `OTA__*` keys below | FD-20 · [`feature-flags.md`](feature-flags.md) |
| `Features__UiRedesign` | O | **leave unset** (off) | — | 01-D8: gradual rollout of the new interface. No backend code reads it; it is only listed in `GET /api/public/features` (`uiRedesign`) for the frontend. Turn it on when the frontend work is deployed and reviewed, `test` first | BL-01 · [`feature-flags.md`](feature-flags.md) |
| `Features__OrgTeam` | O | **leave unset** (off) | — | AM-01: org team. Off: `GET /api/me/contexts` does not list the `account` context (the web app of today does not know it). Turn it on only together with the account screens (AM-04). AM-02: with it off every invitation and member endpoint answers 404 (the migration `AddOrgInvitations` is additive and the maintenance job runs anyway) | AM-01, AM-02 · [`org-team.md`](org-team.md) §§ 7, 18 |
| `Features__RliProvider`, `Features__ESignProvider` | O | **leave unset** (off) | — | D15: no provider client exists; `ESignProvider=true` requires `ESign__WebhookSecret` (≥ 16 chars): otherwise **startup** | LT-01, LT-02 · [`rli.md`](rli.md) |
| `Features__SupplierRequestAutoCancel` | O | **leave unset** (off) | — | D8: CasaZen cancels the new requests nobody answered (job `service-request-auto-cancel`, every 10 minutes). Turn it on only when the console and the mobile app show the status `Annullato`; the first run cancels every open request that is already overdue | SP-04 · [`feature-flags.md`](feature-flags.md), [`suppliers.md`](suppliers.md) § 21.8 |
| `Features__SupplierShowcaseBooking` | O | **leave unset** (off) | — | D34: the supplier's public showcase. It gates the public services, free slots and price estimate (SP-09, 404 when off; the page reads as before), the booking from the showcase (SP-10: needs the two `Suppliers__…` variables below) and the customer's own area of a booking (SP-11: find, cancel, move, answer a proposed time; **turn the flag on only where SP-10 and SP-11 are both deployed**). Leave it unset until the screens and the texts are reviewed. The supplier's service catalog does not depend on it | SP-02, SP-09, SP-10, SP-11 · [`feature-flags.md`](feature-flags.md), [`suppliers.md`](suppliers.md) § 19, § 22, § 23, § 24 |
| `Features__SupplierOnlinePayments` | O | test: `true` only to try the supplier's Stripe onboarding and the payments · prod: **leave unset** (off) | — | D2: payment of the supplier's work inside CasaZen. On: the supplier's Stripe account routes (`api/supplier/payments/*`, SP-14) and, since SP-15a, the **creation** of payments: a request taken by a supplier whose Stripe account takes charges and payouts is paid with a direct charge on that account and the CasaZen commission (`SupplierPayments__CommissionPercent`), and `POST api/supplier/requests/{id}/payment-request` exists (404 while off, before authentication). The payer's page, the payment sessions and the webhook of money already in flight are **not** behind it. Since SP-15b the same flag also lets the daily job send the pending requests and the reminders (the webhook, the sync job, the refunds and the admin tools are not behind it). **The Connect webhook endpoint must listen to `charge.dispute.created`** and the other events of [`INFRA.md`](../INFRA.md) § Stripe, or a payment made online is recorded only when the sync job reads it (every 15 minutes). **Not for production** before the supplier legal texts (D-C) and the tax treatment of the commission (D4) are approved, and before the test-mode check of [`stripe.md`](stripe.md) § "Services of the suppliers (SP-15)" → Verification | SP-02, SP-14, SP-15a, SP-15b · [`feature-flags.md`](feature-flags.md), [`suppliers.md`](suppliers.md) §§ 25-26, [`stripe.md`](stripe.md) |
| `Suppliers__Showcase__PrivacyNoticeVersion` | PO | same | — | Version of the privacy notice a customer accepts when it books from a supplier's showcase. **Required when `Features__SupplierShowcaseBooking` is on** (startup fails otherwise); a booking with another version is 422. The text is a decision of the product owner and legal (D14) | SP-10 · [`suppliers.md`](suppliers.md) § 23.13 |
| `Suppliers__CustomerIndexKey` | S | same | — | Secret (32+ random characters) of the HMAC index of the customers' e-mail addresses of the suppliers. **Required outside Development/Testing when `Features__SupplierShowcaseBooking` is on** (startup fails otherwise). Never change it once customers exist: they would no longer be found | SP-10 · [`suppliers.md`](suppliers.md) § 23.7 |
| `Suppliers__Showcase__EmailVerificationMinutes`, `__ProposalResponseMinutes` | O | same | `30` / `1440` | Minutes a booking from the showcase holds its slot and waits for the e-mail check (5 to 1440); minutes a customer has to answer a proposed time (30 to 10080). Out of range: **startup** | SP-10 · [`suppliers.md`](suppliers.md) § 23.13 |
| `RateLimiting__PublicSupplierBookingCreate__*`, `RateLimiting__SupplierBookingCreatePerEmail__*` | O | same | 5 / 10 min per IP; 3 / h per address and supplier | Limits of the booking from the showcase | SP-10 · [`proxy-ip.md`](proxy-ip.md) § 4 |
| `RateLimiting__SupplierBookingManagePerEmail__*` | O | same | 10 / 15 min per address and supplier | Limit of the customer's own area of a booking (lookup, cancel, move, answer a proposal), next to the per-IP policy `PublicGuestBookingLookup` (10 / 5 min); in memory per replica | SP-11 · [`proxy-ip.md`](proxy-ip.md) § 4, [`suppliers.md`](suppliers.md) § 24.4 |
| `Suppliers__Showcase__FreeCancellationHours` | O | same | `24` | Elapsed hours before the work until which a customer's cancellation is "free" (a later one is allowed too, decision D6, and costs nothing); 0 to 720, out of range: **startup** | SP-11 · [`suppliers.md`](suppliers.md) § 24.10 |
| `ESign__BaseUrl`, `ESign__ApiKey`, `ESign__WebhookSecret` | O | provider sandbox / production | — | Read only with `Features__ESignProvider` on | LT-02 |
| `OTA__WebhookSecret`, `OTA__Resilience__{Airbnb,BookingCom,Expedia,Vrbo,TripAdvisor,Agoda}__{RetryCount,CircuitBreakerFailures,CircuitBreakerDurationSeconds,TimeoutSeconds,MaxRequestsPerWindow,WindowDurationSeconds,MaxConcurrentRequests}` | O | only with `Features__OtaPartnerApi` | — | HMAC secret of `POST /webhooks/ota/{platform}` (missing: 500) and the Polly / rate-limit settings of each platform adapter; read only with the flag on | FD-20 · [`feature-flags.md`](feature-flags.md) |
| `Ai__Provider` | O | `Stub` | `Stub` | `Stub` = no external call. `DeepSeek` + `Ai__ApiKey` makes DeepSeek an active subprocessor (read [`ai.md`](ai.md) first) | FD-21 · [`ai.md`](ai.md) |
| `Ai__ApiKey`, `Ai__Model`, `Ai__OpenAiBaseUrl`, `Ai__AnthropicBaseUrl`, `Ai__MaxCompletionTokens`, `Ai__WebSearchMaxTokens` | O | only with an AI provider | secret | Provider endpoint, model and token caps | FD-21 |
| `Ai__Subprocessor__{Name,Purpose,Entity,Region,TransferMechanism,Website,Source}` | PO | only with an AI provider | committed DeepSeek facts | How the active provider appears in the subprocessor list; the transfer basis is left to the product owner | FD-21, PL14-SUBP · [`ai.md`](ai.md) |
| `Expo__AccessToken` | O | per environment | `<token>` | Secret, only if "Enhanced Security for Push Notifications" is on in the Expo project; without it a push answered 401 is marked failed | MO-04 · [`mobile-release.md`](mobile-release.md) § 9.7 |

### 2.12 Other

| Variable | Req. | Test / Prod | Example (placeholder) | Effect if missing or wrong | Task · runbook |
|---|---|---|---|---|---|
| `Logging__LogLevel__Default`, `__Microsoft`, `__Hangfire` | O | same | `Information` / `Warning` / `Information` | Log levels | — |
| `CASAZEN_MIGRATION_TARGET` | O | local tooling | `test` / `prod` | Used only by `dotnet ef` and `scripts/migrate.*` to choose the Supabase connection string; not a runtime variable | FD-02 · [`INFRA.md`](../INFRA.md) |

**Supabase pooler port.** The connection string must use the **session** pooler (port `5432`, host `*.pooler.supabase.com`, user `postgres.<project-ref>`) or the direct host, never the transaction pooler (port `6543`): it silently breaks PostgreSQL advisory locks (overbooking and plan-limit protection) and Hangfire's locks. Outside Development and Testing the **startup is refused** with port 6543, and `db-connections` is `degraded` when the pools can exceed the connection budget.

## 3. Local development (backend)

Copy `Casazen.Web/appsettings.Development.example.json` to `appsettings.Development.json` (gitignored) and keep real values
there or in user secrets; `ASPNETCORE_ENVIRONMENT=Development` makes the startup checks soft (file storage, no certificate,
no mandatory email or CORS origin). Integration tests use `TEST_POSTGRES_CONNECTION` (set by `env.sh`) and the `Testing`
environment with `Casazen.Web/appsettings.Testing.json`. `secrets/supabase.local.env.example` feeds `scripts/migrate.*` and
`setup-supabase.*`.

## 4. Frontend (Vercel and local)

Read by `vite.config.ts`, `src/config/*` and `api/sitemap.ts`. A Vercel **Preview** or **Production** build fails without the
three required variables (`src/config/vercel-build-env.ts`); local and CI builds keep the defaults.

| Variable | Req. | Vercel Preview | Vercel Production | Example (placeholder) | Effect if missing | Task · runbook |
|---|---|---|---|---|---|---|
| `VITE_API_BASE_URL` | S (build) | Railway **test** URL + `/api` | Railway **production** URL + `/api` | `https://<railway host>/api` | **Build fails** on Vercel (it used to fall back to the production API in any production bundle); also read by the `/sitemap.xml` function at runtime | PL-11, SE-02 · [`INFRA.md`](../INFRA.md) § Vercel |
| `VITE_AUTH0_DOMAIN` | S (build) | test tenant | production tenant (never the test one) | `<tenant>.eu.auth0.com` | **Build fails** (bare host name, no `https://`) | PL-11 · [`auth0.md`](auth0.md) § 1.1 |
| `VITE_AUTH0_CLIENT_ID` | S (build) | SPA client id of the test tenant | SPA client id of the production tenant | `<spa client id>` | **Build fails** | PL-11 |
| `VITE_AUTH0_AUDIENCE` | D | `https://casazen-api` | same | `https://casazen-api` | Default `https://casazen-api`; must equal `Auth0__Audience`: a mismatch = every API call 401 | PL-11 |
| `VITE_PUBLIC_SITE_URL` | S (Production build) | not needed | `https://<public domain>` (same as the backend `App__PublicSiteBaseUrl`) | `https://<public domain>` | **Production build fails**; other builds write `robots.txt` with `Disallow: /` | SE-02 · [`seo-domain.md`](seo-domain.md) |
| `VITE_SUPPORT_EMAIL` | O | optional | optional | `<support address>` | The "account disabled" page shows a generic text (no address is invented) | PL-03 |
| `VITE_DEMO_MODE` | O | `false` | `false` | `false` | `true` in a normal build **fails the build** (demo opens without login); only `npm run build:demo` | A9-38 · [`demo-mode.md`](demo-mode.md) |
| `VITE_DEMO_PROFILE` | local | — | — | `long-term` | Demo profile of `npm run dev:demo` | A9-38 |
| `VITE_HTTPS` | local | — | — | `1` | Dev server over HTTPS (read from the shell, not from `.env`) | — |
| `VERCEL_ENV`, `VERCEL_*` | set by Vercel | — | — | — | Decides `robots.txt` and the build checks; keep "Automatically expose System Environment Variables" on | SE-02 |
| `E2E_*` (`E2E_AUTH0_EMAIL`, `E2E_AUTH0_PASSWORD`, `E2E_AUTH0_USER_ID`, `E2E_BASE_URL`, `E2E_LOCAL`, `E2E_STAGING`, `E2E_STAGING_API_URL`, `E2E_LOCAL_API_URL`, `E2E_PROD_SMOKE`, `E2E_PROD_FE_URL`, `E2E_PROD_API_URL`, `E2E_DEPLOY_SMOKE`, `E2E_DEPLOY_FE_URL`) | O | CI / local | — | — | Playwright only; never in a deployed bundle (`e2e/.env.example` → `.env.e2e`, gitignored) | FD-01 · [`ci-frontend.md`](ci-frontend.md) |

## 5. Mobile (EAS and local)

Read by `app.config.ts` (EAS build worker) and `src/config/env.ts` (bundle). Every `EXPO_PUBLIC_*` value is compiled into the
JavaScript and readable by anyone with the app: use "Plain text" visibility and never a secret. A development build
(`APP_VARIANT=development`) may omit them and uses local defaults; `preview` and `production` may not.

| Variable | Req. | EAS `preview` | EAS `production` | Example (placeholder) | Effect if missing | Task · runbook |
|---|---|---|---|---|---|---|
| `EXPO_PUBLIC_API_URL` | S (build) | Railway test URL (no `/api`) | Railway production URL | `https://<railway host>` | **Build fails**; https only, no local address | MO-01 · [`mobile-release.md`](mobile-release.md) § 2 |
| `EXPO_PUBLIC_WEB_URL` | S (build) | staging web app | production web app | `https://<web app host>` | **Build fails** | MO-01 |
| `EXPO_PUBLIC_AUTH0_DOMAIN` | S (build) | test tenant | production tenant | `<tenant>.eu.auth0.com` | **Build fails** (bare host name) | MO-01 · [`auth0.md`](auth0.md) § 7 |
| `EXPO_PUBLIC_AUTH0_CLIENT_ID` | S (build) | client id of the **Native** app of the test tenant | of the production tenant | `<native client id>` | **Build fails**; required in development too (no default); never the SPA client id | MO-01 |
| `EXPO_PUBLIC_AUTH0_AUDIENCE` | S (build) | `https://casazen-api` | same | `https://casazen-api` | **Build fails**; must equal `Auth0__Audience` | MO-01 |
| `EXPO_PUBLIC_E2E_DEMO` | local | **do not set** | **do not set** | `0` | Maestro demo button, ignored outside `__DEV__` | MO-01 |
| `EXPO_PUBLIC_E2E_BUILD`, `EXPO_PUBLIC_E2E_PUSH_TOKEN` (and `APP_VARIANT=e2e`) | local E2E build only | **never** (the EAS build fails) | **never** (the EAS build fails) | `1` / `ExponentPushToken[...]` | Maestro E2E APK of the CI job: http and local addresses accepted, fixed push token on the emulator. `app.config.ts` refuses them on any EAS build | FN-04 · [`mobile-e2e.md`](mobile-e2e.md) |
| `EAS_PROJECT_ID` | S (build) | project-wide | project-wide | `<uuid from eas init>` | Release build fails; local builds without it skip push registration (`PUSH_PROJECT_ID_MISSING`). Not a secret; a malformed or all-zero value is refused | MO-03 · [`mobile-release.md`](mobile-release.md) § 1 |
| `GOOGLE_SERVICES_JSON` | S (Android production) | file variable (warns only) | file variable | `<path to google-services.json>` | Production Android build fails; preview warns and the app reports `PUSH_FCM_NOT_CONFIGURED`. The file is gitignored | MO-03 · § 9.2 |
| `APP_VARIANT` | set by `eas.json` | `preview` | `production` | — | Chooses the checks above; do not set by hand | MO-01 |

Credentials that are not variables: the FCM v1 service account key (Android) and the APNs key (iOS) are uploaded to the EAS
credentials, never to git ([`mobile-release.md`](mobile-release.md) § 5, § 9).

## 6. GitHub (variables and secrets)

| Repository | Name | Kind | Req. | Effect if missing | Task · runbook |
|---|---|---|---|---|---|
| backend | `RAILWAY_TEST_URL` | Variable | S (CI) | `verify-test` fails at once and the PR comment has no test link | FD-12 · [`health-checks.md`](health-checks.md) |
| backend | `RAILWAY_PROD_URL` | Variable | S (CI) | `verify-prod` fails at once | FD-12 |
| backend | `TEST_PROPERTY_ID` | Variable | O | The pricing-adapter smoke uses a fixed all-zero id (a 401/404 is accepted) | [`ci-backend.md`](ci-backend.md) |
| backend | `SUPABASE_PROJECT_URL`, `SUPABASE_DB_HOST` | Variable | O | The keep-alive workflow skips the matching ping (visible warning) | [`ci-backend.md`](ci-backend.md) |
| backend | `SUPABASE_ANON_KEY` | Secret | O | REST ping skipped with a warning | [`ci-backend.md`](ci-backend.md) |
| frontend | `E2E_AUTH0_EMAIL`, `E2E_AUTH0_PASSWORD` | Secrets | O | Staging E2E skipped (`::warning::`) | [`ci-frontend.md`](ci-frontend.md) |
| frontend | `E2E_STAGING_API_URL`, `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE` | Variables | O | Staging E2E runs against the default test API / skips | [`ci-frontend.md`](ci-frontend.md) |
| all | `RAILWAY_TOKEN`, `RAILWAY_SERVICE_*`, `VERCEL_TOKEN` | — | **not needed** | Deploys use the native GitHub integrations of Railway and Vercel | [`INFRA.md`](../INFRA.md) |

## 7. Migrations that drop or rewrite data (applied at the startup of the first deploy that contains them)

The startup runs `Database.Migrate()`. These migrations of the risanamento drop a table or a column, or rewrite existing
rows; take the check on **production** before the release PR is merged.

| Migration | What it drops | Check before |
|---|---|---|
| `RemoveSupplierJobs` | table `SupplierJobs` (D12) | `SELECT count(*) FROM "SupplierJobs"`, export procedure in [`suppliers.md`](suppliers.md) § 10.3 |
| `UnifyTouristTaxOnTouristTaxRates` | table `TaxRates` (never written, every booking recorded tourist tax 0; rates now only in `TouristTaxRates`) | [`tourist-tax-rates.md`](tourist-tax-rates.md): confirm `TouristTaxRates` is filled |
| `RemoveLegacyBookingCheckInToken` | `Bookings.CheckInToken`, `CheckInTokenExpiresAt` (the removed legacy check-in portal, CO-16) | links of the old portal already answer 404: [`storage.md`](storage.md) § CO-16 |
| `GuestPrivacyConsentsAndRetention`, `LeasePartyRetention` | `Guests.DataRetentionUntil`, `LeaseContracts.DataRetentionUntil` (the invented 7-year retention) | retention is now configuration: [`gdpr.md`](gdpr.md) § 2-3 |
| `SeparateSupplierOrgFromHostOrgId` (PL-05) | drops nothing: rewrites the org links of supplier accounts and may split a supplier org that became a host, in one transaction | run the read-only queries of [`suppliers.md`](suppliers.md) § 13.2 on the environment before the deploy |
| `AddStayAlertStates`, `AddDl145SafetyChecklist`, `AddICalMultiFeed`, `SeasonalPriceSuggestions` | `Bookings.CheckoutReminderJobId` ([`hangfire.md`](hangfire.md) § 9), `Properties.SafetyChecklistJson` ([`compliance.md`](compliance.md)), `PropertyICalFeeds.ExportToken` ([`ical.md`](ical.md): the export link moved to `PropertyICalExports`), `PricingAdapterConfigs.NextScheduledRunAt` ([`seasonal-suggestions.md`](seasonal-suggestions.md)) | Look for rows with a value in these columns on production before the release; no export procedure is documented for them |

## 8. Keys that look like settings but nothing reads (inert)

Do not set these expecting an effect. DEPLOY-CFG removed from `appsettings.json` the ones below that were only placeholders
(`Auth0:ClientId`, the `Hangfire` worker/schedule/path keys, the `OTA` platform credentials); the others are still committed.

<!-- deploy-checklist:not-checked -->

| Key | Why inert |
|---|---|
| `Auth0__ClientId`, `Auth0__ClientSecret` | `Auth0Options` has no such property; the API validates tokens with `Domain` + `Audience` only (the SPA/Native client ids live in Vercel and EAS) |
| `Hangfire__Schedules__OtaSync`, `__BookingPull`, `Hangfire__ServerName`, `Hangfire__DashboardPath` | Cron expressions are constants in `RecurringJobsRegistration`; the server name is `HangfireServerIdentity.ServerName`; the dashboard path is `/hangfire` |
| `Billing__OssThresholdAmount` | PL-13 removed the OSS tracker and its threshold; the VAT is computed by Stripe Tax |
| `Sdi__Provider`, `Sdi__ProviderConfigured`, `Vies__StubMode` | Not read any more: PL-13 replaced them with `Sdi__ManualIssuanceAccepted` (a provider is registered in code, not by configuration) |
| `OTA__Airbnb__*`, `OTA__BookingCom__*`, `OTA__Expedia__*` (`ApiKey`, `Endpoint`, `BaseUrl`) | Adapters do not read credentials from configuration; only `OTA__WebhookSecret` and `OTA__Resilience__*` are read |

<!-- /deploy-checklist:not-checked -->

## 9. Corrections to the other documents found while verifying against the code

<!-- deploy-checklist:not-checked -->

| Where | What it said | What the code does |
|---|---|---|
| Open-questions register § 1 (`DOMANDE-APERTE.md`) | "Attivare **Wait for CI** su Railway" | It must stay **off** (`ci-cd.yml`, `INFRA.md`, `health-checks.md`): `verify-test` waits for the deployment, so with the option on both wait for each other |
| `auth0.md` § 5, § 1.1 | `Auth0__ClientId` "used by the supplier registration page" | No code reads it (§ 8); only `Domain`, `Audience` and the management settings are read |
| `secrets/railway.test.variables.example.json` | Complete test environment | Lacked the required `DataProtection__*`, `App__ApiBaseUrl`, `Legal__*` rows: it would not start (updated, with a production twin) |
| `appsettings.json` `App:ApiBaseUrl` | Committed with the production Railway host | Test environment published the production host in iCal links and nothing detected it: now empty, `api-url` health check |
| Frontend `VITE_API_BASE_URL` | Optional (production fallback in code) | A Vercel Preview build without it called the production API: now a build failure |

<!-- /deploy-checklist:not-checked -->

## 10. After every deploy

1. `curl https://<railway host>/api/health/ready` → `status` `healthy` or `degraded` (200). With an Admin token each check has a
   `description` naming the missing variables: the expected `degraded` ones at this stage are only the items you decided to
   postpone (`stripe` until payments, `legal` until the texts exist). `commit` must equal the deployed SHA.
2. `GET /api/public/features` → every flag `false`.
3. Web app: login, the sitemap on the public domain (Production), `robots.txt` (`Disallow: /` on Preview).
4. Mobile: install the preview build, log in, load the calendar, check **Profilo → Notifiche**.
5. Stripe: send a test event to both webhook endpoints (200 expected).
