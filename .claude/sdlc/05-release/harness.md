# Stage 05: Release — Quality Harness

## Entry Criteria

- Stage 04 complete — 0 open 🔴 critical findings
- Open feature PR(s) targeting `develop` (`pr_backend`, `pr_frontend`)
- Design spec + issue acceptance criteria available for Phase B validation

## Council Run

Coordinator spawns: `release-manager`, `qa-validator`

Topic handed to council:
> "Release Issue #N sequentially: merge feature PR(s) to develop, validate full BE+FE functionality on staging, then promote develop → main with tag vX.Y.Z. Run Phase D production checks before handoff to Stage 06."

---

## Phase A — Merge to develop

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | Backend feature PR CI green | `gh pr checks <P_be> --repo casazen/backend` | All ✅ (or N/A if no BE PR) |
| G2 | Frontend feature PR CI green | `gh pr checks <P_fe> --repo casazen/frontend` | All ✅ (or N/A if no FE PR) |
| G3 | Backend merged to develop | `gh pr merge <P_be> --squash` then verify closed | `state: MERGED` |
| G4 | Frontend merged to develop | `gh pr merge <P_fe> --squash` then verify closed | `state: MERGED` |

**Order**: G3 before G4 when both repos change.

---

## Phase B — Staging validation (develop environment)

Run after develop deploy completes (~90–120s post-merge).

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Railway test health | `curl -sf $RAILWAY_TEST_URL/api/health` | HTTP 200 |
| G6 | Auth smoke | `curl -sw "%{http_code}" $RAILWAY_TEST_URL/api/properties` | 401 (not 5xx) |
| G7 | Backend tests (release candidate) | `dotnet test` (in `casazen/backend` on `develop`) | All tests pass, 0 failures (N/A if no BE changes in release) |
| G8 | E2E tests (release candidate) | `npm run test:e2e` (in `casazen/frontend` on `develop`) | All Playwright tests pass, 0 failures (N/A if no FE changes in release) |
| G9 | Feature AC validated | Automated E2E + spot-check vs Issue `#N` ACs on staging URLs | All ACs pass on staging BE+FE |
| G10 | Staging FE serves SPA | `curl -sf $STAGING_FE_URL` | HTTP 200 **and** body contains `id="root"` (not a stray `.env` or placeholder file) |

`$RAILWAY_TEST_URL` from GitHub variable `RAILWAY_TEST_URL`.
`$STAGING_FE_URL` = Vercel staging URL for branch `develop` (see `docs/INFRA.md`).

**If G7, G8, or G9 fails**: stop — do not promote to `main`. Route fix to Stage 03.

**Phase C precondition (non-negotiable)**: G7 **and** G8 **and** G9 **and** G10 must all pass in the same release run immediately before opening/merging `develop` → `main`.

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
| G17 | Vercel prod health | `curl -sf https://casazen.vercel.app` | HTTP 200 **and** body contains `id="root"` |
| G18 | Feature AC on production | Re-run E2E against prod FE URL or critical AC spot-check | Pass on prod URLs |
| G19 | Docker build (backend) | `docker build -t casazen-api .` | Exit 0 — validates prod artifact (N/A if no BE changes) |

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
WHILE (G16–G19 fail) AND (release_iteration < max_iterations):
  IF deploy pending → wait 60s, retry
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

Plus:
- Git tag(s) on `main`
- GitHub Release URL(s)
- Issue `#N` closed

## Handoff to Stage 06

**Precondition**: Phase D gates G16–G18 ✅ on **`main` / production**.

Pass to operations:
- Tag `vX.Y.Z`
- `$RAILWAY_PROD_URL`
- `https://casazen.vercel.app`
- Issue `#N` + acceptance criteria for regression spot-check
