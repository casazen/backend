---
name: sdlc-stage-run
description: >-
  Execute exactly one SDLC stage (01-06): read coordinator + harness, run
  specialists, produce artifacts. Never declare gate PASS — that is sdlc-gate-runner.
---

# sdlc-stage-run

## Steps

1. Read `Sessions/pipeline-<slug>/state.md` → `current_stage`.
2. Read (mandatory):
   - `.claude/sdlc/<stage>/agents/coordinator.md`
   - `.claude/sdlc/<stage>/harness.md`
3. Build specialist prompt with coordinator instructions + input artifact + gate list.
4. Invoke specialists (Agent tool / Task) as coordinator directs. Stage 03 always includes backend-developer + frontend-developer (+ mobile-developer when design scopes mobile).
5. Produce the stage exit artifact (issue, design spec, PRs, review doc, release doc, ops report).
6. Update pipeline state **work fields only** (artifacts paths, PR numbers). Leave Gates column as `pending evidence` until `sdlc-gate-runner`.

## Forbidden

- Writing `✅` / `completed` for the stage based on self-report
- Merging to `develop`/`main` (Stage 05 release-manager only after gate-runner PASS)
- Closing GitHub issues (only Stage 05 Phase B after matrix ✅)
- Skipping `sdlc-contract-check` on Stage 03/04 boundaries when API/UI changed
