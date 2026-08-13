# SDLC Delivery Loop — Process (not a skill)

Long-running delivery orchestrator for CasaZen. Runs **alongside** the reliability loop (`.claude/process/sdlc-reliability-loop/`). Atomic skills under `.claude/skills/sdlc-*` implement steps; this document owns the delivery state machine.

**Triggers:** `/sdlc-delivery`, Cursor Automation cron, `resume delivery`.

**Does not replace** `/sdlc-loop` / `sdlc-loop-tick` — that remains the isolated quality-gap audit. When the top delivery work-unit is a P0 gap, this loop **invokes** reliability skills (`sdlc-spec-gap`, fix, `sdlc-gate-runner`, `sdlc-matrix-writeback`).

---

## One Automation run = one work-unit

A **work-unit** is atomic:

1. If a feature pipeline is `running` and incomplete → advance **exactly one** stage (`sdlc-stage-run` + `sdlc-gate-runner`).
2. Else pick the top item from the unified work queue (after optional goal filter):
   - `gap` → implement fix + gates + matrix write-back (+ PR if mergeable diffs exist)
   - `feature` → `sdlc-init` (if needed) then current stage only
3. When the unit produces mergeable code (typically Stage 03 PASS, or a gap fix with commits): open/update PR(s) → `develop`.
4. **Review + merge (automated, no human merge):**
   - Spawn **fresh-context** Stage 04 agents (`code-reviewer` + `security-auditor` per `.claude/sdlc/04-review/`) with only PR diff, design AC map, evidence path, security checklist — not full chat history.
   - Run Stage 04 gates via `sdlc-gate-runner`.
   - On review PASS + PR mergeable + required checks green: **`gh pr merge` into `develop`** (never `main`, never `--force`).
   - On checks still pending: set `merge_wait: checks_pending`, leave `running`, retry next cron tick.
   - On review FAIL: fix loop (max 3) then `sdlc-escalate` + notify; do not merge.
5. Notify human **informationally** (`pr_merged` / `review_failed` / `escalated` / `merge_wait`) — not “please merge”.

**Obsolete:** `awaiting_human_pr_review` and human merge HITL. If found in old state files, rewrite to `running` or `merge_wait` and continue.

---

## Tick procedure

1. Read this file + `Sessions/loop/delivery-state.md`.
2. If `status` is `completed` or `escalated` → stop and report.
3. If `merge_wait: checks_pending` → re-check PR with `gh pr view` / checks; if green continue merge path; if still pending → stop tick (retry next cron); if failed → treat as fail / escalate path.
4. If `work_units_done >= max_work_units` (from goal or default 20) → **sdlc-goal-handoff** (`-Event max_work_units -Notify`) then set `completed` (safety cap).
5. Run **sdlc-work-queue** → refresh `Sessions/loop/work-queue.md` (+ optional `work-queue.json`).
6. Apply `Sessions/loop/goal.md` filter if present; else treat mode as `until_empty`.
7. Pick top work-unit (sticky running pipeline wins).
8. If queue empty / goal satisfied → **sdlc-goal-handoff** (`-Event goal_done` or `queue_empty -Notify`) then set `completed`, stop. Demo HTML for FE/BE features; markdown report for gaps/other.
9. Increment `tick`; write `Sessions/loop/next-prompt.md` for this work-unit (delivery prompt template).
10. Execute the work-unit (gap fix or one feature stage).
11. Run **sdlc-gate-runner** → `Sessions/loop/evidence/delivery-<tick>/`.
12. On PASS: **sdlc-matrix-writeback** when the unit is a gap; update pipeline stage history from evidence when feature.
13. If mergeable code exists and implementation gates PASS (or Stage 03 just passed): `gh pr create` / update → base `develop`; record PR URL(s).
14. **Automated review + merge** (when PR URL(s) exist and this tick owns review/merge for that unit):
    - Fresh Task/subagents for Stage 04 specialists → `Sessions/review-<N>.md`
    - `sdlc-gate-runner` for Stage 04 harness gates
    - PASS → merge to `develop` → clear `merge_wait` → notify `pr_merged`
    - Checks pending → `merge_wait: checks_pending` → notify `merge_wait` → stop
    - FAIL → increment fails; at 3 → escalate + notify `review_failed` / `escalated`
15. Update `delivery-state.md`, append `journal.md`, refresh `metrics.md`.
16. Default after successful merge (or stage with no PR yet): leave `status: running` for next cron (`stop_on: continue_next_item`).

---

## Unified queue + goal

Queue artifact: `Sessions/loop/work-queue.md` (human) and optional `work-queue.json` (machine).

Merge order (highest first):

1. Sticky `Sessions/pipeline-<slug>/state.md` with `status: running`
2. P0 gaps from `gap-backlog.md` / `requirements.json` (Status `open`)
3. Features with spec status `planned` or `in-dev` + open GitHub issues (order per `Sessions/specs/README.md` / `PLANNING.md`)

**Goal** — optional `Sessions/loop/goal.md`:

```markdown
# Delivery goal
- mode: limited | until_empty
- include: [MATRIX:native-host:AC15, SPEC:native-host-app, #299]
- exclude: [...]
- max_work_units: 20
- stop_on: continue_next_item | escalate_only
```

- Missing goal → `until_empty` on full queue.
- Present → filter; when satisfied → `completed` + notify.
- Default `stop_on: continue_next_item` — after auto-merge, next cron picks the next queue item.
- `escalate_only` — same progression; stop only on escalate / completed / max_work_units.
- **Do not** use `awaiting_human_pr_review` (obsolete).
- Goal may reorder features; it must **not** bypass freeze for promote `develop`→`main`. With open P0 fails, prefer gaps before new features unless include forces a feature (still no promote).

---

## Context budget (long-running)

Each Automation run loads **only**:

- This PROCESS + `delivery-state.md` + `goal.md` + top of `work-queue.md`
- Sticky `Sessions/pipeline-<slug>/state.md` if any
- `next-prompt.md` / current stage design
- For review: PR URL(s), `gh pr diff` summary, design AC map, Stage 04 harness — not full chat history

Forbidden: replaying full chat history, reading every `Sessions/pipeline-*`, or every spec. Subagents do stage/review work; parent updates state, evidence, PR URLs, merge result.

---

## Stop criteria

| Condition | Status / field |
|---|---|
| Goal satisfied or unified queue empty | `completed` + `sdlc-goal-handoff` webhook (`demo` or `report`) |
| Same work-unit FAIL × 3 (impl or review) | `escalated` |
| CI/checks still pending on PR | `running` + `merge_wait: checks_pending` |
| `max_work_units` reached | `completed` + `sdlc-goal-handoff` (`max_work_units`) |

---

## Non-negotiable rules

Inherited from reliability PROCESS, plus:

1. **No narrative PASS** — only `sdlc-gate-runner` evidence.
2. **Auto-merge to `develop` only** after Stage 04 gate-runner PASS + mergeable + required checks green. Never `--force`. Never auto-merge to `main`.
3. **No inventing device/secret PASS** — Maestro/device/staging gaps stay `blocked`/`escalated` when hardware/secrets missing; document in delivery-state Notes.
4. **P0 freeze** — feature PRs merge to `develop` allowed; promote to `main` blocked while P0 `fail` (see `Sessions/quality/freeze-policy.md`).
5. **Never push directly to `main` or `develop`** — land via PR merge only.
6. **Multi-repo** — open/update/merge all PRs for the slug (BE/FE/mobile) after review; notify once with all URLs.
7. **No "Co-Authored-By: Claude"** in commits.
8. **Fresh-context review** — Stage 04 agents are separate Task/subagents; do not reuse the implementer’s chat transcript as their only context.

---

## Skill map

| Skill | Role |
|---|---|
| `sdlc-delivery` | Entrypoint `/sdlc-delivery` |
| `sdlc-delivery-tick` | Orchestrate one delivery tick |
| `sdlc-work-queue` | Build unified queue |
| `sdlc-notify-human` | Webhook notify (informational) |
| `sdlc-goal-handoff` | On loop complete: FE/BE demo HTML or gap report, then notify |
| `sdlc-spec-gap` | Refresh gaps (when unit is gap) |
| `sdlc-prompt-gen` | Reliability gap prompts; delivery uses delivery PROMPT-TEMPLATE |
| `sdlc-init` / `sdlc-stage-run` | Feature pipeline |
| `sdlc-gate-runner` | Sole PASS/FAIL authority |
| `sdlc-matrix-writeback` | Matrix from evidence |
| `sdlc-contract-check` | API/UI contract |
| `sdlc-escalate` | Max fails |

---

## Feature sticky path (detail)

```
sticky running? --yes--> sdlc-stage-run(current_stage) --> gate-runner
                              | PASS
                              v
                    stage==03? --yes--> gh pr create/update
                              |              |
                              |              v
                              |         next tick: Stage 04 fresh agents
                              |              --> gate-runner
                              |              --> PASS: gh pr merge → develop
                              |                   notify pr_merged; continue
                              |              --> checks pending: merge_wait
                              |              --> FAIL: fix / escalate
                              v
                    stage==04? --yes--> (same review+merge path if PR open)
                              |
                              v
                    advance current_stage; leave running for next cron
```

For **gap** units with a PR: same review → gate-runner → auto-merge path in the tick that owns the PR (or the next tick if checks pending).

Journal (`journal.md`) and metrics (`metrics.md`) update every tick regardless of kind.

## Relation to reliability loop

| Loop | State file | Scope |
|---|---|---|
| Reliability | `Sessions/loop/state.md` | P0 quality gaps only |
| Delivery | `Sessions/loop/delivery-state.md` | Gaps + features + PR + auto review/merge + notify |

Do not overwrite reliability `state.md` from delivery ticks except when intentionally running gap remediation (then update both: delivery journal + reliability gap fields as today).
