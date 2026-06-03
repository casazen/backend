# Stage 05: Release — Quality Harness

## Entry Criteria

- PR `#P` is approved (≥ 1 approval, 0 requested-changes)
- All Stage 04 gates passed
- No critical findings pending
- Test environment URLs available (Railway test + Vercel preview)

## Council Run

Coordinator spawns: `release-manager`, `qa-validator`

Topic handed to council:
> "Release PR #P. Run all five phases: CI validation, test environment health, human validation gate, bundle check, then production promotion with tag vX.Y.Z."

---

## Phase A — CI Validation

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | All CI checks green | `gh pr checks #P` | All checks ✅, 0 failures |
| G2 | Docker build succeeds | `docker build -t casazen-api .` | Exit code 0 |
| G3 | Version tag is valid semver | Read planned tag | Matches `v[0-9]+\.[0-9]+\.[0-9]+` |
| G4 | Branch is up to date with main | `gh pr view #P --json mergeable` | `MERGEABLE` (not `BEHIND`) |

---

## Phase B — Test Environment Validation

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G5 | Railway test health | `curl -sf $RAILWAY_TEST_URL/api/health` | HTTP 200 |
| G6 | Smoke: auth endpoint | `curl -sw "%{http_code}" $RAILWAY_TEST_URL/api/properties` | Returns 401 (not 500) |
| G7 | Smoke: public search | `curl -sf $RAILWAY_TEST_URL/api/properties/search?city=Milano` | HTTP 200 |
| G8 | Vercel preview reachable | `curl -sf $VERCEL_PREVIEW_URL` | HTTP 200 |

`$RAILWAY_TEST_URL` is read from GitHub variable `RAILWAY_TEST_URL`.
`$VERCEL_PREVIEW_URL` is read from the PR comment posted by Vercel bot or `gh pr view #P --json comments`.

---

## Phase C — Human Validation (HITL-test)

**This is a mandatory HUMAN gate. The coordinator must STOP and wait.**

Coordinator action:
1. Display to human:
   ```
   ⏸  HITL-test — Validate feature on test environment

   BE test:  $RAILWAY_TEST_URL
   FE preview: $VERCEL_PREVIEW_URL

   Please verify ALL acceptance criteria from Issue #<N> on the test environment.
   When done, update Sessions/bundle-<epic>.md marking this feature as verified.

   Then type:  bundle-verified #<PR> epic #<EPIC>
   ```
2. Update `Sessions/bundle-<epic>.md`: set this feature's test status to `✅ deployed, ✅ verified`.
3. **Stop**. Do not proceed to Phase D until the human confirms.

---

## Phase D — Bundle Check

| # | Gate | Check | Pass condition |
|---|---|---|---|
| G9 | Bundle file exists | `ls Sessions/bundle-<epic>.md` | File exists |
| G10 | All bundle features deployed | Read `Sessions/bundle-<epic>.md` | All rows have `✅ deployed` |
| G11 | All bundle features verified | Read `Sessions/bundle-<epic>.md` | All rows have `✅ verified` |

If G10 or G11 fail: coordinator identifies which features are still pending, displays them to the human, and waits. **Do not advance to Phase E until the entire bundle is verified.**

---

## Phase E — Production Promotion

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G12 | Squash merge succeeds | `gh pr merge #P --squash --delete-branch` | Exit 0 |
| G13 | Tag created and pushed | `git tag vX.Y.Z && git push origin vX.Y.Z` | Tag visible on origin |
| G14 | GitHub Release created | `gh release create vX.Y.Z --generate-notes` | Release URL returned |
| G15 | Railway prod health | `curl -sf $RAILWAY_PROD_URL/api/health` (after ~60s) | HTTP 200 |
| G16 | Vercel prod health | `curl -sf https://casazen.vercel.app` | HTTP 200 |

---

## Harness Loop

```
iteration = 0
max_iterations = 3

Phase A loop:
WHILE (G1–G4 fail) AND (iteration < max_iterations):
  G1 fails → qa-validator reads CI logs; route back to Stage 03 if code issue
  G2 fails → release-manager fixes Dockerfile, re-pushes to PR branch
  G4 fails → release-manager rebases branch on develop
  iteration++

Phase B loop:
WHILE (G5–G8 fail) AND (iteration < max_iterations):
  G5/G6/G7 fail → qa-validator checks Railway test logs: startup error? DB connection? config?
  G8 fails → check Vercel dashboard for preview build error
  iteration++

Phase E loop (post-merge only):
WHILE (G15–G16 fail) AND (iteration < max_iterations):
  G15 fails → check Railway production deploy logs; rollback if needed
  G16 fails → check Vercel production build logs
  iteration++

IF any phase reaches max_iterations with gates still failing:
  ESCALATE: do NOT merge (Phase A/B) or document rollback needed (Phase E)
```

---

## Merge Sequence (only after all phases A–D pass)

Release PR `#P` must be `develop` → `main`.

```bash
# Only release-manager executes this block
gh pr merge #P --squash --delete-branch=false
git fetch origin main
git checkout main && git pull
git tag vX.Y.Z
git push origin vX.Y.Z
gh release create vX.Y.Z --generate-notes --title "Release vX.Y.Z"
# Push to main triggers Railway prod + Vercel prod; tag is changelog only
```

---

## Bundle File Update (end of Stage 05)

After Phase E completes, coordinator updates `Sessions/bundle-<epic>.md`:
- Set all feature rows to `✅ released`
- Set bundle `Status: released`
- Fill in production URLs and release tag

---

## Exit Artifact

- Release PR `#P` merged to `main` (squash; `develop` branch retained)
- Git tag `vX.Y.Z` pushed to origin
- GitHub Release created with auto-generated changelog
- `Sessions/bundle-<epic>.md` → status `released`
- Railway production serving new version
- Vercel production updated

## Handoff to Stage 06

Tag `vX.Y.Z` deployed to production. Notify Stage 06 operations team to begin post-deploy monitoring.
