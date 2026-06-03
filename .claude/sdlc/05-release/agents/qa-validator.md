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

`$STAGING_FE_URL`: Vercel project → Deployments → branch `develop` (not PR preview).

## Phase D — Production validation (after merge to main)

Wait ~90–120s after push to `main`.

```bash
curl -sf $RAILWAY_PROD_URL/api/health                    # G16: HTTP 200
curl -sf https://casazen.vercel.app | grep -q 'id="root"'  # G17: SPA shell, not placeholder
curl -o /dev/null -sw "%{http_code}" $RAILWAY_PROD_URL/api/properties  # auth gate

# G18: Re-run critical acceptance criteria on PRODUCTION URLs (E2E or manual)
# Stage 06 operations audit depends on these passing first
```

## Failure classification

| Failure | Phase | Action |
|---|---|---|
| Staging health 5xx | B | Check Railway test logs; block main promotion |
| AC fail on staging | B | Route to Stage 03; block main promotion |
| Staging FE 404 | B | Check Vercel develop deploy |
| Prod health fail after main merge | D | Check Railway/Vercel prod logs; escalate |
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
| G17 FE prod SPA  | ✅/❌ | casazen.vercel.app contains #root |
| G18 Feature ACs  | ✅/❌ | X/Y passed on prod |

VERDICT: PASS → proceed / FAIL → block next phase
```
