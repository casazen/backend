# Prompt template — delivery `Sessions/loop/next-prompt.md`

`sdlc-delivery-tick` overwrites this file each tick. Agents execute it as the work order.

```markdown
# Delivery tick <N> — <work_id>

## Kind
gap | feature_stage

## Objective
<one-line: close gap REQ-ID / advance pipeline slug to stage XX / review+merge PR>

## Context files
- @.claude/process/sdlc-delivery-loop/PROCESS.md
- @Sessions/loop/delivery-state.md
- @Sessions/loop/work-queue.md
- @Sessions/loop/goal.md
- <gap-backlog / requirements if gap>
- <Sessions/pipeline-<slug>/state.md if feature>
- <design / spec path if any>
- <PR URL(s) + Sessions/review-<N>.md when reviewing>

## Allowed actions
- Skills: sdlc-init, sdlc-stage-run, sdlc-contract-check, sdlc-gate-runner, sdlc-matrix-writeback, sdlc-notify-human
- Exactly one stage if kind=feature_stage (unless this tick is review+merge only)
- Feature branch `feature/<issue>-<slug>` (never push directly to develop/main)
- Open/update PRs to `develop` with `Refs #N` when mergeable after impl PASS
- Spawn fresh-context Stage 04 Task agents: code-reviewer + security-auditor
- Auto-merge to `develop` after Stage 04 gate-runner PASS + checks green (no --force)

## Gate list (must pass via sdlc-gate-runner)
- <gate-id>: <exact command>
- Stage 04 harness gates when reviewing

## PR / review / merge / notify
1. After impl PASS + mergeable diffs (or Stage 03 PASS): gh pr create/update → develop
2. Fresh-context Stage 04 review agents → Sessions/review-<N>.md
3. sdlc-gate-runner Stage 04 → on PASS: gh pr merge → develop (never main)
4. If checks pending: merge_wait + stop tick
5. sdlc-notify-human: pr_merged | review_failed | merge_wait | blocked | escalated | stage_pass

## Done when
1. Evidence at Sessions/loop/evidence/delivery-<N>/gates.json has overall pass|fail|blocked recorded honestly
2. delivery-state + journal + metrics updated
3. If review PASS and checks green: PR(s) merged to develop; notify pr_merged
4. If secrets/device missing: last_result=blocked, Notes document blocker — do not invent PASS

## Forbidden
- Narrative PASS without evidence/
- Asking a human to merge (HITL merge obsolete)
- status awaiting_human_pr_review
- Promoting develop → main / force-merge
- Closing GitHub issue before Stage 05 Phase B
```
