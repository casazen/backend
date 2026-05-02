---
name: feature-implementation
description: Implement features from GitHub issues through to merged PR. Orchestrates issue analysis, implementation, code review (max 3 iterations), and merge. Auto-triggers /compliance-feature if backlog is empty.
invocable: true
---

# Feature Implementation Workflow

## Prerequisites

```bash
# Check open issues (exclude epics)
gh issue list --state open --repo casazen/backend \
  --json number,title,labels --jq '[.[] | select(.labels[].name | test("epic") | not)]'
```

**If no issues exist** → run `/compliance-feature` first, then resume here.

## Agents

| Agent | Role |
|---|---|
| `@scrum_master_casazen` | Orchestration, cross-repo coordination |
| `@feature_developer` | Branch + implementation + PR creation |
| `/code-review-local` | Code review (max 3 iterations) |
| `@release_manager` | Merge (squash + delete branch) |

## Execution Steps

**1. Issue Analysis**: read all open FE + BE issues (exclude epics), identify FE↔BE dependencies, order by: compliance deadline > priority:critical > priority:high > effort.

**2. Plan**: BE-first order, API contract (endpoints + DTOs), DB migrations, testing strategy, external dependencies (Auth0, Stripe, OTA).

**3. Implement** (`@feature_developer`):
```bash
git checkout main && git pull
git checkout -b feature/<name>
# implement + write tests
dotnet test && dotnet format
git add . && git commit -m "feat: <description>"
git push origin feature/<name>
gh pr create --base main --title "feat: <title>" --body "## Summary\n...\n## Test Plan\n...\nCloses #<N>"
```

**4. Review** (see `.claude/workflows/common/review-process.md`):
- Run `/code-review-local`
- Fix 🔴 Critical + 🟡 High findings
- Re-review delta only (max 3 iterations)
- If 3 iterations fail → escalation report, stop

**5. Merge** (`@release_manager`):
```bash
gh pr merge <number> --squash --delete-branch
```
Conditions: CI passes + 🔴 Critical resolved + no conflicts.

## Output

- PR merged to main + issue closed
- OR: escalation report if review blocked after 3 iterations

## Full Workflow Spec

`.claude/workflows/feature-implementation.md`
