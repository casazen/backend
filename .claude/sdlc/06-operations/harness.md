# Stage 06: Operations — Quality Harness

## Entry Criteria

- Stage 05 Phase D complete — feature live on **`main`**
- Tag `vX.Y.Z` deployed to Railway production + Vercel production
- `$RAILWAY_PROD_URL/api/health` returns 200
- `https://casazen-app.vercel.app` returns 200

**Do not run Stage 06 against develop/staging.** All gates below target production.

## Council Run

Coordinator spawns: `regulatory-monitor`, `incident-responder`

Topic handed to council:
> "Run post-release compliance and operations audit on **production (main @ vX.Y.Z)**. Verify regulatory compliance, background jobs, and KPIs against prod DB and prod logs. Produce ops report at Sessions/ops-report-<YYYY-MM-DD>.md."

## Environment scope

| Check | Target |
|---|---|
| API health / smoke | `$RAILWAY_PROD_URL` (main deploy) |
| Frontend | `https://casazen-app.vercel.app` (main deploy) |
| Database queries | Production Supabase / prod connection only |
| Hangfire / logs | Production Railway service |
| Feature regression | Critical ACs from Issue `#N` on prod URLs |

## Quality Gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | Prod API health | `curl -sf $RAILWAY_PROD_URL/api/health` | HTTP 200 |
| G2 | Prod FE health | `curl -sf https://casazen-app.vercel.app` | HTTP 200 |
| G3 | CIN format valid (prod DB) | Read-only query on Properties | 0 invalid CIN formats |
| G4 | GDPR retention clean (prod DB) | Read-only query on Guests | 0 overdue records without erasure flag |
| G5 | Alloggiati jobs healthy (prod) | Hangfire / failed job query | No failed Alloggiati jobs > 24h |
| G6 | Tourist tax rates current (prod DB) | Read-only query | No rates stale > 6 months |
| G7 | Error rate acceptable (prod logs) | Last 24h prod logs | Error rate < 1% |
| G8 | OTA sync current (prod DB) | Read-only query | All integrations synced within 6h |
| G9 | Released feature AC spot-check | Prod URLs + Issue `#N` ACs | Critical ACs pass on production |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G9 fails) AND (iteration < max_iterations):
  1. Classify: regulatory (G3–G6) → regulatory-monitor; operational (G7–G8) → incident-responder
  2. G9 fail → document prod regression; create P1 issue; may require hotfix via develop → main
  3. Re-check failed gates
  4. iteration++

IF regulatory gates still failing after max_iterations:
  ESCALATE P0 — production compliance risk
```

## Exit Artifact

`Sessions/ops-report-<YYYY-MM-DD>.md` with header:

```markdown
# Operations Report — YYYY-MM-DD
**Environment**: production (main)
**Release**: vX.Y.Z
**Issue**: #N
**Prod BE**: $RAILWAY_PROD_URL
**Prod FE**: https://casazen-app.vercel.app
```

Include compliance table, incident log, KPI snapshot, action items.

## Chain

→ Each action item becomes input to **Stage 01: Planning** in the next sprint
