# Workflow: Feature Implementation (Issue → PR)

**Orchestrator**: `@scrum_master_casazen`
**Implementer**: `@feature_developer`
**Reviewer**: `/code-review-local`
**Merger**: `@release_manager`

Invoked via: `/feature-implementation` skill

---

## Flow

```
Open Issues?
  NO  → auto-trigger /compliance-feature, then resume
  YES → Issue Analysis → Planning → Implementation → Review (max 3) → Merge
```

---

## Step 0: Prerequisites

```bash
gh issue list --state open --repo casazen/backend \
  --json number,title,labels --jq '[.[] | select(.labels[].name | test("epic") | not)]'
gh issue list --state open --repo casazen/frontend \
  --json number,title,labels --jq '[.[] | select(.labels[].name | test("epic") | not)]'
```

**If no issues exist** → run `/compliance-feature` to generate backlog, then resume here.

---

## Step 1: Issue Analysis

- Read all open issues (FE + BE), exclude `epic` label
- Identify FE↔BE dependencies (API contract, DB migration, infra)
- Order by priority: `compliance deadline` > `priority:critical` > `priority:high` > effort

---

## Step 2: Planning

Output a concrete plan:
- **Order**: BE first (API) → FE (UI)
- **API Contract**: endpoints, DTOs, error codes
- **DB Migrations**: schema changes, seed data
- **Testing strategy**: unit + integration
- **External deps**: Auth0, Stripe, SendGrid, OTA adapters

Hand off plan to `@feature_developer`.

---

## Step 3: Implementation (`@feature_developer`)

```bash
git checkout main && git pull
git checkout -b feature/<descriptive-name>
# implement, write tests
dotnet test
dotnet format
git add . && git commit -m "feat: <description>"
git push origin feature/<branch-name>
gh pr create --base main --head feature/<branch-name> \
  --title "feat: <title>" \
  --body "## Summary\n...\n## Test Plan\n...\nCloses #<N>"
```

**CRITICAL**: never `git merge` to main directly. See `.claude/rules/github-flow-mandatory.md`.

After PR is open → run `/code-review-local`.

---

## Step 4: Review (max 3 iterations)

See `.claude/workflows/common/review-process.md` for full protocol.

1. Run `/code-review-local`
2. Fix 🔴 Critical + 🟡 High findings
3. Push, re-run review (delta only)
4. After 3 iterations with unresolved blockers → produce escalation report, stop

---

## Step 5: Merge (`@release_manager`)

Conditions: CI passes + code review approved + no merge conflicts.

```bash
gh pr merge <number> --squash --delete-branch
```

---

## Notes

- Max 3 review iterations per PR (anti-loop)
- For full-stack features: coordinate BE + FE PRs via `@scrum_master_casazen`
- This workflow is invoked by the `/feature-implementation` skill
