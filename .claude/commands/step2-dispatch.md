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

2b. Pre-flight: ensure pipeline labels exist on **both** repos and verify `casazen/frontend` is accessible:
   ```bash
   # Verify frontend repo access — abort loudly if not accessible
   gh repo view casazen/frontend --json name \
     || { echo "ERROR: cannot access casazen/frontend. Check GITHUB_TOKEN scope."; exit 1; }

   # Create pipeline labels on casazen/frontend (idempotent — 2>/dev/null suppresses "already exists" errors only)
   for args in \
     "task --color e99695 --description 'Atomic implementation task'" \
     "sprint-candidate --color bfd4f2 --description 'Task available for sprint selection'" \
     "fe --color e4b429 --description 'Frontend scope'" \
     "in-sprint --color 0e8a16 --description 'Task selected for current sprint'" \
     "merged --color 6f42c1 --description 'Task PR has been merged'" \
     "effort:XS --color c5def5 --description '< 4 hours'" \
     "effort:S --color c5def5 --description '0.5-1 day'" \
     "effort:M --color c5def5 --description '1-2 days'"; do
     eval "gh label create $args --repo casazen/frontend 2>/dev/null || true"
   done
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
   # BE tasks — always on casazen/backend
   gh issue create --repo casazen/backend \
     --title "[BE] <task>" \
     --label "task,sprint-candidate,be,effort:<XS|S|M>" \
     --body-file <temp_file>

   # FE tasks — always on casazen/frontend (NEVER on casazen/backend as fallback)
   gh issue create --repo casazen/frontend \
     --title "[FE] <task>" \
     --label "task,sprint-candidate,fe,effort:<XS|S|M>" \
     --body-file <temp_file>
   ```
   - Write each issue body to a temp file first (`--body-file`) to avoid bash escaping issues with backticks and code blocks
   - FE issues on `casazen/frontend` are NOT optional — if creation fails, abort and report the error; do NOT create FE issues on `casazen/backend`
   - Adds bidirectional dependency cross-links between related issues
   - Posts summary comment on Epic with complete cross-linked task checklist (FE issues referenced as `casazen/frontend#N`)
