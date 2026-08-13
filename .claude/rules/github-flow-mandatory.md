# GitHub Flow — Mandatory (All Agents)

**NEVER push or merge directly to `main` or `develop`.**

## Branch model

| Branch | Role | Who merges into it |
|---|---|---|
| `develop` | Integration / test deploys | Dev agents via approved PR (`feature/*` → `develop`) |
| `main` | Production deploys | `release-manager` only via release PR (`develop` → `main`) |

## Workflow (features)

1. `git checkout develop && git pull && git checkout -b feature/<name>`
2. Implement, commit (Conventional Commits)
3. `git push origin feature/<name>`
4. `gh pr create --base develop` — STOP, wait for approval
5. After merge to `develop`: Railway test + Vercel staging deploy automatically

## Workflow (release — Stage 05 only)

1. `gh pr create --base main --head develop` (release PR)
2. Coordinator + human gates on test environment
3. Only `release-manager` merges after `confirm release vX.Y.Z`
4. `git tag vX.Y.Z` on `main` for changelog only (does **not** trigger deploy)

## PR Body Requirements

- Summary: what changed and why
- Test Plan: how to verify
- `Closes #X`

## Agent Rules

- **Dev agents** (feature_developer, architect, test_engineer): open PR to `develop`, never merge
- **release_manager**: only agent allowed to merge to `main` (release PR), after approval + CI pass
- **issue_planner**: always include GitHub Flow steps in implementation plans
- **scrum-master**: track PR status, escalate if agents bypass process

## Automated Review

PRs to `develop` are reviewed by Stage 04 agents (`code-reviewer` + `security-auditor`) and `sdlc-gate-runner`. Required GitHub checks must be green before auto-merge. Critical findings must be fixed before merge.
