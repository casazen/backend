---
description: Step 2 — decompose an approved Epic or Feature into atomic cross-repo tasks and create GitHub issues on casazen/backend and casazen/frontend. Requires label `approved`. Planning only — no code produced.
disable-model-invocation: true
allowed-tools: Bash Read Grep Glob
---

Run the Step 2 task-dispatcher workflow.

Full instructions: @.claude/workflows/step2-dispatcher.md

Execute the following steps:

1. Parse argument:
   ```bash
   ISSUE_NUMBER=$1
   ```

2. Verify label `approved` is present:
   ```bash
   gh issue view $ISSUE_NUMBER --json labels \
     --jq '.labels[].name' | grep -q "^approved$" \
     || { echo "ERROR: issue #$ISSUE_NUMBER does not have label 'approved'"; exit 1; }
   ```

3. Phase A — `@analyzer-agent` reads the Epic/Feature body:
   - Maps which of the 9 canonical layers are touched
   - Produces a dependency map (canonical order: DB → Domain → Repos/Services → API → Swagger → FE API service → FE State → FE UI → E2E)

4. Phase B — `@feature-developer` (PLANNING MODE ONLY — no file edits, no git operations):
   - Decomposes into atomic tasks following the dependency map
   - Each task: max 1–2 days, never BE+FE mixed
   - BE task titles: `[BE] <verb> <noun>`, FE task titles: `[FE] <verb> <noun>`
   - Each task includes: What to Build, API Contract (if applicable), Definition of Done, Dependencies
   - Max 12 tasks — flag for Epic splitting if exceeded

5. Phase C — `@scrum-master-casazen` creates GitHub issues:
   ```bash
   # BE tasks
   gh issue create --repo casazen/backend \
     --title "[BE] <task>" \
     --label "task,sprint-candidate,be,effort:<XS|S|M>"

   # FE tasks
   gh issue create --repo casazen/frontend \
     --title "[FE] <task>" \
     --label "task,sprint-candidate,fe,effort:<XS|S|M>"
   ```
   - Adds bidirectional dependency cross-links between related issues
   - Posts summary comment on Epic with complete cross-linked task checklist
