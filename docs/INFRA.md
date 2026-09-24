# CasaZen — Infrastructure Guide

> Zero-cost production stack: **Supabase** (PostgreSQL) + **Railway** (.NET API) + **Vercel** (React SPA)

---

## Overview

| Layer | Provider | Plan | Monthly cost | Notes |
|---|---|---|---|---|
| Database | Supabase | Free | $0 | 500 MB, 2 GB bandwidth; pauses after 7 days inactivity |
| Backend API | Railway | Hobby | ~$5 | $5 free credit/month; no cold starts |
| Frontend | Vercel | Hobby | $0 | Unlimited deployments, 100 GB bandwidth |

Railway is effectively ~$0–$2/month for a low-traffic .NET API once you stay within the $5 credit.  
Alternative for truly $0: **Render** free tier (same DX, but the service sleeps after 15 min of inactivity — acceptable for test, not ideal for prod).

---

## Environment Architecture

```
┌──────────────────────────────────────────────────────────┐
│  PRODUCTION                                              │
│  FE: casazen-app.vercel.app  (⚠️ see issue #187)         │
│  BE: casazen-api.up.railway.app                          │
│  DB: supabase.co (schema: casazen_prod)                  │
└──────────────────────────────────────────────────────────┘
         ▲ promoted by Stage 05 release-manager
┌──────────────────────────────────────────────────────────┐
│  TEST (shared staging)                                   │
│  FE: preview-[hash].vercel.app  (per PR, auto)           │
│  BE: casazen-api-test.up.railway.app                     │
│  DB: supabase.co (schema: casazen_test)                  │
└──────────────────────────────────────────────────────────┘
         ▲ Railway: push to `develop` (test) · Vercel: `develop` + PR previews (FE)
```

Two Supabase schemas (`casazen_test`, `casazen_prod`) in one free project — saves the free-tier limit.
Hangfire (background jobs) gets its own schema per environment too (`hangfire_casazen_test`, `hangfire_casazen_prod`),
so test and production never share queue, recurring jobs or servers: see [`docs/runbooks/hangfire.md`](runbooks/hangfire.md)
(variables, one-time switch from the old shared `hangfire` schema, check on `hangfire.server`, separate DB users).

---

## Git branch model

| Branch | Purpose | Deploy target |
|---|---|---|
| `develop` | Integration / shared test | Railway `test` + Vercel Preview (staging FE) |
| `main` | Production | Railway `production` + Vercel Production |
| `feature/*`, `fix/*`, `hotfix/*` | Short-lived work | PR → `develop` only |

- **Default branch** on GitHub: `develop` (feature PRs open against `develop`).
- **Release**: Stage 05 opens a release PR `develop` → `main`; only `release-manager` merges it.
- **Tags** `v*`: version label and GitHub Release only — not used as deploy triggers.

### One-time migration (if `develop` predates this model)

Align `develop` with current production code before switching Railway triggers:

```bash
# Backend and frontend (run in each repo)
git checkout develop && git pull
git merge origin/main   # resolve conflicts if any
git push origin develop
```

GitHub default branch is already `develop` on `casazen/backend` and `casazen/frontend`.

---

## Deploy model: native GitHub integrations

CasaZen uses **native deploys** from each provider’s GitHub app. GitHub Actions only **build, test, and verify** — they do **not** call `railway up` or push containers.

| Provider | Who deploys | Where runtime env vars live |
|---|---|---|
| **Railway** | Railway GitHub integration → `casazen/backend` | Railway dashboard → each environment (`test` / `production`) |
| **Vercel** | Vercel GitHub integration → `casazen/frontend` | Vercel dashboard → Preview / Production; `vercel.json` sets `outputDirectory: dist` |

**Production FE sanity check** (Stage 05 G10/G17): `curl -sf https://casazen-app.vercel.app` must return HTML containing `id="root"`. Do **not** use `https://casazen.vercel.app` (mislinked domain — issue #187). If the body shows `.env` placeholders or `GEMINI_API_KEY`, the Vercel project/domain is mislinked — fix the project root and output directory in the Vercel dashboard before promoting to `main`.
| **Supabase** | Hosted DB (no app deploy) | Supabase dashboard; optional secrets synced to GitHub by Supabase integration |

### What GitHub Actions still do

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci-cd.yml` | PR, push `develop` / `main` | NuGet vulnerability gate (High/Critical fail, transitive included), build, tests on PostgreSQL, format — `docs/runbooks/ci-backend.md` |
| `ci-cd.yml` → `verify-test` | Push `develop` | Poll `GET /api/health/ready` until the test API runs **this commit** and answers 200, then smoke (fails if `RAILWAY_TEST_URL` is missing) |
| `ci-cd.yml` → `verify-prod` | Push `main` | Same on production (`RAILWAY_PROD_URL`), then smoke |
| `deploy-preview.yml` | PR | Comment with BE/FE URLs (no deploy) |
| `supabase-keepalive.yml` | Weekly cron | Optional Supabase ping: fails when a configured ping fails, skips with a warning when not configured |

### What is **not** synced automatically

Integrations link repos and trigger deploys. They **do not** copy env vars across platforms:

- Supabase connection string → must be set on **Railway** (`ConnectionStrings__DefaultConnection`)
- Auth0 / Stripe / Email (Resend) → **Railway** only
- `VITE_*` → **Vercel** only
- Public API URLs → **GitHub Variables** (`RAILWAY_TEST_URL`, `RAILWAY_PROD_URL`) for CI health checks and PR comments only

You do **not** need `RAILWAY_TOKEN` or `RAILWAY_SERVICE_*` in GitHub unless you add custom scripts later.

---

## Manual setup checklist (one-time)

Do these once per project. Tick in order.

### 1. Supabase

- [ ] Create project `casazen` (region `eu-central-1`)
- [ ] Run SQL: create schemas `casazen_test`, `casazen_prod` (see below)
- [ ] Copy database URI for each schema
- [ ] Apply EF migrations to **test** schema (local `dotnet ef database update` with test connection string)
- [ ] (Optional) Enable **Supabase ↔ GitHub** integration — may add `SUPABASE_*` secrets to GitHub for keep-alive / CLI; **does not** configure Railway

### 2. Git branches (`casazen/backend` + `casazen/frontend`)

- [ ] Long-lived branch **`develop`** exists on both repos (integration / test)
- [ ] Long-lived branch **`main`** exists (production only)
- [ ] GitHub **default branch** = `develop` (new PRs target `develop`)
- [ ] Feature PRs: `feature/*` → `develop`
- [ ] Release PR (Stage 05): `develop` → `main` (squash merge by release-manager)

### 3. Railway (`casazen/backend`)

- [ ] New project → **Deploy from GitHub** → repo `casazen/backend`
- [ ] Environments: `test` + `production`
- [ ] **test**: trigger deploy on push to branch **`develop`**
- [ ] **production**: trigger deploy on push to branch **`main`** (disable autodeploy from `develop`)
- [ ] (Recommended) Enable **PR deployments** if you want a backend URL per PR; otherwise use shared test URL after merge
- [ ] Per environment, set **all** variables (see Railway section) — especially `ConnectionStrings__DefaultConnection` with correct `SearchPath` and `Hangfire__Schema` (different per environment, see [`runbooks/hangfire.md`](runbooks/hangfire.md))
- [ ] Enable **Public networking**; copy each environment’s HTTPS URL
- [ ] First deploy green in Railway dashboard

### 4. GitHub repo `casazen/backend` (variables only)

- [ ] **Variable** `RAILWAY_TEST_URL` = Railway test public URL (no trailing slash)
- [ ] **Variable** `RAILWAY_PROD_URL` = Railway production public URL
- [ ] (Optional) **Variables** `SUPABASE_PROJECT_URL`, `SUPABASE_DB_HOST` + **Secrets** `SUPABASE_ANON_KEY` for `supabase-keepalive.yml` — often already present if Supabase GitHub app is installed

You do **not** need: `RAILWAY_TOKEN`, `RAILWAY_SERVICE_TEST`, `RAILWAY_SERVICE_PROD`.

### 5. Vercel (`casazen/frontend`)

- [ ] Import repo; preset Vite; build `npm run build`; output `dist`
- [ ] **Production Branch** = `main` (Settings → Git)
- [ ] Enable deployments for branch **`develop`** (Preview env vars → Railway test API)
- [ ] Set `VITE_API_BASE_URL`, `VITE_AUTH0_*` for **Preview** and **Production** (see Vercel section)
- [ ] Confirm: push `develop` → staging FE; push `main` → production FE (see issue #187 for canonical URL confirmation)

### 6. Smoke test

- [ ] `GET {RAILWAY_TEST_URL}/api/health/ready` → 200, `commit` = the SHA of the last push to `develop` (see [`runbooks/health-checks.md`](runbooks/health-checks.md))
- [ ] `GET {RAILWAY_TEST_URL}/api/properties` without token → 401
- [ ] Open a PR → Vercel bot comment + backend link comment from `deploy-preview.yml`

---

## PostgreSQL migration (completed in codebase)

The backend uses **Npgsql** and PostgreSQL migrations. For a fresh database:

### 1 — Swap NuGet package

```xml
<!-- Casazen.Infrastructure/Casazen.Infrastructure.csproj -->
<!-- REMOVE: -->
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
<!-- ADD: -->
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
```

### 2 — Update DbContext registration

```csharp
// Casazen.Web/Extensions/ServiceCollectionExtensions.cs
// Replace:  options.UseSqlServer(connectionString)
// With:
options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"));
```

### 3 — Rebuild migrations

```bash
# Delete all existing SQL Server migrations
rm -rf Casazen.Infrastructure/Migrations/

# Regenerate for PostgreSQL
dotnet ef migrations add InitialCreate --project Casazen.Infrastructure
dotnet ef database update --project Casazen.Infrastructure \
  --connection "Host=localhost;Database=casazen_dev;Username=postgres;Password=dev"
```

### 4 — Update integration tests

Done (FD-04): integration tests run on real PostgreSQL. Set `TEST_POSTGRES_CONNECTION` (CI uses a `postgres:16` service in `ci-cd.yml`) or let Testcontainers start a container; each web factory creates, migrates and drops its own `it_<guid>` database. Details: `docs/TECHNICAL.md` § Testing.

### 5 — Update Dockerfile

```dockerfile
# Remove HTTPS self-signed cert requirement — Railway terminates TLS at the edge
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
```

---

## Supabase Setup

### Create project

1. https://supabase.com → New project
2. Name: `casazen` | Region: `eu-central-1` (Frankfurt)
3. Save the database password — used in every connection string
4. Wait ~2 min for provisioning

### Create schemas

Run in Supabase SQL Editor:

```sql
CREATE SCHEMA IF NOT EXISTS casazen_test;
CREATE SCHEMA IF NOT EXISTS casazen_prod;
GRANT ALL ON SCHEMA casazen_test TO postgres;
GRANT ALL ON SCHEMA casazen_prod TO postgres;
```

### Connection strings

Dashboard → **Project Settings → Database**:

| Value | Where |
|---|---|
| Host | `db.xxxxxxxxx.supabase.co` (copy from **Host** field — this is your `[REF]`) |
| Password | Project database password (set at project creation) |
| Database | `postgres` |
| Schema | `casazen_test` or `casazen_prod` (via `SearchPath`, not a separate database) |

**URI format** (Supabase dashboard — auto-converted at startup; include the full `options` query, not `?options` alone):

```
# Test
postgresql://postgres:YOUR_PASSWORD@db.YOUR_REF.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_test

# Production
postgresql://postgres:YOUR_PASSWORD@db.YOUR_REF.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_prod
```

**Npgsql format** (recommended for Railway `ConnectionStrings__DefaultConnection` and for `dotnet ef`):

```
Host=db.YOUR_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD;SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true
```

Replace `YOUR_REF` and `YOUR_PASSWORD` with real values — do not paste `[REF]` / `[PW]` literally.

### Apply migrations (scripts — one-time setup)

Credentials live in **`secrets/supabase.local.env`** (gitignored) and are copied once to **dotnet user-secrets** on your machine. You do not pass `--connection` manually after setup.

#### One-time setup

```powershell
# From backend repo root (Windows)
Copy-Item secrets\supabase.local.env.example secrets\supabase.local.env
# Edit secrets\supabase.local.env → SUPABASE_HOST + SUPABASE_PASSWORD

.\scripts\setup-supabase.ps1    # saves to user-secrets (outside repo)
```

```bash
# macOS / Linux
cp secrets/supabase.local.env.example secrets/supabase.local.env
# edit host + password
./scripts/setup-supabase.sh
```

`secrets/supabase.local.env` example:

```env
SUPABASE_HOST=db.abcdefghijklmnop.supabase.co
SUPABASE_PASSWORD=your-database-password
```

Host: use **Connect → Session pooler** host on Windows (IPv4), e.g. `aws-0-eu-west-1.pooler.supabase.com`, with `SUPABASE_USERNAME=postgres.YOUR_PROJECT_REF`. Direct `db.*.supabase.co` is IPv6-only and often fails locally.

Copy the exact host and username from Supabase **Connect** (not the project URL `https://ref.supabase.co`).

#### Run migrations (every time you add a migration)

```powershell
.\scripts\migrate.ps1              # default: casazen_test
.\scripts\migrate.ps1 -Target prod   # casazen_prod (before production release)
```

```bash
./scripts/migrate.sh test
./scripts/migrate.sh prod
```

Install EF tools once if needed: `dotnet tool install --global dotnet-ef`

#### What gets stored where

| Store | Contents | Committed? |
|---|---|---|
| `secrets/supabase.local.env` | Host + password | No (gitignored) |
| dotnet user-secrets (`casazen-backend-local`) | Full connection strings test/prod | No (local machine) |
| Railway env vars | Same connection string for runtime | No (Railway dashboard) |

`AppDbContextFactory` and `migrate.ps1` read from the env file or user-secrets — not from `localhost` in `appsettings.Development.json`.

#### Expected result

```
Applying migrations to Supabase schema: casazen_test
Build succeeded.
Applying migration '..._InitialCreate'.
Migrations applied successfully to casazen_test.
```

#### Troubleshooting

| Error | Fix |
|---|---|
| `secrets/supabase.local.env not found` | Copy from `.example` and fill in |
| `Failed to connect to 127.0.0.1:5432` | Run `.\scripts\migrate.ps1` — do not run bare `dotnet ef database update` |
| `password authentication failed` | Check password in Supabase; reset if needed |
| `schema "casazen_test" does not exist` | Run **Create schemas** SQL first |

### Supabase keep-alive (free tier pauses after 7 days)

Add a scheduled GitHub Actions ping or use the Supabase dashboard to configure the keep-alive option. The workflow `supabase-keepalive.yml` and its variables are described in `docs/runbooks/ci-backend.md` (section "Supabase keep-alive").

---

## Railway Setup (Backend API)

### Create project

1. https://railway.app → New Project → Deploy from GitHub → `casazen/backend`
2. Service name: `casazen-api`

### Create two environments

Railway → Project → **Environments** → Create:
- `test` — **Settings → Source**: deploy on push to **`develop`** (GitHub integration)
- `production` — **Settings → Source**: deploy on push to **`main`** (GitHub integration)

**PR previews (optional):** Railway → Service → Settings → enable PR deployments if you want a distinct backend URL per PR. Otherwise validate backend on shared test URL after merge to `develop`.

**Wait for CI: keep it off** on both environments. `verify-test` / `verify-prod` are part of `ci-cd.yml` and wait for the deployment of the pushed commit: with "Wait for CI" on, Railway would wait for them and they would wait for Railway, so the job times out and the deploy is skipped. Pull requests stay gated by the `build` job (branch protection).

**Healthcheck path (recommended):** Railway → service → Settings → Deploy → Healthcheck Path = `/api/health/ready`: a new deployment receives traffic only when its database and Hangfire are ready. Details: [`runbooks/health-checks.md`](runbooks/health-checks.md).

### Environment variables per Railway environment

Set in Railway dashboard (Variables tab), per environment:

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
PORT=8080
ConnectionStrings__DefaultConnection=Host=db.YOUR_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD;SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true
# Auth0 — Domain/Audience REQUIRED (the app does not start without them); M2M client for the role sync (docs/runbooks/auth0.md)
Auth0__Domain=[your-tenant.auth0.com]
Auth0__Audience=https://casazen-api
Auth0__ManagementClientId=[M2M client id]
Auth0__ManagementClientSecret=[M2M client secret]
# Stripe — same mode (test keys on test, live keys on production); two webhook endpoints, see "Stripe keys and webhooks"
Stripe__SecretKey=[sk_live_... or sk_test_...]
Stripe__PublishableKey=[pk_live_... or pk_test_...]
Stripe__WebhookSecret=[whsec_... of the platform endpoint /webhooks/stripe]
Stripe__ConnectWebhookSecret=[whsec_... of the Connect endpoint /webhooks/stripe/connect]
# Email — Resend (docs/runbooks/email.md). Required in Production: the app does not start without them.
Email__Provider=Resend
Email__ApiKey=[re_...]
Email__FromAddress=[sender on the domain verified on Resend]
Email__FromName=CasaZen
App__PublicSiteBaseUrl=[public URL of the web app for this environment]
Hangfire__DashboardEnabled=false
# Hangfire schema of THIS environment (production: hangfire_casazen_prod) — never shared, see docs/runbooks/hangfire.md
Hangfire__Schema=hangfire_casazen_test
# CORS — REQUIRED: the web app origin(s) of THIS environment, no default in code (docs/runbooks/cors-security-headers.md)
Cors__AllowedOrigins=[https://<web app host of this environment>]
# Optional, test environment only: regex for the Vercel previews of our own project (empty = no preview allowed)
Cors__VercelPreviewPattern=[e.g. casazen-app-git-[a-z0-9-]+-<team-slug>]
# AI supplier discovery (DeepSeek) — replaces Google Places
Ai__Provider=DeepSeek
Ai__ApiKey=sk-...
Ai__Model=deepseek-v4-flash
Ai__AnthropicBaseUrl=https://api.deepseek.com/anthropic
Ai__OpenAiBaseUrl=https://api.deepseek.com
# Object storage — Supabase Storage (S3 API), REQUIRED: the API does not start without it (docs/runbooks/storage.md)
Storage__Provider=S3
Storage__PublicBaseUrl=https://YOUR_REF.supabase.co/storage/v1/object/public/casazen-<env>-public
Storage__S3__ServiceUrl=https://YOUR_REF.storage.supabase.co/storage/v1/s3
Storage__S3__Region=[region shown in Supabase]
Storage__S3__AccessKeyId=[S3 access key id]
Storage__S3__SecretAccessKey=[S3 secret access key]
Storage__S3__PublicBucket=casazen-<env>-public
Storage__S3__PrivateBucket=casazen-<env>-private
# Data Protection key-ring encryption (recommended, docs/runbooks/storage.md §4)
DataProtection__CertificatePfxBase64=[base64 .pfx]
DataProtection__CertificatePassword=[pfx password]
```

`App__PublicSiteBaseUrl` is the base of **every link in emails** (supplier invite `/register?inviteToken=…`, supplier inbox, guest check-in `/checkin/{token}`): there is no fallback domain in code. Use the web app URL of the matching environment. Email setup, sender domain verification (SPF/DKIM) and send test: [`docs/runbooks/email.md`](runbooks/email.md).

CORS accepts only `Cors__AllowedOrigins` (exact origins) and, when `Cors__VercelPreviewPattern` is set, the Vercel previews of our own project; never any `*.vercel.app`, never with credentials. Security headers (HSTS, `frame-ancestors`) and the web app CSP: [`runbooks/cors-security-headers.md`](runbooks/cors-security-headers.md).

Client IP behind the Railway edge and per-IP rate limits: `ForwardedHeaders__KnownNetworks`, `ForwardedHeaders__ForwardLimit` and `RateLimiting__{Policy}__PermitLimit` (optional, safe defaults). Check the proxy chain of each environment as described in [`runbooks/proxy-ip.md`](runbooks/proxy-ip.md). Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.

`RAILWAY_GIT_COMMIT_SHA` is set by Railway itself on every deployment from GitHub: the health endpoints expose it as `commit` and CI compares it with the pushed commit. Do not set it by hand.

### Variables required in Production

Both Railway environments run with `ASPNETCORE_ENVIRONMENT=Production` (see `secrets/railway.test.variables.example.json`), so everything below applies to **test and production**. "Startup fails" = the new container stops with the list of problems and Railway keeps the previous deployment; "ready …" = what `GET /api/health/ready` reports ([`runbooks/health-checks.md`](runbooks/health-checks.md)).

| Variable | Required | If missing | Runbook |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Development/Testing skip every startup validation below | this file |
| `ConnectionStrings__DefaultConnection` | yes | startup fails (empty value); unreachable → ready `database: unhealthy` (503) | this file § Supabase |
| `Hangfire__Schema` | yes (different per environment) | startup fails when the connection string has no SearchPath; never share it | [`hangfire.md`](runbooks/hangfire.md) |
| `Auth0__Domain`, `Auth0__Audience` | yes | startup fails | [`auth0.md`](runbooks/auth0.md) |
| `Auth0__ManagementClientId`, `Auth0__ManagementClientSecret` | yes for the role sync | ready `auth0: degraded`; onboarding answers `rolesSynced: false` | [`auth0.md`](runbooks/auth0.md) §4-5 |
| `Auth0__ManagementApiDomain` | only with an Auth0 custom domain | Management API calls fail | [`auth0.md`](runbooks/auth0.md) §5 |
| `Email__Provider`, `Email__ApiKey`, `Email__FromAddress` (`Email__FromName` optional) | yes | startup fails | [`email.md`](runbooks/email.md) |
| `App__PublicSiteBaseUrl` | yes (https) | startup fails | [`email.md`](runbooks/email.md) |
| `Storage__Provider=S3`, `Storage__PublicBaseUrl`, `Storage__S3__ServiceUrl`, `Storage__S3__Region`, `Storage__S3__AccessKeyId`, `Storage__S3__SecretAccessKey`, `Storage__S3__PublicBucket`, `Storage__S3__PrivateBucket` | yes | startup fails | [`storage.md`](runbooks/storage.md) |
| `DataProtection__CertificatePfxBase64`, `DataProtection__CertificatePassword` | recommended | warning at startup: Data Protection keys stored unencrypted | [`storage.md`](runbooks/storage.md) §4 |
| `Stripe__SecretKey`, `Stripe__PublishableKey`, `Stripe__WebhookSecret`, `Stripe__ConnectWebhookSecret` | yes once payments are active | ready `stripe: degraded` (the deploy is not blocked); without the Connect secret no direct booking is ever confirmed, without the publishable key the checkout cannot load Stripe | this file § Stripe |
| `Cors__AllowedOrigins` | yes (no origin in code) | startup fails; a malformed entry also stops the startup | [`cors-security-headers.md`](runbooks/cors-security-headers.md) |
| `Cors__VercelPreviewPattern` | no (test only) | Vercel previews rejected by CORS | [`cors-security-headers.md`](runbooks/cors-security-headers.md) |
| `ForwardedHeaders__KnownNetworks`, `ForwardedHeaders__ForwardLimit`, `RateLimiting__{Policy}__PermitLimit` | no (safe defaults) | — | [`proxy-ip.md`](runbooks/proxy-ip.md) |
| `Hangfire__DashboardEnabled` / `Hangfire__DashboardApiKey` | no (default off) | — | [`hangfire.md`](runbooks/hangfire.md) |
| `RAILWAY_GIT_COMMIT_SHA` | set by Railway | `commit: null`: CI cannot verify the deployment and fails | [`health-checks.md`](runbooks/health-checks.md) |

GitHub (backend repo, Actions **variables**): `RAILWAY_TEST_URL`, `RAILWAY_PROD_URL` — required, `verify-test` / `verify-prod` fail without them.

### Stripe keys and webhooks

Stripe has a **test** and a **live** mode with separate keys, webhook endpoints and signing secrets. Railway `test` uses test mode, `production` uses live mode; never mix them (`stripe: degraded` reports a secret key and a publishable key of different modes).

**Keys** — Stripe Dashboard → Developers → API keys (in the right mode):

- `Stripe__SecretKey`: secret key `sk_…` (or a restricted key `rk_…` with the permissions the API uses);
- `Stripe__PublishableKey`: publishable key `pk_…` of the same mode. The API returns it to the checkout page with the host's connected account: without it the checkout calls `loadStripe('')` and cannot take payments.

**Two webhook endpoints.** Payments of direct bookings and rent are created on the host's connected account (Stripe Connect, direct charges), so their events reach only a **Connect** endpoint; plan subscriptions and invoices are events of the platform account. The API verifies each endpoint with its own signing secret. Create both, in each mode, with the **same API version**: `2025-12-15.clover`, the version pinned by Stripe.net 50.1.0 (`Casazen.Infrastructure/Casazen.Infrastructure.csproj`). Stripe.net rejects events of an incompatible API version: the endpoint answers 400 and the log shows `Invalid Stripe … webhook signature` with `Received event with API version …`.

Stripe Dashboard → Developers → Webhooks (Workbench → Event destinations) → **Add endpoint / Add destination** (labels may differ slightly):

| | Platform endpoint | Connect endpoint |
|---|---|---|
| Events from | **Your account** | **Connected accounts** |
| URL | `https://<Railway URL of the environment>/webhooks/stripe` | `https://<Railway URL of the environment>/webhooks/stripe/connect` |
| API version | `2025-12-15.clover` | `2025-12-15.clover` |
| Events | `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`, `charge.refunded`, `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled` | `account.updated`, `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled`, `setup_intent.succeeded` |
| Signing secret (`whsec_…`, "Reveal") | `Stripe__WebhookSecret` | `Stripe__ConnectWebhookSecret` |

The event lists are the ones handled by `Casazen.Infrastructure/External/StripeWebhookHandler.cs`; other events are acknowledged and ignored. Same result with the API (the `secret` is returned only in the creation response), once per endpoint and mode:

```bash
curl https://api.stripe.com/v1/webhook_endpoints -u "<secret key of the mode>:" \
  -d url="https://<Railway URL>/webhooks/stripe/connect" \
  -d api_version="2025-12-15.clover" \
  -d connect=true \
  -d "enabled_events[]=account.updated" \
  -d "enabled_events[]=payment_intent.succeeded" \
  -d "enabled_events[]=payment_intent.payment_failed" \
  -d "enabled_events[]=payment_intent.canceled" \
  -d "enabled_events[]=setup_intent.succeeded"
# platform endpoint: url …/webhooks/stripe, no connect=true, platform event list
```

Check after setting the variables and redeploying:

1. `GET /api/health/ready` with an admin token: `stripe` is `healthy` (anonymous callers see only the status).
2. Stripe Dashboard → each endpoint → send a test event (or `stripe trigger payment_intent.succeeded`): the delivery answers **200**. 400 = wrong signing secret or API version; 500 = secret not set on Railway.
3. Upgrading Stripe.net changes the pinned API version: create both endpoints again with the new version (new secrets), update the two Railway variables, then delete the old endpoints.

Webhook idempotency, subscription states, checkout guard, restricted-key permissions and the Customer portal settings: `docs/runbooks/stripe.md`.

### Get service URLs → GitHub Variables

After first native deploy:
1. Railway → each environment → Service → **Networking** → copy public HTTPS URL
2. GitHub → `casazen/backend` → **Settings → Secrets and variables → Actions → Variables**:

| Variable | Example |
|---|---|
| `RAILWAY_TEST_URL` | `https://casazen-api-test.up.railway.app` |
| `RAILWAY_PROD_URL` | `https://casazen-api.up.railway.app` |

Used only for CI health checks and PR link comments — **not** for Railway runtime (that uses Railway env vars above).

---

## Vercel Setup (Frontend)

### Create project

1. https://vercel.com → New Project → Import `casazen/frontend`
2. Framework preset: **Vite**
3. Build command: `npm run build`
4. Output directory: `dist`

### Environment variables

In Vercel dashboard → Settings → Environment Variables:

| Variable | Preview (develop / PR) | Production (main) |
|---|---|---|
| `VITE_API_BASE_URL` | `https://casazen-api-test.up.railway.app/api` | `https://casazen-api.up.railway.app/api` |
| `VITE_AUTH0_DOMAIN` | `dev-mp6wadq7j6bophl5.us.auth0.com` | same until prod Auth0 tenant is ready |
| `VITE_AUTH0_CLIENT_ID` | `[dev client id]` | same SPA client until prod tenant is ready |
| `VITE_AUTH0_AUDIENCE` | `https://casazen-api` | **`https://casazen-api`** (must match Railway `Auth0__Audience` on **both** environments) |

> **Critical:** Preview and Production must point to **different** `VITE_API_BASE_URL` hosts (test vs prod Railway). If Production accidentally uses the test API URL, staging will look fine while production users hit the wrong backend/schema. After every `main` deploy, CI runs `prod-deploy-smoke` to catch this.

### Git branches and deploy mapping

| Branch | Vercel | Railway |
|---|---|---|
| `develop` | Preview deployment (staging FE, Preview env vars) | `test` environment |
| `main` | Production → `https://casazen-app.vercel.app` (⚠️ see issue #187) | `production` environment |
| PR → `develop` | Per-PR preview URL | Optional PR deploy or shared test after merge |

Configure in Vercel → **Settings → Git**:
- **Production Branch**: `main`
- Leave automatic Preview deployments enabled (covers PRs and `develop` pushes)

### Auto-deploy behaviour

| Event | Result |
|---|---|
| PR opened / updated (base `develop`) | Preview URL → `https://preview-[hash].vercel.app` |
| Push to `develop` | Staging FE deploy (Preview env vars, points to test API) |
| Push to `main` | Production deploy → `https://casazen-app.vercel.app` (⚠️ confirm via issue #187) |

Vercel posts a comment on every PR with the preview URL.

---

## Multi-Environment Promotion Flow

```
PR opened → develop (BE or FE)
    │
    ├─ GitHub Actions: build & test (ci-cd.yml)
    ├─ Railway: optional PR deploy OR shared test after merge
    ├─ Vercel: per-PR preview URL
    ├─ deploy-preview.yml: PR comment with links
    │
    ▼
Merge feature PR → develop
    │
    ├─ Railway (native): deploy test environment
    ├─ ci-cd.yml verify-test: $RAILWAY_TEST_URL/api/health/ready → 200 with commit = pushed SHA
    ├─ Vercel (native): staging FE from develop branch
    └─ Human: bundle check + acceptance on test
    │
    ▼
Stage 05 — Release (release PR: develop → main)
    ├─ release-manager squash-merges develop → main
    ├─ Railway (native): deploy production environment
    ├─ ci-cd.yml verify-prod: $RAILWAY_PROD_URL/api/health/ready → 200 with commit = pushed SHA
    ├─ Vercel (native): production FE from main
    └─ git tag vX.Y.Z on main (changelog only — no deploy trigger)
```

### Version tags

Tags `vMAJOR.MINOR.PATCH` remain the release version label (GitHub Releases, bundle files). They are **not** wired to Railway or Vercel deploy triggers.

---

## Release Bundle Concept

A **Release Bundle** groups related BE and FE features that must reach production together (e.g., BE issue #165 and FE issue #177 are part of the same Epic).

### Bundle file

Created in Stage 05 when an Epic-linked feature is released:

**`Sessions/bundle-<epic-number>.md`**

```markdown
# Release Bundle — Epic #<N>: <Epic title>

## Status: collecting | test-deployed | test-verified | released

## Features
| Issue | Repo | Branch | Test status | Prod status |
|---|---|---|---|---|
| #165 | backend | feature/165-long-term-lease | ✅ deployed, ✅ verified | — |
| #177 | frontend | feature/177-lease-ui | ✅ deployed, ✅ verified | — |

## Test URLs
- BE: https://casazen-api-test.up.railway.app
- FE: https://preview-abc123.vercel.app

## Production Release
- Version: v1.3.0
- Tag: (pending)
- BE prod: https://casazen-api.up.railway.app
- FE prod: https://casazen-app.vercel.app
- Released: (pending)

### Stage 05 Phase D checklist (mandatory)

After merge `develop` → `main`:

1. `.\scripts\migrate.ps1 -Target prod` — apply EF migrations to `casazen_prod` **before** relying on prod traffic
2. `.\scripts\release-smoke.ps1` — health + auth gates + FE SPA
3. `E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke` (frontend repo) — authenticated prod FE + prod API
4. Confirm GitHub Actions `verify-prod` + frontend `e2e-deploy-smoke` on `main` are green
```

### Bundle gate in Stage 05

Before allowing production promotion, the coordinator checks:
1. Does `Sessions/bundle-<epic>.md` exist?
2. Is every row in the Features table showing `✅ deployed, ✅ verified`?
3. If not: block and inform which features are still pending.

---

## GitHub Secrets / Variables (backend repo)

### Required for CI (public URLs only)

| Type | Name | Purpose |
|---|---|---|
| Variable | `RAILWAY_TEST_URL` | Deploy verification after push to `develop` (`verify-test` fails without it); PR comment link |
| Variable | `RAILWAY_PROD_URL` | Deploy verification after push to `main` (`verify-prod` fails without it) |
| Variable | `STAGING_FE_URL` | Vercel develop deployment URL for staging FE smoke (frontend repo) |

### Optional

| Type | Name | Purpose |
|---|---|---|
| Secret | `SUPABASE_ANON_KEY` | `supabase-keepalive.yml` REST ping |
| Variable | `SUPABASE_PROJECT_URL` | Keep-alive (`https://[ref].supabase.co`) |
| Variable | `SUPABASE_DB_HOST` | Keep-alive TCP (`db.[ref].supabase.co`) |

Often auto-created by **Supabase ↔ GitHub** integration. Not used by Railway runtime.

### Not required (native deploy model)

| Name | Why omitted |
|---|---|
| `RAILWAY_TOKEN` | No `railway up` in Actions |
| `RAILWAY_SERVICE_*` | Railway knows service from GitHub link |
| `SUPABASE_CONNECTION_STRING_*` in GitHub | Connection string lives on **Railway** env vars only |

### Where secrets actually live

| Secret / config | Set on |
|---|---|
| Database password / connection string | **Railway** per environment |
| Auth0, Stripe, Email (Resend) | **Railway** per environment |
| `VITE_*` | **Vercel** Preview + Production |
| Supabase service role (if needed for admin scripts) | **Supabase** dashboard or GitHub (optional) |

---

## Monitoring

| Dashboard | URL | What to watch |
|---|---|---|
| Railway | https://railway.app/project/[id] | API logs, CPU/memory, deploy status |
| Supabase | https://app.supabase.com/project/[ref] | DB connections, storage, query logs |
| Vercel | https://vercel.com/[team]/casazen | FE deploys, preview URLs, error tracking |
| Health endpoints | `GET /api/health/live` (process), `GET /api/health/ready` (database, Hangfire, configuration) | 200 healthy/degraded, 503 unhealthy; `commit` = deployed SHA ([`runbooks/health-checks.md`](runbooks/health-checks.md)) |

---

**Last Updated**: 2026-09-23 (FD-12: health checks, deploy verification, Stripe variables and webhooks, required variables)
