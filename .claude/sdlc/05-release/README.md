# Stage 05 — Release

**Pattern**: plan-execute-verify (multi-environment)
**Input**: Approved PR from Stage 04 (merged to `develop`)

## Purpose

Deploy to the **test environment** for human validation, coordinate a **Release Bundle** check (all related BE + FE features verified), then promote to **production**. This stage is the only place where code reaches end-users.

## Environments

| Environment | BE URL | FE URL | Trigger |
|---|---|---|---|
| Test | `$RAILWAY_TEST_URL` (GitHub var) | Vercel `develop` + PR previews | Merge to `develop` |
| Production | `$RAILWAY_PROD_URL` | `casazen.vercel.app` | Release PR merge to `main` |

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates 5-phase release, gates production promotion | `agents/coordinator.md` |
| release-manager | Merges release PR to `main`, creates tag, confirms prod deploy | `agents/release-manager.md` |
| qa-validator | Validates CI, test env health, smoke tests, prod health | `agents/qa-validator.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full gate specification.

**Phase A — CI validation**:
- All CI checks green (`gh pr checks`)
- Docker build succeeds
- PR approved and up to date

**Phase B — Test environment**:
- Railway test URL health check returns HTTP 200
- Vercel staging / preview URL is reachable
- Smoke tests pass on test environment

**Phase C — Human validation (HITL-test)**:
- Human tester verifies acceptance criteria on test environment
- Updates `Sessions/bundle-<epic>.md` row to `verified`

**Phase D — Bundle check**:
- All features in the Epic bundle deployed to test AND verified
- `Sessions/bundle-<epic>.md` shows all rows ✅

**Phase E — Production promotion**:
- Squash merge release PR `develop` → `main` (only `release-manager`)
- Railway production deploy triggered by push to `main`
- `git tag vX.Y.Z` + GitHub Release (version label only)
- Production health check passes
- Vercel production deploy from `main`

## Exit Artifact

- Release PR merged to `main` (squash)
- Git tag `vX.Y.Z` on `main` (changelog)
- GitHub Release with auto-generated changelog
- `Sessions/bundle-<epic>.md` status → `released`
- Railway production running new version
- Vercel production updated

## Infrastructure Reference

See [`docs/INFRA.md`](../../../docs/INFRA.md) for Supabase + Railway + Vercel setup.

## Chain

→ **Stage 06: Operations** — post-deploy monitoring begins
