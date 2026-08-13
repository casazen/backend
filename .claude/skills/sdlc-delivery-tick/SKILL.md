---
name: sdlc-delivery-tick
description: >-
  Execute exactly one SDLC delivery tick: queue → pick work-unit → gap fix or
  one feature stage → gate-runner → PR → Stage 04 fresh review → auto-merge
  develop → notify → update delivery-state.
---

# sdlc-delivery-tick

Read `.claude/process/sdlc-delivery-loop/PROCESS.md` first.

## Procedure

1. Ensure `Sessions/loop/delivery-state.md` exists (seed from STATE-FORMAT). Ensure `journal.md` / `metrics.md` exist (create empty templates if missing).
2. Read status:
   - `completed` | `escalated` → report and stop.
   - If obsolete `awaiting_human_pr_review` → rewrite to `running` (set `merge_wait: checks_pending` if PR still open); continue.
   - If `merge_wait: checks_pending` → re-check PR checks; merge when green; stop if still pending; fail/escalate if checks failed.
3. Read goal (`Sessions/loop/goal.md` if present). Cap: `max_work_units` (default 20). If `work_units_done >= max` → **sdlc-goal-handoff** `-Event max_work_units -Notify`, set `completed`, stop. Treat `stop_on: awaiting_human_pr_review` as `continue_next_item`.
4. Run **sdlc-work-queue**.
5. Pick top work-unit after goal include/exclude:
   - Sticky `feature_stage` always wins when present and not excluded.
   - Else first matching queue row.
   - Prefer `gap` over new `feature` when open P0 gaps exist, unless goal `include` forces a feature.
6. If nothing to pick → **sdlc-goal-handoff** `-Event goal_done` or `queue_empty -Notify`, set `completed`, stop.
7. Increment `tick`. Set `current_work_id`, `current_kind`, `sticky_pipeline` as applicable.
8. Overwrite `Sessions/loop/next-prompt.md` using delivery PROMPT-TEMPLATE (include concrete gate commands).
9. **Execute work-unit:**

### kind = gap

- Optionally `sdlc-spec-gap` if backlog stale.
- Implement fix on feature branch (create if needed).
- If environment cannot run required gates (device/secrets) → do not invent PASS; set `last_result: blocked`, document Notes, notify `blocked`, stop tick cleanly.

### kind = feature_stage

- `sdlc-init` if no pipeline state; else resume sticky slug.
- Run **exactly one** `sdlc-stage-run` for `current_stage` (unless this tick is only completing review+merge for an existing PR).
- Do not chain 03→04→05 in one invocation unless Stage 03 just opened a PR and the prompt explicitly allows continuing into Stage 04 review in the **same** tick (preferred: PR this tick, review+merge next tick — either is OK if evidence stays honest).

10. Run **sdlc-gate-runner** for implementation → `Sessions/loop/evidence/delivery-<tick>/`.
11. On overall pass:
    - Gap → **sdlc-matrix-writeback**
    - Feature → update pipeline Stage History Gates column with evidence path; advance `current_stage` only after PASS
12. **PR policy**
    - Open/update PR to `develop` when: gap fix has mergeable commits, **or** Stage 03 just PASSed.
    - Multi-repo: create/update BE/FE/mobile PRs as artifacts require; collect all URLs.
13. **Automated review + merge** (when PR URL(s) exist):
    - Spawn **fresh-context** Task/subagents for Stage 04 `code-reviewer` + `security-auditor` (see `.claude/sdlc/04-review/agents/`). Pass only: PR URLs, `gh pr diff` summary, design AC map path, evidence path, security checklist. Write `Sessions/review-<N>.md`.
    - Run Stage 04 harness gates via **sdlc-gate-runner**.
    - PASS + mergeable + required checks green → `gh pr merge` into `develop` (never `main`, never `--force`); clear `merge_wait`; notify `pr_merged`.
    - Checks pending → `merge_wait: checks_pending`; notify `merge_wait`; stop.
    - FAIL → do not merge; notify `review_failed`; increment `consecutive_fails_on_current`; at ≥ 3 → **sdlc-escalate** + notify `escalated`.
14. Update delivery-state, append journal row, refresh metrics (`work_units_done++` on terminal pass|fail|blocked; `prs_merged++` when merged).
15. Leave `status: running` for next cron (`stop_on: continue_next_item` default). Never set `awaiting_human_pr_review`.

## Sticky pipeline rules (feature path)

| After gate | Next tick behavior |
|---|---|
| Stage 01–02 PASS | sticky remains; next tick runs next stage |
| Stage 03 PASS | PR open → Stage 04 review + auto-merge (this tick or next) |
| Stage 04 PASS + merged | advance toward 05 only if goal/prompt allows; else continue queue |
| Stage 05–06 | only when prompt/goal explicitly allows; never promote on narrative |

### Sticky selection

1. If `delivery-state.sticky_pipeline` names a slug with `status: running` → that wins.
2. Else most recently updated `Sessions/pipeline-*/state.md` with `status: running`.
3. Set `delivery-state.sticky_pipeline` and `current_kind: feature_stage`.
4. **One stage only** for implementation — never chain 01→06 in the same Automation invocation.
5. If Stage History marks current stage `completed` but `current_stage` was not advanced, advance `current_stage` to the next pending stage before `sdlc-stage-run` (repair stale state).

### PR after Stage 03 → review → merge

1. Ensure `gh pr create` / update for each repo with mergeable commits (BE/FE/mobile).
2. Write URLs into pipeline Artifacts and `delivery-state.pr_urls`.
3. Fresh-context Stage 04 agents + gate-runner.
4. Auto-merge to `develop` when green; notify `pr_merged` (informational).

### Journal + metrics (every tick)

Append one row to `Sessions/loop/journal.md`:

`| <tick> | <ISO> | <work_id> | <kind> | <pass\|fail\|blocked> | <pr_url\|(none)> | <evidence> |`

Update `Sessions/loop/metrics.md`: `ticks_total`, `work_units_done`, `gaps_closed` / `stages_advanced`, `prs_opened`, `prs_merged`, `escalations`, `last_tick`, `last_work_id`, `last_result`.

## Automation constraints

- One work-unit per invocation (impl stage **or** review+merge completion).
- Auto-merge **only** to `develop` after Stage 04 evidence PASS + checks green.
- No `develop`→`main` unless prompt lists Phase B/C gates and gate-runner PASS.
- No narrative PASS; no fake device results.
- If `gh` auth or `NOTIFY_WEBHOOK_URL` missing: implement path, write Notes blockers, still record honest evidence.

## Forbidden

- Multiple implementation stages in one tick
- Human merge HITL / `awaiting_human_pr_review`
- Force-merge or merge to `main`
- Clearing P0 matrix rows without evidence
- Overwriting reliability `Sessions/loop/state.md` status to hide open gaps
