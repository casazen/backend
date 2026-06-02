---
name: sdlc-pipeline
description: Orchestrates the full 6-stage CasaZen SDLC pipeline end-to-end. Start at Stage 01 with a feature description; the pipeline advances automatically through all stages with two mandatory HITL pauses (PR approval, merge confirmation). Supports resume after interruption via pipeline state file.
---

# SDLC Pipeline Orchestrator

## Trigger

Invoke this skill when the user says any of:
- `/sdlc-pipeline "<feature description>"`
- "avvia pipeline", "start pipeline", "lancia pipeline"
- "resume pipeline" or "riprendi pipeline" (to continue from a paused state)

---

## Phase 0 — Initialize or Resume

### On new pipeline

1. Ask the user (if not already provided):
   - Feature description (required)
   - Type: `feat` | `fix` | `compliance` | `ota` (default: `feat`)
   - Priority: `critical` | `high` | `medium` | `low` (default: `medium`)

2. Generate a slug from the description (lowercase, hyphens, max 30 chars).

3. Check if `Sessions/pipeline-<slug>/state.md` already exists:
   - If yes: **ask the user** whether to resume or restart
   - If no: create `Sessions/pipeline-<slug>/` directory and write initial state file (see State File Format below)

4. Set `current_stage = 01-planning` and begin the Stage Loop.

### On resume

1. Read `Sessions/pipeline-<slug>/state.md`
2. Identify `current_stage` and `status`
3. If `status = paused-hitl1`: proceed to HITL Gate 1 check
4. If `status = paused-hitl2`: proceed to HITL Gate 2 check
5. If `status = running`: re-run the current stage (it was interrupted)
6. If `status = escalated` or `completed`: inform the user and stop

---

## Stage Loop

Execute stages in this sequence, passing artifacts forward:

```
01-planning  →  02-design  →  03-development  →  [HITL-1]
→  04-review  →  [HITL-2]  →  05-release  →  06-operations
```

For each stage, follow this procedure:

### Stage Execution Procedure

**Step 1 — Prepare context**

Read the following files (do NOT skip):
- `.claude/sdlc/<stage>/agents/coordinator.md` — coordinator instructions
- `.claude/sdlc/<stage>/harness.md` — quality gates to verify

**Step 2 — Build stage prompt**

Construct a prompt for the stage coordinator that includes:
- The coordinator's full instructions (from coordinator.md)
- The stage-specific input artifact (see Artifact Handoff table below)
- The list of quality gates from harness.md
- Instruction: "Run all specialists, produce the exit artifact, then report gate status."

**Step 3 — Invoke stage coordinator**

Use the Agent tool (subagent_type: `general-purpose`) with the constructed prompt. The agent has access to all tools: Bash, Read, Write, Edit, Grep, Glob.

**Step 4 — Verify quality gates**

After the agent completes, run the executable gate checks directly (do not rely solely on the agent's self-report):

| Stage | Primary executable checks |
|---|---|
| 01 | `gh issue view <N>` · `gh issue view <N> --json labels` |
| 02 | Check `Sessions/design-<N>.md` exists and has all required sections |
| 03 | `dotnet test` · `dotnet format --verify-no-changes` · `npm test` · `tsc -b --noEmit` · `npm run lint` · `npm run build` · `gh pr view --json number,url` |
| 04 | `gh pr view <P> --json reviews` (check approval) · `gh pr view <P> --json reviewDecision` |
| 05 | `gh pr checks <P>` · `git tag --list v*` · `gh release view` |
| 06 | Check `Sessions/ops-report-<date>.md` exists and non-empty |

**Step 5 — Gate outcome**

- If **all gates pass**: update state file (stage → `completed`), advance to next stage.
- If **any gate fails** and `iteration < 3`: increment iteration, re-run Step 2–4 with the list of failing gates as additional context.
- If **iteration == 3** and gates still failing: write `Sessions/pipeline-<slug>/escalation-<stage>.md` (list failing gates + iteration history), update state to `escalated`, stop and inform user.

**Step 6 — Update state file**

After each gate check (pass or fail), rewrite `Sessions/pipeline-<slug>/state.md` with current status.

---

## Artifact Handoff Table

| Stage | Input artifact | How to obtain | Output artifact |
|---|---|---|---|
| 01-planning | Raw feature description from user | User input | GitHub Issue `#N` (URL from `gh issue create`) |
| 02-design | Issue `#N` | State file `artifacts.issue` | `Sessions/design-<N>.md` |
| 03-development | `Sessions/design-<N>.md` + branch `feature/<N>-<slug>` | State file `artifacts.design_spec` | PR `#P` (URL from `gh pr create`) |
| 04-review | PR `#P` + `Sessions/design-<N>.md` | State file `artifacts.pr_number` | PR with ≥1 approval, all critical findings resolved |
| 05-release | PR `#P` (approved) + semver tag from user | State file `artifacts.pr_number` + HITL-2 input | Merged PR, Git tag, GitHub Release URL |
| 06-operations | Git tag `vX.Y.Z` | State file `artifacts.tag` | `Sessions/ops-report-<YYYY-MM-DD>.md` |

---

## HITL Gate 1 — After Stage 03 (PR Approval)

**Trigger**: Stage 03 completes (all G1–G12 gates pass, PR is open).

**Action**:
1. Update state: `status = paused-hitl1`
2. Display to user:
   ```
   ⏸  HITL Gate 1 — PR Review Required

   PR #<P> is open: <PR_URL>

   Please:
   1. Review the code on GitHub
   2. Approve the PR (or request changes — pipeline will resume after approval)

   When the PR has at least 1 approval, type:
     resume pipeline <slug>
   ```
3. **Stop**. Do not advance to Stage 04 until the user resumes.

**On resume**:
- Verify approval: `gh pr view <P> --json reviews` — confirm `state: "APPROVED"` present
- If not yet approved: inform user and stop again
- If approved: update state to `running`, continue to Stage 04

---

## HITL Gate 2 — Before Stage 05 Merge (Merge Confirmation)

**Trigger**: Stage 04 completes (all review gates pass, PR is approved).

**Action**:
1. Update state: `status = paused-hitl2`
2. Determine next semver version: `git tag --sort=-v:refname | head -1` → increment patch (or ask user for major/minor bump)
3. Display to user:
   ```
   ⏸  HITL Gate 2 — Merge Confirmation Required

   All review gates passed. Ready to:
   - Merge PR #<P> to main (squash merge, branch deleted)
   - Create Git tag vX.Y.Z
   - Create GitHub Release

   Proposed version: vX.Y.Z
   This action is irreversible.

   To proceed, type:
     confirm release vX.Y.Z
   To use a different version:
     confirm release v<your-version>
   ```
4. **Stop**. Do not merge until explicit user confirmation.

**On confirmation**:
- Extract the confirmed semver tag from user input
- Validate format: matches `v[0-9]+\.[0-9]+\.[0-9]+` — if not, ask again
- Update state: `artifacts.tag = vX.Y.Z`, `status = running`
- Continue to Stage 05 with `tag = vX.Y.Z` as confirmed input

---

## State File Format

Path: `Sessions/pipeline-<slug>/state.md`

```markdown
# Pipeline: <feature-title>

## Status
- status: running | paused-hitl1 | paused-hitl2 | completed | escalated
- current_stage: 01-planning
- started: <ISO-8601>
- last_updated: <ISO-8601>

## Input
- description: <raw feature description>
- type: feat | fix | compliance | ota
- priority: critical | high | medium | low

## Artifacts
- issue: (pending)
- branch: (pending)
- design_spec: (pending)
- pr_number: (pending)
- pr_url: (pending)
- tag: (pending)
- release_url: (pending)
- ops_report: (pending)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | (pending) | - | - | - |
| 02-design | (pending) | - | - | - |
| 03-development | (pending) | - | - | - |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |
```

Update the state file **after every gate check**. Replace `(pending)` with actual values as artifacts are produced.

---

## Escalation Protocol

When a stage fails after 3 iterations:

1. Write `Sessions/pipeline-<slug>/escalation-<stage>.md`:

```markdown
# Escalation: Stage <N> — <stage-name>

## Pipeline: <slug>
## Date: <ISO-8601>

## Failing Gates
| Gate | Status | Notes |
|---|---|---|
| G1 | ❌ | <reason> |
...

## Iteration History
### Iteration 1
<what was tried, what still failed>
### Iteration 2
...
### Iteration 3
...

## Recommended Action
<specific steps a human should take to unblock this gate>
```

2. Update state: `status = escalated`
3. Inform user:
   ```
   ❌ Pipeline escalated at Stage <N>

   Gate(s) could not be resolved after 3 iterations.
   See: Sessions/pipeline-<slug>/escalation-<stage>.md

   Fix the issue manually, then run:
     resume pipeline <slug>
   ```

---

## Pipeline Completion

When Stage 06 completes:

1. Update state: `status = completed`, all artifacts populated
2. Display summary:
   ```
   ✅ Pipeline Complete

   Feature: <description>
   Issue:   #<N>  <issue-url>
   PR:      #<P>  <pr-url> (merged)
   Release: <release-url>
   Tag:     vX.Y.Z
   Ops:     Sessions/ops-report-<date>.md

   Total stages: 6/6 completed
   HITL pauses: 2 (PR approval, merge confirmation)
   ```

---

## Non-Negotiable Rules

These rules apply throughout the pipeline and cannot be bypassed:

- **Never push directly to `main`** — all code via feature branch → PR → review → merge
- **Never merge without HITL-2 confirmation** — the merge step is irreversible
- **Never skip secrets check (G10)** — committed secrets cannot be undone
- **Never hardcode tourist tax amounts** — `TouristTaxRate` entity only
- **All `/api` endpoints require `[Authorize]`** unless explicitly justified as public
- **Stripe webhook signature must be verified** — no exceptions
- **EF Core migrations required** for every schema change — no raw SQL
- **No "Co-Authored-By: Claude"** in any commit message produced by any stage agent
