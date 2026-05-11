# GitHub Flow — Mandatory (All Agents)

**NEVER push or merge directly to `main`.**

## Workflow
1. `git checkout -b feature/<name>` from main
2. Implement, commit (Conventional Commits)
3. `git push origin feature/<name>`
4. `gh pr create --base main` — STOP, wait for approval
5. Only `release_manager` merges after CI + review pass: `gh pr merge <n> --squash --delete-branch`

## PR Body Requirements
- Summary: what changed and why
- Test Plan: how to verify
- `Closes #X`

## Agent Rules
- **Dev agents** (feature_developer, architect, test_engineer): open PR, never merge
- **release_manager**: only agent allowed to merge, after approval + CI pass
- **issue_planner**: always include GitHub Flow steps in implementation plans
- **scrum-master**: track PR status, escalate if agents bypass process

## Automated Review
All PRs trigger `claude-code-review.yml`. Critical issues must be fixed before merge.
