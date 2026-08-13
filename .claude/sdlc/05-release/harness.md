# Stage 05: Release — Quality Harness

## Entry Criteria

- Stage 04 complete — 0 open 🔴 critical findings; **G11 AC matrix PASS**
- Open feature PR(s) targeting `develop` (`pr_backend`, `pr_frontend`, optional `pr_mobile`)
- Design spec AC Test Map + issue ACs available for Phase B
- **Release freeze**: if `Sessions/quality/ac-matrix-mvp.md` has any P0 `fail`, promote only hotfixes that clear those fails (L3 on broken path required)

## Council Run

Coordinator spawns: `release-manager`, `qa-validator`

Topic handed to council:
> "Release Issue #N: merge to develop, validate L2 + L3 staging (feature ACs + Golden Journey), then promote develop → main. Close issue only after Phase B AC matrix ✅ and Phase D smoke."

---

## Phase A — Merge to develop

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | Backend feature PR CI green | `gh pr checks <P_be> --repo casazen/backend` | All ✅ (or N/A if no BE PR) |
| G2 | Frontend feature PR CI green | `gh pr checks <P_fe> --repo casazen/frontend` | All ✅ including **e2e-l2** (or N/A) |
| G2b | Mobile PR CI green | `gh pr checks` on casazen/mobile | All ✅ when mobile PR exists; else N/A |
| G3 | Backend merged to develop | `gh pr merge <P_be> --squash` | `state: MERGED` |
| G4 | Frontend merged to develop | `gh pr merge <P_fe> --squash` | `state: MERGED` |
| G4b | Mobile merged to develop | `gh pr merge` mobile | When applicable |

**Order**: G3 before G4 when both repos change.

---

## Phase B — Staging validation (develop environment)

Run after develop deploy completes (~90–120s post-merge).

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Railway test health | `curl -sf $RAILWAY_TEST_URL/api/health` | HTTP 200 |
| G6 | Auth smoke | `curl` on `/api/properties`, `/api/bookings`, `/api/users/me`, `/api/me/contexts` | 401 each (never 5xx) |
| G6b | API regression E2E | `E2E_STAGING=1 npm run test:e2e -- api-regression-smoke` | Authenticated: no 500 |
| G6c | Vercel deploy smoke | `E2E_DEPLOY_SMOKE=1 npm run test:e2e -- vercel-deploy-smoke` | `#root` + no API 500 on load |
| G6d | EF migrations applied | `.\scripts\migrate.ps1 -Target test` before Phase B; **`.\scripts\migrate.ps1 -Target prod` mandatory before Phase C** | Exit 0 |
| G7 | Backend tests (release candidate) | `dotnet test` (backend `develop`) | 0 failures (N/A if no BE changes) |
| G8 | L2 E2E full suite | `npm run test:e2e` (frontend `develop`) | All demo Playwright tests pass (N/A if no FE changes) |
| G9 | L3 Feature ACs on staging | `E2E_STAGING=1 npm run test:e2e -- --project=staging-gj` **and/or** feature `e2e/l3/*` mapped in AC Test Map against staging API | Every Issue UI AC PASS on real staging API (no mock of path under test) |
| G9b | Golden Journey web | `E2E_STAGING=1 npm run test:e2e -- golden-journey-web` | GJ steps required for this release PASS (full 1–12 when MVP exit; subset documented otherwise) |
| G9c | Mobile Maestro (when mobile released) | `maestro test e2e/` against staging/demo seed | M1–M7 PASS; N/A if no mobile changes |
| G9d | AC matrix gate | `sdlc-matrix-writeback` from Phase B evidence + `.\scripts\quality\check-spec-coverage.ps1` for issue REQ-IDs | No `fail` remaining for issue ACs; stubs only with `status:stub`; **write-back required** |
| G9e | Portfolio freeze | `.\scripts\quality\check-spec-coverage.ps1` when promoting unrelated work | Exit 0 **or** this release is an explicit P0-hotfix clearing freeze rows |
| G10 | Staging FE serves SPA | `curl -sf $STAGING_FE_URL` | HTTP 200 **and** `id="root"` — Vercel **develop** URL, not production |

`$RAILWAY_TEST_URL` from GitHub variable `RAILWAY_TEST_URL`.
`$STAGING_FE_URL` = Vercel staging URL for branch `develop` (see `docs/INFRA.md`).

**If G7, G8, G9, G9b, G9d, or G10 fails**: stop — do not promote to `main`. Route fix to Stage 03.

**Phase C precondition (non-negotiable)**: G7 **and** G8 **and** G9 **and** G9b **and** G9d **and** G9e **and** G10 must all pass in the same release run immediately before opening/merging `develop` → `main`. PASS only via **sdlc-gate-runner** evidence — not release markdown tables alone.

**Issue close**: only after Phase B AC matrix ✅ — then `gh issue close <N>` (or `Closes #N` on the release PR). Never close at Stage 03/04.

---

## Phase C — Promote develop → main

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G11 | Semver tag valid | Read planned tag | Matches `v[0-9]+\.[0-9]+\.[0-9]+` |
| G12 | Backend release PR merged | `develop` → `main` squash merge | Exit 0; `main` contains feature |
| G13 | Frontend release PR merged | `develop` → `main` squash merge | Exit 0 (N/A if FE-only tag on FE repo) |
| G14 | Tags pushed | `git push origin vX.Y.Z` per repo | Tag visible on origin |
| G15 | GitHub Releases created | `gh release create vX.Y.Z` | Release URL returned |

Release PRs may be created on the fly by release-manager if none exist:
```bash
gh pr create --base main --head develop --repo casazen/backend --title "release: vX.Y.Z"
```

---

## Phase D — Production validation (main environment)

Run **after** push to `main` deploys (~90–120s). Stage 06 must not start until Phase D passes.

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G16 | Railway prod health | `curl -sf $RAILWAY_PROD_URL/api/health` | HTTP 200 |
| G16b | Prod migration + smoke script | `.\scripts\release-smoke.ps1` (applies `migrate.ps1 -Target prod`, then health + auth gates + FE SPA) | Exit 0 |
| G17 | Vercel prod health | `curl -sf https://casazen-app.vercel.app` | HTTP 200 **and** body contains `id="root"` (do **not** use `casazen.vercel.app` — mislinked, #187) |
| G18 | Feature AC on production | `E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke` in `casazen/frontend` (authenticated prod FE + prod API; fails on 401/500) | Pass on prod URLs |
| G19 | Docker build (backend) | `docker build -t casazen-api .` | Exit 0 — validates prod artifact (N/A if no BE changes) |
| G20 | **main ↔ develop aligned** (both repos) | See commands below | Zero divergence; correct merge direction (see table) |
| G21 | **Build parity** on `main` + `develop` | `dotnet build` / `npm run build` on both tips | Exit 0 on both branches per repo released |

### G20 — Branch alignment check (mandatory before Stage 06)

Production deploys from **`main`**. If `main` and `develop` diverge, the next feature merged to `develop` will not match what is live in prod, and staging will lie about production state.

#### Merge direction (do not invert)

| Step | Direction | Purpose |
|---|---|---|
| Feature integration | `feature/*` → **`develop`** | Staging / test env |
| **Promote to production** | **`develop` → `main`** | Release PR squash merge + tag on `main` |
| Sync-back after promote | **`main` → `develop`** | Only when `main` is ahead with release commits **and** G21 build passes on `main` |
| Hotfix / build break on `main` | Fix on **`develop`** first, then **`develop` → `main`** | Never copy a broken `main` into `develop` |

**Wrong pattern (caused #189 TS6133 on Vercel):** merge `main` → `develop` while `main` contains a bad commit (e.g. unused `demoUser` in `use-auth.ts`) that never built on `develop`. Alignment without build parity poisons `develop`.

Run in **each** repo that was released (`casazen/backend`, `casazen/frontend`):

```bash
git fetch origin main develop

# Commits on main not in develop (must be 0 after release)
git rev-list --count origin/develop..origin/main

# Commits on develop not in main (must be 0 after release)
git rev-list --count origin/main..origin/develop
```

**Pass**: both commands return `0` for both repos.

**If G20 fails** (typical cause: `main` was updated via local merge or squash without syncing `develop`):

1. **If `main` builds and is release-only ahead:** merge `main` → `develop`:
   ```bash
   git checkout develop && git pull origin develop
   git merge origin/main -m "chore: sync develop with main after release vX.Y.Z (#N)"
   git push origin develop
   ```
2. **If `main` does not build** (or has bad commits not on `develop`): fix on `develop`, run G21, then promote **`develop` → `main`** (release PR or fast-forward merge), not the reverse.
3. Re-run G20 and G21 until both pass.
4. Record sync SHAs in `Sessions/release-<N>.md` under **Branch sync**.

**Do not mark Phase D / Stage 05 complete until G20 and G21 pass.**

### G21 — Release branch build parity (both repos)

After G20 alignment, verify **both** `main` and `develop` tips build (same code must not break prod or staging).

| Repo | Command | Pass |
|---|---|---|
| Backend | `git checkout origin/develop && dotnet build /warnaserror` then `git checkout origin/main && dotnet build /warnaserror` | Exit 0 on both |
| Frontend | `git checkout origin/develop && npm run build` then `git checkout origin/main && npm run build` | Exit 0 on both |

Use detached checkouts or local branches tracking `origin/develop` and `origin/main`. **Fail G21 if either branch fails** — fix on `develop`, re-run G20 sync, then `develop` → `main`.

---

## Harness Loop (fix until promote)

Each phase retries up to **`max_iterations = 3`**. On code-related failure, **route back to Stage 03**, apply fix on `feature/<issue-N>-<slug-fix>` → PR → merge to `develop`, then **re-run the failed phase from the top** (including re-wait for deploy ~90–120s).

```
release_iteration = 0
max_iterations = 3

# ── Phase A: merge feature PR(s) to develop ──
WHILE (G1–G4 fail) AND (release_iteration < max_iterations):
  IF CI/merge conflict → release-manager rebases or fixes; retry merge
  IF code failure from CI logs → spawn Stage 03 (backend/frontend developer) on fix branch
  release_iteration++

# ── Phase B: staging validation (develop deploy) ──
release_iteration = 0
WHILE (G5–G10 fail) AND (release_iteration < max_iterations):
  1. qa-validator captures failing gate + HTTP body / test output
  2. IF infra (503, deploy pending) → wait 60s, retry same gate
  3. IF code bug (5xx, AC fail, broken build) → Stage 03 fix:
     - backend-developer / frontend-developer patch on feature/<N>-release-fix-<i>
     - PR → develop, merge, wait deploy
  4. Re-run ALL Phase B gates (G5–G10)
  release_iteration++
IF still failing → ESCALATE; do NOT enter Phase C

# ── Phase C: promote develop → main ──
release_iteration = 0
WHILE (G11–G15 fail) AND (release_iteration < max_iterations):
  release-manager resolves release PR conflicts; retry merge + tag
  release_iteration++

# ── Phase D: production validation (main deploy) ──
release_iteration = 0
WHILE (G16–G21 fail) AND (release_iteration < max_iterations):
  IF deploy pending → wait 60s, retry
  IF G20 branch drift → sync per direction table; if main broken, fix develop then develop→main
  IF G21 build fail → fix on develop, G20, develop→main, re-run G21
  IF prod regression → Stage 03 hotfix → develop → re-run Phase B → Phase C → Phase D
  release_iteration++
IF still failing → ESCALATE + document rollback in Sessions/release-<N>.md
```

**Coordinator rule**: never call Phase C complete unless Phase B passed in the **same release run** immediately before.

---

## Exit Artifact

`Sessions/release-<issue-N>.md` containing:
- Develop merge SHAs (BE + FE)
- Staging validation results (Phase B)
- Release tag `vX.Y.Z`
- Main merge SHAs
- Production validation results (Phase D)
- **Branch sync** (G20): alignment proof for backend + frontend (`develop` SHA = `main` tip or documented merge-back)

Plus:
- Git tag(s) on `main`
- GitHub Release URL(s)
- Issue `#N` closed

## Handoff to Stage 06

**Precondition**: Phase D gates G16–G18, **G16b**, **G20**, and **G21** ✅ on **`main` / production**.

Pass to operations:
- Tag `vX.Y.Z`
- `$RAILWAY_PROD_URL`
- `https://casazen-app.vercel.app`
- Issue `#N` + acceptance criteria for regression spot-check
