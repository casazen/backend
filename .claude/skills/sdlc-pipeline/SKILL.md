---
name: sdlc-pipeline
description: Orchestrates the full 6-stage CasaZen SDLC pipeline end-to-end. Stage 03 implements backend + frontend together. Stage 05 merges to develop, validates on staging, then promotes to main. Stage 06 audits production only. Supports resume via pipeline state file.
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
3. If `status = running`: re-run the current stage (it was interrupted)
4. If `status = escalated` or `completed`: inform the user and stop

---

## Stage Loop

Execute stages in this sequence, passing artifacts forward:

```
01-planning  →  02-design  →  03-development  →  04-review  →  05-release  →  06-operations
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
| 03 | `dotnet test` · `dotnet format --verify-no-changes` · `npm test` · `npm run test:e2e` (AC-driven specs) · `tsc` · `lint` · `build` · `gh pr view` for BE + FE PRs targeting `develop` |
| 04 | `Sessions/review-<N>.md` · `gh pr view` BE + FE PRs |
| 05 | Phase A–C: `develop`→`main` for prod · Phase D: prod health + **G20 aligned** + **G21 build on both tips** · sync-back `main`→`develop` only if `main` builds; else fix `develop` then `develop`→`main` |
| 06 | `Sessions/ops-report-<date>.md` · prod health on `$RAILWAY_PROD_URL` + `casazen.vercel.app` |

**Step 5 — Gate outcome**

- If **all gates pass**: update state file (stage → `completed`), advance to next stage.
- If **any gate fails** and `iteration < 3`: increment iteration, re-run Step 2–4 with failing gates as context. **Stage 05 Phase B/D**: route code failures to Stage 03 fix branch → merge develop → re-test (see `05-release/harness.md` fix loop). **After prod promotion, G20 requires `main` and `develop` tips aligned in both repos** — merge `main` back into `develop` if drift.
- If **iteration == 3** and gates still failing: write `Sessions/pipeline-<slug>/escalation-<stage>.md` (list failing gates + iteration history), update state to `escalated`, stop and inform user.

**Step 6 — Update state file**

After each gate check (pass or fail), rewrite `Sessions/pipeline-<slug>/state.md` with current status.

---

## Artifact Handoff Table

| Stage | Input artifact | How to obtain | Output artifact |
|---|---|---|---|
| 01-planning | Raw feature description from user | User input | GitHub Issue `#N` (URL from `gh issue create`) |
| 02-design | Issue `#N` | State file `artifacts.issue` | `Sessions/design-<N>.md` |
| 03-development | `Sessions/design-<N>.md` + branch `feature/<N>-<slug>` | State file `artifacts.design_spec` | Feature PR(s) → `develop` (`pr_backend`, `pr_frontend`) |
| 04-review | PR(s) + `Sessions/design-<N>.md` | State file PR numbers | `Sessions/review-<N>.md`; 0 critical findings |
| 05-release | Feature PR(s) + issue ACs | State file PR numbers | `Sessions/release-<N>.md`; develop merge → staging test → main + tag |
| 06-operations | Tag on `main`, prod deploy live | State file `artifacts.tag` | `Sessions/ops-report-<YYYY-MM-DD>.md` (production audit) |

---

## State File Format

Path: `Sessions/pipeline-<slug>/state.md`

```markdown
# Pipeline: <feature-title>

## Status
- status: running | completed | escalated
- current_stage: 01-planning
- started: <ISO-8601>
- last_updated: <ISO-8601>

## Input
- description: <raw feature description>
- type: feat | fix | compliance | ota
- priority: critical | high | medium | low

## Artifacts
- issue: (pending)
- branch: (pending) — same name in backend + frontend when both change
- design_spec: (pending)
- pr_backend: (pending)
- pr_backend_url: (pending)
- pr_frontend: (pending)
- pr_frontend_url: (pending)
- release_report: (pending)
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
   PRs:     BE #<P>  FE #<P>  (merged develop → main)
   Release: <release-url>
   Tag:     vX.Y.Z (on main)
   Ops:     Sessions/ops-report-<date>.md (production)

   Total stages: 6/6 completed
   ```

---

## Release Flow (Stage 05 — sequential, no skips)

```
Feature PR(s)  →  merge to develop  →  test on staging (develop deploy)
                                        ↓ (if AC pass)
                              develop → main  →  tag vX.Y.Z  →  prod smoke
                                        ↓
                              Stage 06 audit on main/production only
```

Stage 03 opens PRs; Stage 05 merges. Never merge feature branches directly to `main`.

---

## Non-Negotiable Rules

These rules apply throughout the pipeline and cannot be bypassed:

- **Run to completion** — once a pipeline is started or resumed, execute Stages 03→06 sequentially without pausing for user confirmation between stages. Only stop on escalation (3 failed gate iterations), missing secrets, or non-standard destructive actions. Deliver the final completion summary when Stage 06 finishes.
- **Stage 03 always runs backend-developer + frontend-developer** — both specialists spawn every time; document N/A if a layer has no changes
- **Never push directly to `main` or `develop`** — feature PRs → `develop`; release PR `develop` → `main` (Stage 05 Phase C)
- **Never promote to `main` before staging validation** — Stage 05 Phase B must pass on develop deploy, including **`dotnet test`**, **`npm run test:e2e`**, and staging FE serving the React SPA (`id="root"`)
- **Tests from acceptance criteria in Stage 03** — test-engineer adds Vitest + Playwright E2E for each Issue AC before PR; Stage 05 re-runs BE + E2E before main promotion
- **Mandatory deploy regression E2E (non-negotiable)** — every release must include:
  1. **Demo E2E** for new feature ACs (`npm run test:e2e` in CI)
  2. **Live API regression** (`E2E_STAGING=1 npm run test:e2e -- api-regression-smoke`) — authenticated calls to `/api/properties`, `/api/bookings`, `/api/users/me`, `/api/me/contexts` must **never** return 500
  3. **Vercel deploy smoke** (`E2E_DEPLOY_SMOKE=1 npm run test:e2e -- vercel-deploy-smoke`) — prod/preview FE must serve `id="root"` with no API 500 storm on load
  4. Run (3) and (4) on **Railway test** before `develop`→`main` and on **prod** after Phase D
- **EF migrations before promote** — if Stage 03 adds a migration: run `.\scripts\migrate.ps1 -Target test` before Phase B staging validation and `.\scripts\migrate.ps1 -Target prod` before Phase D (startup auto-migrate is a safety net, not a substitute for the release checklist)
- **Stage 06 runs on production (`main`) only** — not against develop/staging URLs
- **Stage 05 auto-increments patch semver** from latest tag on backend repo unless specified otherwise
- **Stage 05 Phase D G20/G21** — `main` and `develop` same tip **and** both build; promote only `develop`→`main`; never merge broken `main` into `develop`
- **Never skip secrets check (G10)** — committed secrets cannot be undone
- **Never hardcode tourist tax amounts** — `TouristTaxRate` entity only
- **All `/api` endpoints require `[Authorize]`** unless explicitly justified as public
- **Stripe webhook signature must be verified** — no exceptions
- **EF Core migrations required** for every schema change — no raw SQL
- **No "Co-Authored-By: Claude"** in any commit message produced by any stage agent
