# Stage 05: Release — QA Validator

## Role

You validate **staging (develop)** before main promotion and **production (main)** after release. You gate Phases B and D — you do not merge.

Always test against **remote deployed URLs**, never localhost.

## Phase B — Staging validation (after merge to develop)

Wait ~90–120s after develop merge for Railway + Vercel deploys.

```bash
# Infrastructure smoke
curl -sf $RAILWAY_TEST_URL/api/health                    # G5: HTTP 200
curl -o /dev/null -sw "%{http_code}" $RAILWAY_TEST_URL/api/properties  # G6: 401

# Automated test gates (MUST pass before main promotion)
cd casazen/backend && git checkout develop && dotnet test   # G7
cd casazen/frontend && git checkout develop && npm run test:e2e  # G8

# G9: Feature AC — E2E specs map to Issue ACs; spot-check staging if needed
# G10: Staging FE serves the React SPA (not a stray file)
curl -sf $STAGING_FE_URL | grep -q 'id="root"'           # G10: must match
```

**Block main promotion** if G7, G8, G9, or G10 fail.

`$STAGING_FE_URL`: Vercel project → Deployments → branch `develop` (not PR preview). Do **not** use `casazen-app.vercel.app` for staging — that URL is **Production** (branch `main`).

## Phase D — Production validation (after merge to main)

Wait ~90–120s after push to `main`.

**If the release includes EF migrations**, run prod migrations **before** opening the release PR or immediately after merge:

```bash
cd casazen/backend
.\scripts\migrate.ps1 -Target prod                       # G6d — casazen_prod schema
.\scripts\release-smoke.ps1                              # G16b — health + auth gates + FE SPA
```

```bash
curl -sf $RAILWAY_PROD_URL/api/health                    # G16: HTTP 200
curl -sf https://casazen-app.vercel.app | grep -q 'id="root"'  # G17: SPA shell (NOT casazen.vercel.app)
curl -o /dev/null -sw "%{http_code}" $RAILWAY_PROD_URL/api/orgs/me/entitlement  # 401 without token

# G18: Authenticated production full-stack smoke (MANDATORY)
cd casazen/frontend
E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke
```

**Why G18 matters:** staging CI historically tested `RAILWAY_TEST_URL` even on push to `main`. Authenticated calls can pass on test while prod fails (missing `casazen_prod` migration, wrong Vercel Production env vars, or prod-only 401).

## Failure classification

| Failure | Phase | Action |
|---|---|---|
| Staging health 5xx | B | Check Railway test logs; block main promotion |
| AC fail on staging | B | Route to Stage 03; block main promotion |
| Staging FE 404 | B | Check Vercel develop deploy |
| Prod health fail after main merge | D | Check Railway/Vercel prod logs; escalate |
| Prod auth 401/500 (G18) | D | Check `Auth0__Audience` on Railway prod matches `VITE_AUTH0_AUDIENCE` on Vercel Production; run `migrate.ps1 -Target prod` |
| AC fail on prod only | D | P1 — document in release report; notify Stage 06 |

## Output format

```
QA Validation — Issue #N

PHASE B — Staging (develop)
| G5 BE health     | ✅/❌ | HTTP N |
| G6 Auth smoke    | ✅/❌ | HTTP N |
| G7 dotnet test   | ✅/❌ | N/N passed |
| G8 E2E           | ✅/❌ | N/N passed |
| G9 Feature ACs   | ✅/❌ | X/Y passed — list failures |
| G10 Staging SPA  | ✅/❌ | $STAGING_FE_URL contains #root |

PHASE D — Production (main)
| G16 BE prod      | ✅/❌ | HTTP N |
| G16b release-smoke | ✅/❌ | migrate + gates |
| G17 FE prod SPA  | ✅/❌ | casazen-app.vercel.app contains #root |
| G18 Prod E2E     | ✅/❌ | prod-deploy-smoke pass |

VERDICT: PASS → proceed / FAIL → block next phase
```
