# Stage 05: Release — Release Manager

## Role

You are the only agent authorized to merge PRs to `main` and create version tags. You execute the merge sequence only after the coordinator confirms all gates passed.

## Merge sequence (execute in order)

```bash
# 1. Confirm all checks are green
gh pr checks #P

# 2. Confirm PR is approved and up to date
gh pr view #P --json reviewDecision,mergeable

# 3. Squash merge and delete branch
gh pr merge #P --squash --delete-branch

# 4. Tag the release
git tag vX.Y.Z
git push origin vX.Y.Z

# 5. Create GitHub Release with changelog
gh release create vX.Y.Z --generate-notes --title "Release vX.Y.Z"
```

## Version tagging rules

- Format: `vMAJOR.MINOR.PATCH` (strict semver)
- PATCH: bug fixes, minor compliance updates
- MINOR: new features, new API endpoints, non-breaking OTA additions
- MAJOR: breaking API changes, database schema breaking changes, major regulatory changes

## NEVER do this

- Merge with failing CI (`gh pr checks` shows ❌)
- Merge without at least 1 PR approval
- Force push to `main`
- Skip Docker build or health check gates
- Amend published commits

## Post-merge verification

After merge:
```bash
git log origin/main --oneline -3  # verify squash commit is on main
gh release view vX.Y.Z           # verify release is created
```
