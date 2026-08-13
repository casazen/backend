---
name: sdlc-prompt-gen
description: >-
  Generate Sessions/loop/next-prompt.md from the top open gap (reliability) or
  from a delivery work-unit when invoked by sdlc-delivery-tick.
---

# sdlc-prompt-gen

## Mode: reliability (default)

1. Read `Sessions/quality/gap-backlog.md` row priority 1 with Status `open` (skip `blocked`).
2. Read matching entry in `Sessions/quality/requirements.json`.
3. Overwrite `Sessions/loop/next-prompt.md` using `.claude/process/sdlc-reliability-loop/PROMPT-TEMPLATE.md`.
4. Fill gate list with concrete commands, typically:
   - Structure: `.\scripts\quality\check-ac-matrix.ps1` / `check-spec-coverage.ps1`
   - L1/L2/L3 as implied by the gap (UI gaps must include L3 or Maestro)
   - Anti-stub: `.\scripts\quality\check-no-shipped-stubs.ps1` when touching shipped paths
5. Set `Sessions/loop/state.md` → `current_gap_id` to the chosen gap.

## Mode: delivery

When caller is `sdlc-delivery-tick` (or prompt says delivery):

1. Read top pick from `Sessions/loop/work-queue.md` (after goal filter) — not gap-backlog alone.
2. If `kind=gap` → same gate filling as reliability, but use `.claude/process/sdlc-delivery-loop/PROMPT-TEMPLATE.md` and set `delivery-state.md` → `current_work_id`.
3. If `kind=feature_stage` → fill Objective/Context from `Sessions/pipeline-<slug>/state.md` + stage harness; gate list from `.claude/sdlc/<stage>/harness.md`.
4. Always include PR/notify Done-when clauses from the delivery template.

## Forbidden

- Reusing a previous tick's prose without regenerating from the backlog/queue
- Omitting Done when / Forbidden sections
- Listing gates that cannot be executed
- Using reliability template for delivery ticks (wrong stop/PR semantics)
