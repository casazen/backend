---
name: sdlc-prompt-gen
description: >-
  Generate Sessions/loop/next-prompt.md from the top open gap. Use after
  sdlc-spec-gap on each reliability loop tick.
---

# sdlc-prompt-gen

## Steps

1. Read `Sessions/quality/gap-backlog.md` row priority 1 with Status `open` (skip `blocked`).
2. Read matching entry in `Sessions/quality/requirements.json`.
3. Overwrite `Sessions/loop/next-prompt.md` using `.claude/process/sdlc-reliability-loop/PROMPT-TEMPLATE.md`.
4. Fill gate list with concrete commands, typically:
   - Structure: `.\scripts\quality\check-ac-matrix.ps1` / `check-spec-coverage.ps1`
   - L1/L2/L3 as implied by the gap (UI gaps must include L3 or Maestro)
   - Anti-stub: `.\scripts\quality\check-no-shipped-stubs.ps1` when touching shipped paths
5. Set `Sessions/loop/state.md` → `current_gap_id` to the chosen gap.

## Forbidden

- Reusing a previous tick's prose without regenerating from the backlog
- Omitting Done when / Forbidden sections
- Listing gates that cannot be executed
