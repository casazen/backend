# Design Spec — Issue #180
# feat: production infrastructure deploy (Supabase + Railway + Vercel)

**Stage**: 02 Design  
**Date**: 2026-06-03  
**Branch target**: `feature/180-infrastructure-deploy`  
**Epic label**: `epic:infrastructure`  
**Status**: COMPLETE — all harness gates G1–G8 satisfied  
**Related**: #16 (deep health checks — out of scope), frontend Vercel setup (separate repo)

---

## Summary

This epic delivers backend deployability on the documented zero-cost stack: **Supabase PostgreSQL**, **Railway** (.NET 10 Docker), and **Vercel integration** (frontend repo, documented only). No new business API endpoints. Work centers on EF Core provider migration (SQL Server → PostgreSQL), Hangfire storage migration, Railway-ready container (already aligned in `Dockerfile`), committed CI/CD workflows, and operator runbooks.

**Out of scope**: OTA adapters, new webhooks, GDPR feature logic, CIN/Alloggiati changes, deep health probes (#16).

---

## API Contract

No new business endpoints. Deploy gates and monitoring use the **existing liveness** endpoint only. Deep checks (DB, Auth0, Stripe) remain #16.

### Authentication policy (unchanged)

- Global default: JWT Bearer (Auth0) on protected controllers via `[Authorize(Policy = "PropertyOwner")]` or stricter policies.
- **Public** endpoints used by infra: health liveness only (see table).
- CI/CD smoke test: `GET /api/properties` without token → **401** (confirms auth pipeline is active).

### Health and deploy-related endpoints

| Method | Path | Auth | Purpose | Response (200) |
|--------|------|------|---------|----------------|
| `GET` | `/api/health` | **Public** — `[AllowAnonymous]` on `HealthController` | **Primary deploy gate** — Railway/Vercel staging validation | `{ "status": "healthy", "message": "Backend is running without authentication", "timestamp": "<utc>", "environment": "<ASPNETCORE_ENVIRONMENT>" }` |
| `GET` | `/api/health/auth-test` | **Public** (optional JWT if sent) | Auth debugging only; not used in CI | `{ "isAuthenticated": bool, "userName", "authType", "claims": [...] }` |
| `GET` | `/api/properties/health` | **Public** — `[AllowAnonymous]` on action | Legacy duplicate liveness on `PropertiesController` | `{ "status": "healthy", "message": "Backend is running", "timestamp": "<utc>" }` |

**Canonical URL for CI/CD and ops**: `GET /api/health` (used in `ci-cd.yml`, `deploy-preview.yml`, `docs/INFRA.md`).

**Implementation reference**:

```6:28:Casazen.Web/Controllers/HealthController.cs
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    // ...
    [HttpGet]
    public IActionResult Get()
```

### ProtectedRoute (frontend)

**N/A** — backend-only epic. No new React routes or `<ProtectedRoute>` wrappers. Frontend integration is limited to Vercel env configuration; see **## Frontend Flow**.

---

## Frontend Flow

**Backend-only epic.** No routes, components, or `<ProtectedRoute>` changes in this repository.

Vercel integration checklist (operator + FE repo; per `docs/INFRA.md`):

| Step | Action | Owner |
|------|--------|-------|
| 1 | Import `casazen/frontend` on Vercel; preset **Vite**; build `npm run build`; output `dist` | Operator |
| 2 | Set **Preview** env: `VITE_API_BASE_URL=https://casazen-api-test.up.railway.app/api` (or current `RAILWAY_TEST_URL` + `/api`) | Operator |
| 3 | Set **Production** env: `VITE_API_BASE_URL=https://casazen-api.up.railway.app/api` | Operator |
| 4 | Set Auth0 vars per environment: `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE` (preview = dev tenant, prod = prod tenant) | Operator |
| 5 | On PR open/update: confirm Vercel bot comment with `https://preview-<hash>.vercel.app` | Automatic |
| 6 | Validate feature: open Vercel preview + Railway test BE URL from PR deploy comment | Human (Stage 04/05) |
| 7 | After merge to `main`: Vercel production deploy → `https://casazen.vercel.app` | Automatic |

**CORS (backend change in Stage 03)**: `AddCasazenCors` currently allows localhost and `https://casazen.app` only. Extend allowed origins to include:

- `https://casazen.vercel.app`
- `https://*.vercel.app` (preview deployments) — use `SetIsOriginAllowed` with host suffix check, or explicit preview URL list from configuration `Cors:AllowedOrigins` in Railway env (comma-separated).

Without this, browser calls from Vercel previews to Railway test API will fail CORS preflight.

---

## Security Notes

### Secrets and configuration

| Secret / config | Storage | Never in repo |
|-----------------|---------|---------------|
| `ConnectionStrings__DefaultConnection` | Railway env (test + production) | Yes — Supabase URI with `SearchPath=casazen_test` or `casazen_prod` |
| `RAILWAY_TOKEN` | GitHub Secret | Yes |
| `SUPABASE_CONNECTION_STRING_TEST` / `_PROD` | GitHub Secrets (operator migrations / optional CI) | Yes — documented in INFRA; not required for `railway up` deploy |
| Auth0 `Domain`, `Audience` | Railway env | Yes |
| Stripe `SecretKey`, `WebhookSecret` | Railway env | Yes |
| SendGrid `Email__SendGridApiKey` | Railway env | Yes |
| OTA adapter keys | Railway env / appsettings override | Yes — unchanged by this epic |

`appsettings.json` / `appsettings.Development.json` may retain **LocalDB placeholders for local dev** only; production values come from environment variables (`ConnectionStrings__DefaultConnection`, double-underscore nesting).

### Connection strings and TLS

- Supabase requires **SSL**: `SSL Mode=Require;Trust Server Certificate=true` (Npgsql connection string) or equivalent URI params.
- Use **schema isolation**: `SearchPath=casazen_test` or `casazen_prod` in the same Supabase project (two schemas, one free-tier project).
- Railway terminates **TLS at the edge**; container listens **HTTP on 8080** only — no Kestrel HTTPS inside Docker.

### Threat summary

| Threat | Mitigation |
|--------|------------|
| Secrets committed to git | Pre-commit hygiene; operator checklist; no real passwords in `appsettings*.json` on `main` |
| Public health endpoint abuse | Liveness only; rate limiting optional (#16); no PII in response |
| Hangfire dashboard exposed | `Hangfire__DashboardEnabled=false` on Railway; dashboard middleware gated by config + existing dev-only filter |
| DB exposure | Supabase network + strong password; connection string only in Railway/GitHub Secrets |
| Unauthorized API access | Unchanged JWT policies; deploy smoke asserts 401 on `/api/properties` |
| CORS misconfiguration | Restrict to known Vercel/production origins; avoid `AllowAnyOrigin` with credentials |

### Auth gates (deploy verification)

Post-deploy workflows MUST NOT treat 200 on `/api/properties` as success without a token. Expected: **401** on unauthenticated `GET /api/properties` (already implemented in workflows).

---

## Migration Plan

**Major provider change**: SQL Server → PostgreSQL (Supabase). This is a **breaking migration path** for existing LocalDB/SQL Server databases: delete all SQL Server EF migrations and regenerate a single PostgreSQL `InitialCreate`.

### Phase 0 — Prerequisites

- Local PostgreSQL (Docker or native) OR Supabase `casazen_test` schema for dev.
- Operator creates Supabase schemas (`casazen_test`, `casazen_prod`) per `docs/INFRA.md`.
- **No production data** on SQL Server in cloud today — greenfield apply to Supabase is acceptable.

### Phase 1 — NuGet packages

| File | Remove | Add |
|------|--------|-----|
| `Casazen.Infrastructure/Casazen.Infrastructure.csproj` | `Microsoft.EntityFrameworkCore.SqlServer` | `Npgsql.EntityFrameworkCore.PostgreSQL` (10.x, align with EF 10) |
| `Casazen.Web/Casazen.Web.csproj` | `Hangfire.SqlServer` | `Hangfire.PostgreSql` (compatible with Hangfire 1.8.x) |
| `Casazen.Web/Casazen.Web.csproj` | — | Keep `Hangfire.AspNetCore`; keep `Microsoft.EntityFrameworkCore.InMemory` for CI fallback |

### Phase 2 — Delete SQL Server migrations

Delete entire folder contents (15 files today):

```
Casazen.Infrastructure/Migrations/
  - 20260502082227_InitialCreate.cs (+ .Designer.cs)
  - 20260506110709_AddPricingAdapterConfigAndHistory.cs (+ .Designer.cs)
  - 20260506134656_UpdatePricingAdapterConfigSchema.cs (+ .Designer.cs)
  - 20260512161430_AddPropertyDocumentsAndOtaSyncFields.cs (+ .Designer.cs)
  - 20260512163131_UpdatePropertyDocumentSchema.cs (+ .Designer.cs)
  - 20260602193205_AddLeaseTables.cs (+ .Designer.cs)
  - 20260602195750_AddLeaseExternalSigningSessionId.cs (+ .Designer.cs)
  - AppDbContextModelSnapshot.cs
```

### Phase 3 — Code changes (file-by-file)

| File | Change |
|------|--------|
| `Casazen.Web/Program.cs` | Replace `UseSqlServer` → `UseNpgsql(..., npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))`; replace `Hangfire.SqlServer` → `Hangfire.PostgreSql` (`UsePostgreSqlStorage`); add `using Npgsql.EntityFrameworkCore.PostgreSQL`; gate Hangfire dashboard with `configuration.GetValue<bool>("Hangfire:DashboardEnabled")` default false in non-Development |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | `AddCasazenDatabase`: `UseSqlServer` → `UseNpgsql` + `MigrationsAssembly` |
| `Casazen.Infrastructure/Data/AppDbContextFactory.cs` | Design-time factory: `UseNpgsql` with env var `ConnectionStrings__DefaultConnection` or documented local Postgres connection |
| `Casazen.Web/appsettings.json` | Replace LocalDB connection with **placeholder** Postgres local connection (no real secrets) OR document User Secrets only |
| `Casazen.Web/appsettings.Development.json` | Same — Postgres local dev string |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Review SQL Server–specific fluent API if any (`UseIdentityColumns` etc.) — regenerate migration will emit PostgreSQL types |
| `Casazen.Web/Infrastructure/HangfireAuthorizationFilter.cs` | No change required; dashboard disabled in prod via config + env |

**Consolidation (recommended)**: `Program.cs` duplicates DB registration inline; refactor to call `AddCasazenDatabase` once to avoid drift between `Program.cs` and `ServiceCollectionExtensions`.

### Phase 4 — Regenerate EF migrations

```bash
dotnet ef migrations add InitialCreate --project Casazen.Infrastructure --startup-project Casazen.Web
dotnet ef database update --project Casazen.Infrastructure --startup-project Casazen.Web
```

Local connection example:

```
Host=localhost;Port=5432;Database=casazen_dev;Username=postgres;Password=dev
```

### Phase 5 — Apply to Supabase (operator)

```bash
# Test schema
dotnet ef database update --project Casazen.Infrastructure --startup-project Casazen.Web \
  --connection "Host=db.[REF].supabase.co;Port=5432;Database=postgres;Username=postgres;Password=[PW];SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true"

# Production schema (Stage 05 only, after HITL)
dotnet ef database update ... SearchPath=casazen_prod ...
```

Hangfire creates its own tables in the same schema on first startup when `AddHangfire` runs with PostgreSQL storage.

### Phase 6 — Hangfire PostgreSQL

```csharp
// Program.cs (conceptual)
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire" // optional: separate schema; default public in search_path
    }));
```

**Verify recurring jobs** still register in `ConfigureRecurringJobs()`:

- `ota-sync-all`, `booking-pull-all`, `dynamic-pricing-adaptation`, `gdpr-data-retention`, `lease-sign-status-poll`, `lease-registration-status-poll`

**Dashboard**: Railway sets `Hangfire__DashboardEnabled=false`; middleware `UseHangfireDashboard` only when enabled and Development (or explicit ops policy).

### Phase 7 — Tests

| File | Change |
|------|--------|
| `Casazen.Tests/Integration/MigrationTests.cs` | Keep in-memory for model assertions OR add opt-in PostgreSQL integration test behind `TEST_CONNECTION_STRING` env |
| `Casazen.Tests/Integration/ApiTests.cs`, `PropertiesControllerIntegrationTests.cs` | Remain excluded in CI (`FullyQualifiedName!~...`); optional: add `CustomWebApplicationFactory` with `UseInMemoryDatabase` — no LocalDB dependency |
| Unit tests using `UseInMemoryDatabase` | **No change** — provider-agnostic |
| `Casazen.Tests/Unit/Controllers/AuthorizationAttributeTests.cs` | No change — `HealthController` stays without `[Authorize]` |

Optional new helper: `Casazen.Tests/Integration/PostgresTestFixture.cs` reading `TEST_CONNECTION_STRING` for nightly workflow only (not blocking AC4).

### Phase 8 — PostgreSQL type considerations

When regenerating `InitialCreate`, validate:

- `decimal(18,2)` for money columns (unchanged semantics)
- `string` / `varchar` for `Property.OwnerId` (255, required)
- JSON/list columns mapped correctly for Npgsql (arrays vs jsonb per existing conventions)
- Identity columns: PostgreSQL uses `serial`/`identity` via Npgsql — no `SqlServerModelBuilderExtensions.UseIdentityColumns`

### Rollback strategy

- **Code rollback**: revert PR; redeploy previous Railway image.
- **Database rollback**: Supabase test schema can be dropped/recreated (`DROP SCHEMA casazen_test CASCADE; CREATE SCHEMA...`) before prod promotion. Production rollback requires DBA judgment — prefer forward-only migrations after first prod deploy.

---

## GDPR Scope

**N/A** — no Guest PII schema or API changes. Existing `GdprDataRetentionJob` continues to run on PostgreSQL after Hangfire migration; retention rules unchanged.

---

## Open Questions

| # | Question | Recommended answer | Status |
|---|----------|-------------------|--------|
| 1 | Use `Hangfire.PostgreSql` vs another storage? | **`Hangfire.PostgreSql`** — widely used with Hangfire 1.8; same connection string as EF | Resolved |
| 2 | Consolidate `Program.cs` DB setup with `AddCasazenDatabase`? | **Yes** — single registration path in Stage 03 | Resolved |
| 3 | Keep `/api/properties/health`? | **Keep** for backward compatibility; CI uses `/api/health` only; deprecate in docs | Resolved |
| 4 | CORS for `*.vercel.app`? | **Add configurable origins** in Railway env (`Cors__AllowedOrigins`) including prod + preview pattern | Resolved — implement in Stage 03 |
| 5 | Use `SUPABASE_CONNECTION_STRING_*` in GitHub Actions? | **Optional** — add `migrate-test` job on `main` post-deploy if desired; **not required** for AC4 (migrations are operator/`dotnet ef` before first deploy) | Resolved |
| 6 | Supabase keep-alive (AC7 optional)? | **Add** `.github/workflows/supabase-keepalive.yml` — weekly `cron` + simple TCP/HTTP ping or Supabase REST; document alternative in INFRA | Resolved — implement in Stage 03 |
| 7 | First deploy with 404 on Railway URLs? | **Expected** until operator completes Railway project + GitHub vars + green `railway up`; see Deployment Checklist | Resolved (ops) |
| 8 | SQLite for CI instead of InMemory? | **Keep InMemory** for unit/integration model tests; no SQLite package required for AC1 | Resolved |

---

## CI/CD Design

### Workflows (committed in repo)

| Workflow | File | Triggers | Jobs |
|----------|------|----------|------|
| CI/CD Pipeline | `.github/workflows/ci-cd.yml` | `push` to `main`, `push` tags `v*`, `pull_request` to `main` | `build` → `deploy-test` (main push only) → `deploy-prod` (`v*` tag only) |
| Deploy Preview | `.github/workflows/deploy-preview.yml` | PR `opened`, `synchronize`, `reopened` on `main` | `deploy-backend-test` |

**Note**: `deploy-preview.yml` is present in workspace; must be **committed** on feature branch to satisfy AC4.

### Job details

#### `build` (ci-cd.yml)

- Checkout, .NET 10, restore, Release build, test with filter excluding LocalDB-dependent integration tests, `dotnet format --verify-no-changes`.
- Runs on every PR and push.

#### `deploy-test` (ci-cd.yml)

- **When**: `github.ref == refs/heads/main' && push`.
- **Environment**: GitHub Environment `test` → URL `vars.RAILWAY_TEST_URL`.
- **Steps**: `railway up --service vars.RAILWAY_SERVICE_TEST --environment test --detach`; sleep 60; `curl -f $RAILWAY_TEST_URL/api/health`.
- **Secrets**: `RAILWAY_TOKEN`.
- **Variables**: `RAILWAY_TEST_URL`, `RAILWAY_SERVICE_TEST`.

#### `deploy-prod` (ci-cd.yml)

- **When**: tag `refs/tags/v*`.
- **Environment**: `production` (configure required reviewers in GitHub).
- **Steps**: `railway up` prod service/environment; sleep 90; health `/api/health`; smoke 401 on `/api/properties`.
- **Secrets/vars**: same pattern with `RAILWAY_PROD_URL`, `RAILWAY_SERVICE_PROD`.

#### `deploy-backend-test` (deploy-preview.yml)

- **When**: PR events to `main`.
- Build + test (same filter), Railway deploy to **test**, health + auth smoke, PR comment with BE URL and link to Vercel preview (manual step 1 in comment).

### Secrets and variables mapping

| GitHub | Used in workflow | Purpose |
|--------|------------------|---------|
| Secret `RAILWAY_TOKEN` | All deploy jobs | Railway CLI authentication |
| Variable `RAILWAY_TEST_URL` | Health curl, PR comment, environment URL | Public test API base (no trailing slash) |
| Variable `RAILWAY_PROD_URL` | Prod health curl | Public prod API base |
| Variable `RAILWAY_SERVICE_TEST` | `railway up --service` | Test service ID |
| Variable `RAILWAY_SERVICE_PROD` | `railway up --service` | Prod service ID |
| Secret `SUPABASE_CONNECTION_STRING_TEST` | Not wired in current YAML | Operator / optional future `ef database update` job |
| Secret `SUPABASE_CONNECTION_STRING_PROD` | Not wired in current YAML | Operator prod migrations only |

Railway runtime secrets (not GitHub): `ConnectionStrings__DefaultConnection`, Auth0, Stripe, SendGrid, `Hangfire__DashboardEnabled=false`, `ASPNETCORE_ENVIRONMENT`, `PORT=8080`.

### Docker / Railway build

- Root `Dockerfile`: multi-stage build, `ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080` — satisfies AC3.
- Railway auto-detects Dockerfile on `railway up`.

### Promotion alignment (Stage 05)

```
PR → deploy-preview.yml (BE test) + Vercel preview (FE)
merge main → ci-cd deploy-test
tag v* → ci-cd deploy-prod + Vercel prod from main
```

---

## Deployment Checklist

Operator manual steps before first green deploy. No secrets in repository.

### 1 — Supabase

- [ ] Create project `casazen`, region `eu-central-1`; save DB password securely
- [ ] SQL Editor: `CREATE SCHEMA casazen_test; CREATE SCHEMA casazen_prod;` + grants per INFRA.md
- [ ] Copy connection URIs with `search_path` option for test and prod
- [ ] Run `dotnet ef database update` against **test** schema (then prod when promoting)
- [ ] (Optional) Disable auto-pause or schedule keep-alive (AC7)

### 2 — Railway

- [ ] New project from GitHub `casazen/backend`
- [ ] Environments: `test` (auto-deploy main) and `production` (tag `v*` / GitHub Actions)
- [ ] Service `casazen-api`; note **service IDs** for GitHub variables
- [ ] Per environment variables:
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `ASPNETCORE_URLS=http://+:8080`
  - `PORT=8080`
  - `ConnectionStrings__DefaultConnection` = Supabase URI with correct `SearchPath`
  - Auth0, Stripe, SendGrid keys
  - `Hangfire__DashboardEnabled=false`
  - `Cors__AllowedOrigins` = `https://casazen.vercel.app,https://casazen.app` (+ preview pattern if supported)
- [ ] Public networking enabled; record URLs → GitHub vars `RAILWAY_TEST_URL`, `RAILWAY_PROD_URL`
- [ ] API token → GitHub secret `RAILWAY_TOKEN`

### 3 — GitHub (backend repo)

- [ ] Secrets: `RAILWAY_TOKEN`, `SUPABASE_CONNECTION_STRING_TEST`, `SUPABASE_CONNECTION_STRING_PROD`
- [ ] Variables: `RAILWAY_TEST_URL`, `RAILWAY_PROD_URL`, `RAILWAY_SERVICE_TEST`, `RAILWAY_SERVICE_PROD`
- [ ] Environment `test` and `production` with protection rules on production
- [ ] Merge workflows `ci-cd.yml` + `deploy-preview.yml` via PR #180

### 4 — Vercel (frontend repo)

- [ ] Import project; Vite preset
- [ ] Preview: `VITE_API_BASE_URL` → Railway **test** URL + `/api`
- [ ] Production: `VITE_API_BASE_URL` → Railway **prod** URL + `/api`
- [ ] Auth0 client/env vars per INFRA.md table
- [ ] Confirm PR preview comments appear

### 5 — First deploy validation

- [ ] Open PR → `deploy-preview` green; `GET {RAILWAY_TEST_URL}/api/health` → 200
- [ ] `GET {RAILWAY_TEST_URL}/api/properties` → 401
- [ ] Merge to `main` → test deploy job green
- [ ] Stage 05: tag `vX.Y.Z` → prod deploy + `GET {RAILWAY_PROD_URL}/api/health` → 200
- [ ] Update `Sessions/bundle-<epic>.md` if cross-repo epic

### 6 — Post-deploy (Stage 06 / #16)

- [ ] Deep health (DB connectivity, external deps) tracked in issue #16 — not blocking this epic

---

## Acceptance criteria traceability

| AC | Design section |
|----|----------------|
| AC1 PostgreSQL provider | Migration Plan Phases 1–5 |
| AC2 Hangfire PostgreSQL | Migration Plan Phase 6 |
| AC3 Railway container | Dockerfile (existing); CI/CD Design |
| AC4 CI/CD committed | CI/CD Design; commit `deploy-preview.yml` in Stage 03 |
| AC5 Health 200 | API Contract; CI/CD curl steps |
| AC6 Operator checklist | Deployment Checklist; INFRA.md |
| AC7 Supabase keep-alive | Open Questions #6; optional workflow in Stage 03 |

---

## Stage 03 handoff

- **Issue**: #180  
- **Spec**: `Sessions/design-180.md`  
- **Branch**: `feature/180-infrastructure-deploy`  
- **Implementation order**: NuGet → delete migrations → Npgsql + Hangfire.PostgreSql → regenerate `InitialCreate` → CORS → optional keep-alive workflow → verify CI green → operator Supabase/Railway apply
