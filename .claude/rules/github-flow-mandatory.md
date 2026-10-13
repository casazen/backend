# GitHub Flow — Mandatory (All Agents)

**NEVER push or merge directly to `main` or `develop`.**

## Branch model

| Branch | Role | Who merges into it |
|---|---|---|
| `develop` | Integration / test deploys | Approved PR (`feature/*` → `develop`) |
| `main` | Production deploys | Release PR (`develop` → `main`) after human approval |

## Workflow (features)

1. `git checkout develop && git pull && git checkout -b feature/<name>`
2. Implement, commit (Conventional Commits)
3. `git push origin feature/<name>`
4. `gh pr create --base develop` — STOP, wait for approval
5. After merge to `develop`: Railway test + Vercel staging deploy automatically

## Workflow (release)

1. `gh pr create --base main --head develop` (release PR)
2. Human gates on test environment
3. Merge after explicit confirmation (`confirm release vX.Y.Z`) + CI pass
4. `git tag vX.Y.Z` on `main` for changelog only (does **not** trigger deploy)

## PR Body Requirements

- Summary: what changed and why
- Test Plan: how to verify
- `Closes #X`

## Agent Rules

- Open PRs to `develop`; never merge to `main` unless the user explicitly asks
- Never push directly to `main` or `develop`
- Required GitHub checks must be green before merge
