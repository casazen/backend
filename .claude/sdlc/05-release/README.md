# Stage 05 — Release

**Pattern**: plan-execute-verify
**Input**: Approved PR from Stage 04

## Purpose

Validate that CI passes, Docker builds cleanly, and the deployed API is healthy before tagging a release. Only `release-manager` merges to main.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates release sequence, gates merge on CI + health | `agents/coordinator.md` |
| release-manager | Merges PR, creates version tag, deploys | `agents/release-manager.md` |
| qa-validator | Validates CI checks, Docker build, health endpoint, smoke tests | `agents/qa-validator.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates**:
- `gh pr checks` → all checks green
- `docker build -t casazen-api .` → exits 0
- `curl -f https://localhost:5001/api/health` → HTTP 200
- Version tag follows semver (`vMAJOR.MINOR.PATCH`)

## Exit Artifact

- Merged PR (squash merge, branch deleted)
- Git tag `vX.Y.Z` on main
- GitHub Release created with changelog

## Chain

→ **Stage 06: Operations** — monitor post-deploy compliance and health
