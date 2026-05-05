---
description: Step 3 — implement one or more in-sprint tasks end-to-end (branch, code, tests, PR, automated review). Supports multiple task numbers for parallel execution via CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS. Requires label `in-sprint` on each task.
disable-model-invocation: true
allowed-tools: Bash Read Write Edit Grep Glob
---

Run the Step 3 implementation workflow.

Full instructions: @.claude/workflows/step3-implementation.md

Execute the following steps:

1. Parse arguments (supports multiple task numbers):
   ```bash
   TASK_NUMBERS="$@"
   # e.g. /step3-implement 42 43 44
   ```

2. Phase A — Pre-flight for each task:
   ```bash
   for TASK in $TASK_NUMBERS; do
     # Verify label in-sprint
     gh issue view $TASK --json labels --jq '.labels[].name' | grep -q "^in-sprint$" \
       || { echo "ERROR: #$TASK missing label 'in-sprint'"; exit 1; }

     # Check dependencies from issue body ("Blocked by: casazen/<repo>#N")
     # For each dependency found: verify its state is CLOSED
     # If any dependency is open: post blocked comment on task, remove from execution list
   done
   ```

3. Classify tasks for parallel vs serial execution:
   - Independent BE tasks (no shared DB state): run in parallel
   - FE tasks with `Blocked by: BE task` dependency: wait for BE task PR merge
   - Tasks within same layer sharing DB state: serialize

4. Phase B — `@feature-developer` per task (follows `.claude/agents/feature_developer.md`):
   ```bash
   SLUG=$(gh issue view $TASK --json title --jq '.title' | \
     tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | cut -c1-40)
   git checkout main && git pull
   git checkout -b feature/task-$TASK-$SLUG
   # implement per "What to Build" in issue body
   # write tests per "Definition of Done" checklist
   dotnet test && dotnet format --verify-no-changes  # BE only
   git add <specific files>
   git commit -m "feat(task-$TASK): <description>"
   git push origin feature/task-$TASK-$SLUG
   gh pr create --base main \
     --title "feat: <title>" \
     --body "## Summary\n...\n## Test Plan\n...\nCloses #$TASK\nPart of: casazen/backend#EPIC"
   ```

5. Phase C — Automated review after each PR opens:
   - Run `/code-review-local` (max 3 iterations, see `.claude/workflows/common/review-process.md`)
   - Fix 🔴 Critical + 🟡 High findings, push, re-review delta only
   - After 3 iterations with unresolved blockers: produce escalation report, stop
   - On APPROVED:
     ```bash
     gh issue comment $TASK --body "🔍 PR ready for manual review: $PR_URL"
     ```

6. Phase D — Human merge (no automation): developer reviews and merges the PR.

7. Phase E — After merge detected, `@scrum-master-casazen`:
   - Adds label `merged` to task issue and closes it
   - Checks if ALL tasks of the parent Epic are closed
   - If yes: closes Epic with delivery summary + updates `.claude/context/codebase_map.md` + commits
