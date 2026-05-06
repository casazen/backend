# Workflow: Step 3 — Implementation (In-Sprint Task → Merged PR)

**Implementer**: `@feature-developer`
**Reviewer**: `/code-review-local`
**Closure Coordinator**: `@scrum-master-casazen`

Invoked via: `/step3-implement <task_number> [task_number ...]`
Auto-triggered by: GitHub Actions on label `in-sprint`

---

## Label State Machine

```
in-sprint  ← input (task from Step 2)
  ↓ [Phase A — pre-flight checks]
<<<<<<< claude/keen-hawking-cf7aac
  │  dependencies met?
  ├─ NO  → stop, report blocked dependencies
=======
  │  label "blocked" present?
  ├─ YES → remove "in-sprint", post comment, STOP
  │  dependencies (Blocked by: #N) met?
  ├─ NO  → post comment, STOP
>>>>>>> main
  └─ YES →
      ↓ [Phase B — @feature-developer: branch + implement + PR]
      ↓ [Phase C — /code-review-local: max 3 iterations]
      PR ready for manual review
      ↓ [Phase D — human developer merges PR]
      merged  (label added on task issue)
      ↓ [Phase E — @scrum-master-casazen: Epic closure check]
      [all tasks merged?]
        YES → Epic closed + codebase_map.md updated
        NO  → continue
```

---

## Phase A — Pre-flight (per task)

For each task number provided:

```bash
# 1. Verify label in-sprint
<<<<<<< claude/keen-hawking-cf7aac
gh issue view $TASK_NUMBER --json labels --jq '.labels[].name' | grep -q "^in-sprint$" \
  || { echo "ERROR: issue #$TASK_NUMBER does not have label in-sprint"; exit 1; }

# 2. Read issue body to find dependencies
gh issue view $TASK_NUMBER --json body --jq '.body'

# 3. For each "Blocked by: casazen/<repo>#N" found in body, verify the issue is closed
gh issue view $DEP_NUMBER --repo casazen/backend --json state --jq '.state' | grep -q "CLOSED" \
  || { echo "BLOCKED: dependency #$DEP_NUMBER is not yet closed"; exit 1; }

# 4. Determine scope from labels
=======
# CRITICAL: NEVER add this label yourself. Only the human Scrum Master can move a task
# from sprint-candidate to in-sprint. If the label is missing, stop and report.
gh issue view $TASK_NUMBER --json labels --jq '.labels[].name' | grep -q "^in-sprint$" \
  || { echo "ERROR: issue #$TASK_NUMBER does not have label 'in-sprint'. Only the human Scrum Master can select tasks for the sprint. Add the label manually and re-run."; exit 1; }

# 2. Check for blocked label — task has unresolved open questions or explicit blocker
HAS_BLOCKED=$(gh issue view $TASK_NUMBER --json labels \
  --jq '[.labels[].name] | contains(["blocked"])')
if [ "$HAS_BLOCKED" = "true" ]; then
  gh issue comment $TASK_NUMBER \
    --body "⛔ **Step 3 aborted**: this task has label \`blocked\` and cannot be implemented yet.

Resolve the blocking condition first (see the task body or the parent Epic for open questions),
then remove the \`blocked\` label before re-adding \`in-sprint\`."
  gh issue edit $TASK_NUMBER --remove-label "in-sprint"
  echo "BLOCKED: task #$TASK_NUMBER has label 'blocked'. Implementation aborted."
  exit 1
fi

# 3. Read issue body to find dependencies
gh issue view $TASK_NUMBER --json body --jq '.body'

# 4. For each "Blocked by: casazen/<repo>#N" found in body, verify the issue is closed
gh issue view $DEP_NUMBER --repo casazen/backend --json state --jq '.state' | grep -q "CLOSED" \
  || { echo "BLOCKED: dependency #$DEP_NUMBER is not yet closed"; exit 1; }

# 5. Determine scope from labels
>>>>>>> main
SCOPE=$(gh issue view $TASK_NUMBER --json labels --jq '[.labels[].name] | map(select(. == "be" or . == "fe")) | .[0]')
# be → casazen/backend, fe → casazen/frontend
```

If any dependency is open: post a comment on the task issue and stop.

```bash
gh issue comment $TASK_NUMBER \
  --body "⏸ Blocked: dependency casazen/<repo>#$DEP_NUMBER is not yet merged. This task will resume automatically once the dependency is closed."
```

---

## Phase B — Implementation (`@feature-developer`)

Follow `.claude/agents/feature_developer.md` exactly. One agent instance per task.

```bash
# 1. Determine slug from issue title
SLUG=$(gh issue view $TASK_NUMBER --json title --jq '.title' | \
  tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | sed 's/--*/-/g' | cut -c1-40)

# 2. Branch
git checkout main && git pull
git checkout -b feature/task-$TASK_NUMBER-$SLUG

# 3. Implement + tests
# ... (follow issue body: What to Build + Definition of Done checklist)

# 4. Validate (BE)
dotnet test
dotnet format --verify-no-changes

# 5. Commit + push
git add <specific files>
git commit -m "feat(task-$TASK_NUMBER): <description>"
git push origin feature/task-$TASK_NUMBER-$SLUG

# 6. Open PR
gh pr create \
  --base main \
  --title "feat: <title>" \
  --body "## Summary
<what was built and why>

## Test Plan
- [ ] Unit tests pass
- [ ] Integration tests pass (if applicable)
- [ ] Swagger updated (if endpoint changed)

Closes #$TASK_NUMBER
Part of: casazen/backend#$EPIC_NUMBER

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

**CRITICAL**: never `git merge` to main directly. See `.claude/rules/github-flow-mandatory.md`.

---

## Phase C — Automated Review

After PR is open, run `/code-review-local`.

See `.claude/workflows/common/review-process.md` for full protocol (max 3 iterations).

```
Iteration 1:
  Run /code-review-local
  Fix 🔴 Critical + 🟡 High findings
  Push fixes, re-run review (delta only)

Iteration 2 (if needed):
  Review delta only
  Fix remaining 🔴 Critical findings

Iteration 3 (if needed):
  If still unresolved 🔴 Critical → produce escalation report, stop

On APPROVED:
  Post on task issue:
  "🔍 PR ready for manual review: <PR_URL>"
```

---

## Phase D — Human Merge

The developer reviews the PR manually and merges. No automation.

After merge is detected (PR state = `MERGED`):

```bash
# Add merged label on task issue
gh issue edit $TASK_NUMBER --add-label "merged"

# Close the task issue
gh issue close $TASK_NUMBER \
  --comment "Implemented in PR #$PR_NUMBER. Merged to main."
```

---

## Phase E — Epic Closure Check (`@scrum-master-casazen`)

```bash
# Find the Epic reference from task issue body ("Part of: casazen/backend#N")
EPIC_NUMBER=<extracted from issue body>

# Get all tasks that are part of this Epic
ALL_TASKS=$(gh issue list --repo casazen/backend \
  --search "Part of: casazen/backend#$EPIC_NUMBER" \
  --json number,state,labels)

# Check if all are closed (state=CLOSED) or labeled "merged"
OPEN_COUNT=$(echo $ALL_TASKS | jq '[.[] | select(.state == "OPEN")] | length')
```

If all tasks are closed:

```bash
# 1. Close the Epic with delivery summary
gh issue close $EPIC_NUMBER --comment "## Delivery Summary

All tasks completed and merged.

### Tasks delivered
$(echo $ALL_TASKS | jq -r '.[] | "- #\(.number)"')

### Changes
- [Summarize what was built from task titles]

### Codebase map updated
See commit: [commit hash after map update]"

# 2. Update codebase_map.md
# Edit .claude/context/codebase_map.md to reflect newly implemented features
# Mark affected features as COMPLIANT

# 3. Commit the map update
git checkout main && git pull
git add .claude/context/codebase_map.md
git commit -m "docs: update codebase_map after Epic #$EPIC_NUMBER delivery"
git push origin main
```

---

## Parallel Execution Strategy (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`)

When `/step3-implement` receives multiple task numbers (e.g., `42 43 44`):

```
Classify tasks by dependency:

  Group A — Independent BE tasks (no shared dependencies, different layers)
    → Start all in parallel via CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS

  Group B — FE tasks with "Blocked by: BE task"
    → Check if BE dependency is already merged
    → If YES: start in parallel with other independent FE tasks
    → If NO: wait for BE task group to complete first

  Group C — Tasks within same layer sharing DB state
    → Serialize: complete one before starting the next
```

Example for `/step3-implement 10 11 12 13`:
```
#10 [BE] DB migration          → start immediately (independent)
#11 [BE] Service layer         → start immediately (independent, different layer)
#12 [FE] API client types      → wait for #10 + #11 PRs merged
#13 [FE] UI component          → wait for #12 merged
```

---

## Notes

- Max 3 review iterations per PR (anti-loop guard from `common/review-process.md`)
- FE tasks require the dependent BE PR to be **merged** (not just approved) before starting
- `@scrum-master-casazen` handles Epic closure only — it does not implement code
- `codebase_map.md` commit goes directly to main (documentation-only, no feature code)
- If the Epic spans both repos, `@scrum-master-casazen` checks FE tasks on `casazen/frontend` too before closing the Epic
