# Runbook: backend CI (casazen/backend)

Workflows in `.github/workflows/`. Task FD-02 (audit defects A9-26 and the backend part of A9-24), on top of FD-04 (tests on PostgreSQL) and FD-12 (deploy verification, `docs/runbooks/health-checks.md`). Frontend and mobile CI: `docs/runbooks/ci-frontend.md`.

| Workflow | Job (check name) | Trigger | Gate |
|---|---|---|---|
| `ci-cd.yml` | `Build & Test` | PR and push to `develop` / `main` | **Required**: `dotnet restore` (NuGet audit, High/Critical = error), vulnerable packages gate (`scripts/check-vulnerable-packages.sh`), `dotnet build -c Release`, `dotnet test` on a `postgres:16` service, `dotnet format --verify-no-changes` |
| `ci-cd.yml` | `Verify Railway Test (native deploy)` | push `develop` | The test API runs this commit and `/api/health/ready` answers 200, then smoke tests (FD-12) |
| `ci-cd.yml` | `Verify Railway Production (native deploy)` | push `main` | Same on production (FD-12) |
| `deploy-preview.yml` | `Post environment links` | PR to `develop` | Comment with the environment links, no gate |
| `supabase-keepalive.yml` | `Ping Supabase` | weekly (Monday 08:00 UTC), manual | Fails when a configured ping fails; explicit skip when nothing is configured (section 4) |

No backend Golden Journey gate yet. The former `e2e-golden-journey.yml` only ran `echo`, so its check was always green (A9-24): it was removed. The real end-to-end gate (UI → API → DB on an ephemeral stack) arrives with task **FN-03**.

## 1. Required checks (one-time, repo admin)

GitHub → `casazen/backend` → **Settings → Branches** (or **Rules → Rulesets**), for **both** `develop` and `main`:

1. Enable **Require status checks to pass before merging** and **Require branches to be up to date before merging**.
2. Add the check **`Build & Test`**.
3. If the removed workflow's check **`pointer`** (workflow "E2E Golden Journey") is in the list, **remove it**: nothing reports it any more, so a required `pointer` would block every merge ("Expected — Waiting for status to be reported").

From the CLI, when branch protection already exists:

```bash
for b in develop main; do
  gh api -X PATCH "repos/casazen/backend/branches/$b/protection/required_status_checks" \
    -F strict=true -f 'contexts[]=Build & Test'
done
```

The PATCH replaces the whole list of required checks, so `pointer` disappears too.

## 2. NuGet vulnerability gate

Two layers, same rule: **High and Critical advisories fail, direct or transitive packages alike**. Moderate and Low stay warnings.

| Layer | Where | Effect |
|---|---|---|
| NuGet audit at restore | `Directory.Build.props`: `NuGetAudit`, `NuGetAuditMode=all` (transitive too), `WarningsAsErrors` `NU1903;NU1904` | `dotnet restore` / `dotnet build` fail everywhere: locally, in CI and in the Docker build on Railway. When the Railway build fails, the deployment already running stays live. |
| Explicit gate in CI | `scripts/check-vulnerable-packages.sh` (runs `dotnet list package --vulnerable --include-transitive`) | Lists every vulnerable package with severity, project, direct/transitive and advisory, and fails on High/Critical. It runs even when restore has already failed, to show the full report. |

Run it locally the same way:

```bash
dotnet restore Casazen.sln
bash scripts/check-vulnerable-packages.sh          # exit 1 on High/Critical
```

### When the gate fails

1. Read the report: package, version, `direct` or `transitive`, project, advisory link.
2. `direct`: update the `PackageReference` to the patched version (for `Microsoft.*` 10.0.x packages keep them all on the same patch).
3. `transitive`: find the parent with `dotnet nuget why Casazen.sln <package>`. Update the parent to a version that depends on the patched release. If there is none, add an explicit `PackageReference` to the patched version in the project that brings it in.
4. If the package is not used any more, remove the reference (as done with SendGrid).
5. `dotnet restore`, rerun the script, then build and test.

### No patched version yet (temporary exception)

Only when no fixed version exists and the risk has been assessed. Add the advisory to `Directory.Build.props`, with the reason and a review date:

```xml
<ItemGroup>
  <!-- <package> <version>: no patched release on <date>; not reachable because <reason>. Review by <date>. Issue #<n>. -->
  <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />
</ItemGroup>
```

Restore honours `NuGetAuditSuppress`, and so does the script (`dotnet list package` alone would ignore it): the advisory is then reported as a `suppressed` warning and no longer blocks. Remove the suppression as soon as a patched version is published. Never add `NU1903`/`NU1904` to `NoWarn`, and never set `NuGetAudit=false`.

### Packages at FD-02 (2026-09-24)

`dotnet list package --vulnerable --include-transitive`: **no vulnerable packages**.

| Change | Advisories closed |
|---|---|
| `Microsoft.AspNetCore.DataProtection` 10.0.1 → 10.0.12 (Infrastructure) | GHSA-9mv3-2cwr-p262 (Critical) |
| `System.Security.Cryptography.Xml` 10.0.7 → 10.0.12 (Infrastructure) | GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-mmjf-rqrv-855v (High) |
| `SendGrid.Extensions.DependencyInjection` removed (Web): the provider is Resend (FD-13) and no code used it | `starkbank-ecdsa` 1.3.1: GHSA-j3jw-j2j8-2wv9 (Critical), GHSA-9wx7-jrvc-28mm (High) |
| `Testcontainers.PostgreSql` 4.3.0 → 4.15.0 (Tests) | `SSH.NET` 2024.2.0 → 2026.0.0: GHSA-q939-rpr3-3284, GHSA-mggc-4xg6-vcxf (High) |
| Every other `Microsoft.*` 10.0.x package → 10.0.12 (EF Core, DataProtection.EntityFrameworkCore, JwtBearer, Mvc.Testing, Extensions.*) | alignment with the 10.0.12 runtime, no advisory |

## 3. EF Core tools (`dotnet-ef`)

The repository has no local tool manifest (`.config/dotnet-tools.json`): `dotnet-ef` is a global tool. It must have the same version as the `Microsoft.EntityFrameworkCore.*` packages, now **10.0.12**. With an older tool every command prints `The Entity Framework tools version '10.0.0' is older than that of the runtime '10.0.12'`.

```bash
dotnet tool update --global dotnet-ef --version 10.0.12   # installs it when missing
dotnet ef --version
```

Update it every time the EF Core packages change patch. CI does not use `dotnet-ef`: the migrations are checked by the tests (FD-04: apply-all on PostgreSQL + `HasPendingModelChanges`).

## 4. Supabase keep-alive

`supabase-keepalive.yml` pings the Supabase project every week so that the free tier does not pause it after 7 days of inactivity. Configuration: GitHub → `casazen/backend` → **Settings → Secrets and variables → Actions**.

| Kind | Name | Value |
|---|---|---|
| Variable | `SUPABASE_PROJECT_URL` | `https://<ref>.supabase.co` |
| Secret | `SUPABASE_ANON_KEY` | The project's anon (public) API key. Never the service role key. |
| Variable | `SUPABASE_DB_HOST` | Optional: host for the TCP check on port 5432 |

Behaviour:

| Configuration | Result |
|---|---|
| `SUPABASE_PROJECT_URL` + `SUPABASE_ANON_KEY` | `GET <url>/rest/v1/` with the key, 3 retries. HTTP error or timeout → **run fails**. |
| `SUPABASE_PROJECT_URL` without `SUPABASE_ANON_KEY` | REST ping skipped with a visible warning, run green. |
| `SUPABASE_DB_HOST` | TCP connection to `<host>:5432`, 3 attempts. Unreachable → **run fails**. |
| Nothing configured | Warning "No keep-alive ping sent", run green. |

The former `|| echo …` after every command made the run green even when the ping failed (FD-02).

When the run fails:

- REST 401/403: the key is wrong or has been rotated. Copy the current anon key from Supabase → **Project Settings → API** into the secret.
- REST timeout or 5xx: the project may already be paused. Restore it from the Supabase dashboard, then run the workflow manually (**Actions → Supabase Keep-Alive → Run workflow**).
- TCP failure: GitHub-hosted runners have no IPv6. If the direct host `db.<ref>.supabase.co` only resolves to IPv6 (project without the IPv4 add-on), the TCP check cannot pass from GitHub: leave `SUPABASE_DB_HOST` unset and rely on the REST ping. The TCP check only proves that the port answers; it runs no query.

## 5. Verify

1. Open a PR against `develop`: the `Build & Test` check shows the steps "Vulnerable packages (fails on High/Critical, including transitive)", "Build", "Test", "Check formatting". There is no "E2E Golden Journey" workflow any more.
2. Locally, the same commands as CI (with `TEST_POSTGRES_CONNECTION` set, see `AGENTS.md`):
   `dotnet restore && bash scripts/check-vulnerable-packages.sh && dotnet build -c Release --no-restore && dotnet test -c Release --no-build && dotnet format --verify-no-changes`.
3. Keep-alive: **Actions → Supabase Keep-Alive → Run workflow**. With the variables set, the log shows "Supabase REST ping OK"; without them, a warning annotation and no ping.
4. Optional workflow syntax check: `actionlint .github/workflows/*.yml`.
