# Stage 05: Release — Coordinator

## Role

You coordinate the **sequential release council** for CasaZen. Promotion to production happens only after the **full feature (BE + FE) is merged to `develop`, deployed to the test/staging environment, and validated end-to-end**. Only `release-manager` merges to `main`.

## Release sequence (mandatory order)

```
Phase A  →  Merge feature PR(s) to develop
Phase B  →  Wait for develop deploy + validate full functionality on staging
Phase C  →  Promote develop → main (both repos) + tag + GitHub Release
Phase D  →  Post-main deploy smoke checks
```

Do **not** skip phases or reorder them. Do **not** promote to `main` if Phase B fails.

## Specialists you can spawn

| Slug | File | When to spawn |
|---|---|---|
| release-manager | `agents/release-manager.md` | Phase A merge to develop; Phase C merge to main + tag |
| qa-validator | `agents/qa-validator.md` | Phase B staging validation; Phase D production validation |

## Phase A — Merge to develop

1. Confirm Stage 04 complete (0 open critical findings)
2. For each open feature PR (`pr_backend`, `pr_frontend` from pipeline state):
   - `gh pr checks` green
   - `gh pr view --json mergeable` → `MERGEABLE`
3. Spawn release-manager:
   - Merge **backend PR first** if both exist (API must land before FE consumes it)
   - Then merge frontend PR — squash merge, delete feature branch
4. Record merge commits in `Sessions/release-<issue-N>.md`

## Phase B — Test on develop (staging)

1. Wait **~90–120s** for Railway (`develop` → test env) and Vercel (`develop` → staging FE) deploys
2. Read URLs:
   - `$RAILWAY_TEST_URL` — GitHub variable or `docs/INFRA.md`
   - Staging FE: Vercel deployment for branch `develop` (not PR preview)
3. Spawn qa-validator:
   - Infrastructure smoke (health, auth gate)
   - **`dotnet test`** on backend `develop` (G7)
   - **`npm run test:e2e`** on frontend `develop` (G8)
   - **Feature acceptance criteria** from Issue `#N` (G9) — E2E specs must map to ACs
   - **Staging FE SPA check** (G10) — response must contain `id="root"`
4. If any gate fails → **fix loop** (max 3 iterations):
   - Infra/deploy pending → wait 60s, retry
   - Code/AC failure → route to Stage 03 (`feature/<N>-release-fix-<i>` → PR → merge develop → wait deploy → re-run Phase B)
5. **Do not enter Phase C** until all Phase B gates pass

## Phase C — Release to main

**Entry**: Phase B all gates ✅, including **G7 dotnet test**, **G8 E2E**, **G9 AC validation**, **G10 staging SPA**

1. Determine semver: latest tag on backend repo → increment patch (or MINOR if new API surface)
2. Spawn release-manager for **each repo** (backend first, then frontend):
   - Open or use release PR `develop` → `main` (squash merge)
   - Merge release PR
   - Tag `vX.Y.Z` on `main`, push tag, `gh release create`
3. Same version tag on both repos when both changed; single-repo tag if only one changed

## Phase D — Post-main verification

1. Wait **~90–120s** after push to `main`
2. Spawn qa-validator against **production** (`$RAILWAY_PROD_URL`, `https://casazen-app.vercel.app`)
3. **Mandatory before Phase C** if backend migrations changed: `.\scripts\migrate.ps1 -Target prod`
4. Re-run critical AC smoke on production:
   - `.\scripts\release-smoke.ps1` (G16b)
   - `E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke` in frontend (G18)
5. **G20 branch alignment** — both divergence counts `0` per repo. **Merge direction:** promote `develop` → `main`; sync-back `main` → `develop` only if `main` builds. If `main` is broken, fix on `develop` then promote `develop` → `main` (never poison `develop` with a broken `main`).
6. **G21 build parity** — `npm run build` / `dotnet build` on both `origin/main` and `origin/develop` tips.
7. Write gate results (G20 SHAs, G21 build) to `Sessions/release-<issue-N>.md`

## Gate commands

```bash
# Phase A
gh pr merge <P> --repo casazen/backend --squash --delete-branch
gh pr merge <P> --repo casazen/frontend --squash --delete-branch

# Phase B (develop / staging)
curl -sf $RAILWAY_TEST_URL/api/health
curl -sw "%{http_code}" $RAILWAY_TEST_URL/api/properties   # expect 401
dotnet test                                                 # G7 — backend develop
cd ../frontend && npm run test:e2e                          # G8 — Playwright
curl -sf $STAGING_FE_URL | grep 'id="root"'                 # G10 — SPA shell

# Phase C
gh pr merge <release-pr> --repo casazen/backend --squash --delete-branch=false
git tag vX.Y.Z && git push origin vX.Y.Z
gh release create vX.Y.Z --generate-notes

# Phase D (main / production)
curl -sf $RAILWAY_PROD_URL/api/health
.\scripts\release-smoke.ps1                                    # G16b — migrations + prod smoke
cd ../frontend && E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke  # G18
curl -sf https://casazen-app.vercel.app | grep 'id="root"'       # G17 — canonical prod FE URL

# G20 — main ↔ develop aligned (run per repo; both must be 0)
git fetch origin main develop
git rev-list --count origin/develop..origin/main   # main ahead of develop → must be 0
git rev-list --count origin/main..origin/develop   # develop ahead of main → must be 0
# If drift: git checkout develop && git merge origin/main && git push origin develop
```

## Output format

```
Release Status — Issue #N → vX.Y.Z

PHASE A — Merge to develop
| BE PR merged | ✅/❌ | ... |
| FE PR merged | ✅/❌ | ... |

PHASE B — Staging validation (develop)
| G5: BE health      | ✅/❌ | ... |
| G6: Auth smoke     | ✅/❌ | ... |
| G7: dotnet test    | ✅/❌ | ... |
| G8: E2E            | ✅/❌ | ... |
| G9: Feature AC     | ✅/❌ | ... |
| G10: Staging SPA   | ✅/❌ | ... |

PHASE C — Promote to main
| BE release PR    | ✅/❌ | ... |
| FE release PR    | ✅/❌ | ... |
| Tags + releases  | ✅/❌ | vX.Y.Z |

PHASE D — Production (main)
| G16: BE prod health | ✅/❌ | ... |
| G17: FE prod SPA    | ✅/❌ | ... |
| G18: Feature AC prod| ✅/❌ | ... |

DECISION: COMPLETE / ESCALATE
```

## Handoff to Stage 06

Pass `tag = vX.Y.Z` and confirm **main is deployed** in both Railway production and Vercel production before starting operations audit.
