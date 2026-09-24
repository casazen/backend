# Runbook: health checks, startup validation and deploy verification

Task FD-12. Audit defects A9-19 (issue #16) and A3-24. Before FD-12 `/api/health` always answered `{"status":"healthy"}`
and CI waited 90 s and then called it, so a deploy with the database down, Hangfire stopped or a migration missing was
green, and CI could even be checking the **previous** container.

## Endpoints

All anonymous, no rate limit, `Cache-Control: no-store`.

| Endpoint | Checks | HTTP |
|---|---|---|
| `GET /api/health/live` | none: the process answers | always 200 |
| `GET /api/health/ready` | `database`, `hangfire`, `email`, `storage`, `stripe`, `auth0` | 200 when `healthy` or `degraded`, **503** when `unhealthy` |
| `GET /api/health` | same as ready | same as ready |

Body (anonymous caller):

```json
{
  "status": "degraded",
  "commit": "0ddb5dd4e45e3709772353a4574ddc6c6ef42b70",
  "checks": [
    { "name": "auth0", "status": "healthy" },
    { "name": "database", "status": "healthy" },
    { "name": "stripe", "status": "degraded" }
  ]
}
```

Anonymous callers get only the name and status of each check: no description, no exception message, no configuration
value. A platform admin (Auth0 role `Admin`, bearer token) also gets a `description` per check, which names the missing
variables (e.g. `missing or placeholder Stripe__ConnectWebhookSecret`) but never their values. The same descriptions are
written to the Railway logs by `Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService` (warning for
`degraded`, error for `unhealthy`, with the exception).

## What each check means

| Check | `healthy` | `degraded` (200) | `unhealthy` (503) |
|---|---|---|---|
| `database` | PostgreSQL answers through the app's `AppDbContext` and every EF migration of this build is applied | in-memory database (Development/Testing only) | unreachable, timeout (5 s), **pending migrations**, or no database outside Development/Testing |
| `hangfire` | storage (this environment's schema, FD-11) answers and **this process's** server sent a heartbeat in the last 2 minutes | only another instance's server is alive | storage unreachable, no live server, or Hangfire not configured outside Development/Testing |
| `email` | Resend key, sender and `App__PublicSiteBaseUrl` valid (FD-13) | something missing: emails are skipped. Outside Development/Testing this cannot happen: the app does not start | — |
| `storage` | S3 (Supabase Storage, FD-07) | local-disk provider (Development/Testing only) | invalid configuration |
| `stripe` | the four keys below present, right prefixes, secret and publishable key in the same mode (live/test) | anything missing, a placeholder, a wrong prefix or mixed modes: payments are not fully usable | — |
| `auth0` | `Auth0__Domain`, `Auth0__Audience` and the Management API client (M2M) configured | M2M client missing (role sync off) or the deprecated static `Auth0__ManagementApiToken` in use; in Development/Testing also `Auth0__Domain` / `Auth0__Audience` missing | `Auth0__Domain` / `Auth0__Audience` missing: nobody can sign in. Outside Development/Testing the app does not even start |

`degraded` means "an optional integration is not configured": the deploy is accepted, CI prints a warning for each
degraded check. Stripe is optional until payments are switched on; once they are, `stripe` must be `healthy`.

The configuration checks never call the external provider (no Auth0 token, no Stripe or S3 request per probe): they
check presence and format only. Placeholders committed in `appsettings.json`, `secrets/*.example.json` or the docs
(`YOUR_…`, `your-domain…`, `dev-xxxxxxxx…`, `…...`, `[…]`) count as missing.

## Startup validation

Outside `Development` and `Testing` (so on both Railway environments, which run with `ASPNETCORE_ENVIRONMENT=Production`)
the app **does not start** without the settings it cannot work without. Railway then keeps the previous deployment
running and the deploy log shows the list of problems.

| Setting | Validated by | Error in the deploy log |
|---|---|---|
| `ConnectionStrings__DefaultConnection` (empty value) | FD-12, `RequiredConfiguration` | `ConnectionStrings__DefaultConnection is missing …` |
| `Auth0__Domain` (host only, no `https://`), `Auth0__Audience` | FD-12, `Auth0OptionsValidator` (`ValidateOnStart`) | `OptionsValidationException: Auth0__Domain is missing or a placeholder …` |
| `Hangfire__Schema` (or a SearchPath) | FD-11 | `Hangfire schema is ambiguous …` |
| `Email__*`, `App__PublicSiteBaseUrl` | FD-13 | `OptionsValidationException: Email__ApiKey is missing …` |
| `Storage__*` | FD-07 | `OptionsValidationException: Storage:S3:… is required.` |

Stripe and the Auth0 Management client are **not** validated at startup: they are optional and reported as `degraded`.
The full list of variables is in [`docs/INFRA.md`](../INFRA.md#variables-required-in-production).

## Commit of the running build

Every health body carries `commit`, the commit the running container was built from:

1. `RAILWAY_GIT_COMMIT_SHA`, set by Railway on every deployment triggered by its GitHub integration. Nothing to
   configure.
2. Otherwise `GIT_COMMIT_SHA`, for any other host (set it at deploy time).

Only a hexadecimal SHA is exposed; any other value gives `"commit": null`. A deployment started by hand with `railway up`
(not from GitHub) has no `RAILWAY_GIT_COMMIT_SHA`: CI cannot verify it and fails.

The startup log also prints `Commit: <sha>`.

## CI: `verify-test` / `verify-prod`

`ci-cd.yml` runs `scripts/verify-deploy.sh <url> <github.sha> 1200` after a push to `develop` (test) or `main`
(production). The script polls `/api/health/ready` every 15 s and:

- **fails at once** when the GitHub variable (`RAILWAY_TEST_URL` / `RAILWAY_PROD_URL`) is not set;
- waits until the API exposes the pushed commit (or a later commit of the branch that contains it, when Railway
  already deployed a newer push);
- passes when that deployment answers 200 (`healthy` or `degraded`, one warning per degraded check);
- fails when that deployment answers 503 for 8 polls in a row (about 2 minutes), printing each check's status;
- fails after 20 minutes when the commit never shows up (deploy failed, skipped or still building), or when the API
  does not expose a commit.

The smoke steps after it (401 on protected routes, never 500) run only when the verification passed.

Product owner setup (once):

1. GitHub → `casazen/backend` → Settings → Secrets and variables → Actions → **Variables**: `RAILWAY_TEST_URL` and
   `RAILWAY_PROD_URL` = public Railway URLs, without trailing slash. They are now mandatory.
2. Railway → service → each environment → Settings → **Wait for CI: off**. With it on, Railway waits for the whole
   `ci-cd.yml` run, which includes the verify job waiting for the deployment: the job times out and Railway skips the
   deploy. Pull requests are still gated by the `build` job through branch protection.
3. Optional: Railway → service → Settings → Deploy → **Healthcheck Path** = `/api/health/ready`. Railway then switches
   traffic to a new deployment only after it answers 200, so a deployment with the database unreachable or migrations
   missing never replaces the running one. Use `/api/health/live` instead if a degraded-but-running container must
   always replace the old one (with `ready`, only `unhealthy` blocks the switch; `degraded` answers 200).

## Troubleshooting

| Symptom | Meaning / action |
|---|---|
| CI: `API URL not configured` | Set the GitHub variable (setup step 1). |
| CI: `does not expose the commit of the running build` | The deployment still runs a build older than FD-12 (first deploy: wait, then re-run the job), or it was not triggered from GitHub (no `RAILWAY_GIT_COMMIT_SHA`). |
| CI: `Railway still serves commit X instead of Y` | The new deployment failed (Railway → Deployments → build/deploy logs: startup validation errors are listed there), was skipped ("Wait for CI" on, setup step 2), or the build took longer than 20 minutes. |
| `database: unhealthy` | Supabase paused or unreachable (free tier pauses after 7 days of inactivity), wrong connection string, or migrations not applied. The startup of a Production container applies them; on a database migrated by hand run `scripts/migrate.sh test|prod`. |
| `hangfire: unhealthy` | Hangfire storage unreachable or its server not running: no email, Stripe webhook or recurring job is processed. Check the Hangfire schema (runbook [`hangfire.md`](hangfire.md) §5) and the logs of the container. |
| `hangfire: degraded` | Only another instance's server is alive (e.g. right after a deploy): it should turn `healthy` within a minute. |
| `stripe: degraded` | Set the missing Stripe variables and create both webhook endpoints: [`docs/INFRA.md` § Stripe](../INFRA.md#stripe-keys-and-webhooks). |
| `auth0: degraded` | Configure the M2M client ([`auth0.md`](auth0.md) §4-5). |
| `email: degraded` / `storage: degraded` | Only in Development/Testing: see [`email.md`](email.md) and [`storage.md`](storage.md). |
