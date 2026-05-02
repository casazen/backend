---
name: feature-implementation
description: Implement features from GitHub issues through to merged PR. Orchestrates issue analysis, implementation, code review (max 3 iterations), and merge. Auto-triggers compliance-feature skill if backlog is empty.
---

# Feature Implementation Workflow

Orchestrates the full pipeline: open issue → feature branch → PR → code review → merge.

## Agent Chain

```
@scrum-master-casazen (orchestrator)
  → reads + prioritizes open issues
  → creates implementation plan
  → hands off to @feature-developer

@feature-developer (implementer)
  → creates branch
  → writes code + tests
  → opens PR
  → invokes code-review-local skill

@release-manager (merger)
  → verifies CI + review status
  → squash merges + deletes branch
```

## Step 0 — Check backlog

```bash
gh issue list --state open --repo casazen/backend \
  --json number,title,labels \
  --jq '[.[] | select(all(.labels[]; .name != "epic"))]'
```

If no open issues → invoke the `compliance-feature` skill first to generate backlog, then resume here.

## Step 1 — Issue analysis (`@scrum-master-casazen`)

- Read all open issues (backend + frontend), exclude `epic` label
- Identify FE↔BE dependencies (API contract, DB migration)
- Order: `compliance deadline` > `priority:critical` > `priority:high` > effort

**Handoff artifact**: implementation plan passed to `@feature-developer`

## Step 2 — Plan

Produce before implementing:
- Execution order (BE-first → FE second)
- API contract: endpoints + request/response DTOs + error codes
- DB migrations needed
- Testing strategy: unit + integration
- External dependencies: Auth0, Stripe, OTA adapters

## Step 3 — Implementation (`@feature-developer`)

```bash
git checkout main && git pull
git checkout -b feature/<descriptive-name>

# implement + write tests
dotnet test
dotnet format

git add .
git commit -m "feat: <description>"
git push origin feature/<name>

gh pr create --base main \
  --title "feat: <title>" \
  --body "## Summary\n...\n## Test Plan\n- [x] Build passes\n- [x] Tests pass\n\nCloses #<N>"
```

**Critical**: NEVER merge to main directly. NEVER `git merge` locally.
See `.claude/rules/github-flow-mandatory.md`.

**Handoff**: After PR is open → invoke `code-review-local` skill.

## Step 4 — Review (max 3 iterations)

Protocol: `.claude/workflows/common/review-process.md`

1. Invoke `code-review-local` skill
2. Fix 🔴 Critical + 🟡 High findings
3. Push fixes, re-review **delta only** (not full codebase)
4. After 3 iterations with unresolved Critical/High → produce escalation report, stop

Severity guide:
| Level | Action |
|---|---|
| 🔴 Critical | Must fix before merge |
| 🟡 High | Should fix before merge |
| 🟢 Medium | Consider fixing |
| ⚪ Low | Optional |

**Handoff**: When approved → pass PR number to `@release-manager`

## Step 5 — Merge (`@release-manager`)

Conditions: CI passes + 🔴 Critical resolved + no merge conflicts.

```bash
gh pr merge <number> --squash --delete-branch
```

Close the issue automatically via `Closes #<N>` in PR body.

## Output

- PR merged to main, feature branch deleted, issue closed
- OR: escalation report if review fails after 3 iterations

## Full workflow spec

`.claude/workflows/feature-implementation.md`
