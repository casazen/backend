# Stage 05: Release — Quality Harness

## Entry Criteria

- PR `#P` is approved (≥ 1 approval, 0 requested-changes)
- All Stage 04 gates passed
- No critical findings pending

## Council Run

Coordinator spawns: `release-manager`, `qa-validator`

Topic handed to council:
> "Release PR #P. Validate CI pass, Docker build, and API health. If all gates pass, merge and tag version vX.Y.Z."

## Quality Gates

All gates must pass before merge.

| # | Gate | Command | Pass condition |
|---|---|---|---|
| G1 | All CI checks green | `gh pr checks #P` | All checks ✅, 0 failures |
| G2 | Docker build succeeds | `docker build -t casazen-api .` | Exit code 0 |
| G3 | API health endpoint responds | `curl -f https://localhost:5001/api/health` | HTTP 200 |
| G4 | Version tag is valid semver | Read planned tag | Matches `v[0-9]+\.[0-9]+\.[0-9]+` |
| G5 | Branch is up to date with main | `gh pr view #P --json mergeable` | `MERGEABLE` (not `BEHIND`) |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G5 fails) AND (iteration < max_iterations):
  1. Coordinator identifies failing gate
  2. G1 fails → qa-validator investigates CI logs; route fix back to Stage 03 team if code issue
  3. G2 fails → release-manager inspects Dockerfile; fix and re-push to PR branch
  4. G3 fails → qa-validator checks health endpoint config; fix and re-push
  5. G5 fails → release-manager rebases branch on main
  6. iteration++

IF iteration == max_iterations AND gates still failing:
  ESCALATE: add escalation block to PR description
  Human decision required — do NOT merge
```

## Merge Sequence (only after all gates pass)

```bash
# Only release-manager executes this
gh pr merge #P --squash --delete-branch
git tag vX.Y.Z
git push origin vX.Y.Z
gh release create vX.Y.Z --generate-notes
```

## Exit Artifact

- PR `#P` merged to `main` (squash merge, branch deleted)
- Git tag `vX.Y.Z` pushed to origin
- GitHub Release created with auto-generated changelog

## Handoff to Stage 06

Tag `vX.Y.Z` deployed. Notify operations team to begin post-deploy monitoring.
