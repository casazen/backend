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
  │  label "blocked" present?
  ├─ YES → remove "in-sprint", post comment, STOP
  │  dependencies (Blocked by: #N) met?
  ├─ NO  → post comment, STOP
  └─ YES →
      ↓ [Phase B — @feature-developer: branch + implement + PR]
      ↓ [Phase C — post comment, CI review runs automatically]
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

The PR triggers `claude-code-review.yml` automatically. No action needed here.

Post on task issue once PR is open:
```bash
gh issue comment $TASK_NUMBER \
  --body "🔍 PR ready for review: $PR_URL — automated review running via CI."
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
# 1. Extract Epic number from merged task body ("Part of: casazen/backend#N")
EPIC_NUMBER=$(gh issue view $TASK_NUMBER --repo casazen/backend --json body \
  --jq '.body' | grep -oP 'Part of: casazen/backend#\K[0-9]+')

if [ -z "$EPIC_NUMBER" ]; then
  echo "No Epic reference in task #$TASK_NUMBER — skipping closure check."
  exit 0
fi

# 2. Read all task numbers from the Epic's "Task Breakdown" comment
#    (written by @scrum-master-casazen in Step 2, Phase C — authoritative task list)
BREAKDOWN=$(gh issue view $EPIC_NUMBER --repo casazen/backend \
  --json comments \
  --jq '[.comments[] | select(.body | test("Task Breakdown"))] | last | .body')

if [ -z "$BREAKDOWN" ] || [ "$BREAKDOWN" = "null" ]; then
  echo "ERROR: Epic #$EPIC_NUMBER has no 'Task Breakdown' comment. Cannot determine task list — stop and investigate."
  exit 1
fi

BE_TASKS=$(echo "$BREAKDOWN" | grep -oP '(?<=\[[ x]\] #)[0-9]+')
FE_TASKS=$(echo "$BREAKDOWN" | grep -oP '(?<=casazen/frontend#)[0-9]+')

# 3. Check each task individually by issue number (no text-search dependency)
OPEN_COUNT=0

for n in $BE_TASKS; do
  STATE=$(gh issue view $n --repo casazen/backend --json state --jq '.state')
  [ "$STATE" = "OPEN" ] && OPEN_COUNT=$((OPEN_COUNT+1))
done

for n in $FE_TASKS; do
  STATE=$(gh issue view $n --repo casazen/frontend --json state --jq '.state')
  [ "$STATE" = "OPEN" ] && OPEN_COUNT=$((OPEN_COUNT+1))
done
```

If `OPEN_COUNT > 0`: stop, nothing to do.

If `OPEN_COUNT == 0`:

```bash
# Build delivered task list
ALL_DELIVERED=$(
  for n in $BE_TASKS; do echo "- casazen/backend#$n"; done
  for n in $FE_TASKS; do echo "- casazen/frontend#$n"; done
)

# 1. Close the Epic with delivery summary
gh issue close $EPIC_NUMBER --repo casazen/backend --comment "## Delivery Summary

All tasks completed and merged.

### Tasks delivered
$ALL_DELIVERED

### Changes
- [Summarize what was built from task titles]

### Documentation updated
- \`.claude/context/codebase_map.md\` — features marked COMPLIANT
- \`docs/\` — updated where applicable (see commit)"

# 2. Update codebase_map.md
# Edit .claude/context/codebase_map.md: mark delivered features as COMPLIANT

# 3. Update /docs where content changed
# - docs/TECHNICAL.md  → new endpoints, entities, or DB schema
# - docs/PROJECT.md    → feature list or roadmap status
# - docs/BUSINESS.md   → business rules or regulatory context
# Do NOT touch files where nothing relevant changed — no filler content

# 4. Commit all documentation updates in a single commit
git checkout main && git pull
git add .claude/context/codebase_map.md
git add docs/TECHNICAL.md docs/PROJECT.md docs/BUSINESS.md  # only if actually modified
git commit -m "docs: update codebase_map and /docs after Epic #$EPIC_NUMBER delivery"
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

- Code review handled by `claude-code-review.yml` on PR open — do NOT run `/code-review-local` in CI
- FE tasks require the dependent BE PR to be **merged** (not just approved) before starting
- `@scrum-master-casazen` handles Epic closure only — it does not implement code
- Documentation commits (Phase E step 4) go directly to main — documentation-only, no feature code
- Epic task lookup uses the "Task Breakdown" comment (not `gh issue list --search`) — this is the authoritative task list and avoids GitHub search inconsistency
- If the Epic spans both repos, `@scrum-master-casazen` checks both `casazen/backend` and `casazen/frontend` tasks before closing the Epic
- `docs/` update is mandatory in Phase E — skip only files where nothing changed, never skip the step entirely
