# Tick 3 — MATRIX:marketplace:L3

## Objective
Close SPEC:micro-marketplace-v0:AC-L3: L3 real API marketplace loop

## Context files
- @.claude/process/sdlc-reliability-loop/PROCESS.md
- @Sessions/quality/gap-backlog.md
- @Sessions/quality/requirements.json
- @Sessions/quality/ac-matrix-mvp.md
- @Sessions/specs/spec-micro-marketplace-v0.md

## Allowed actions
- Use skills: sdlc-init, sdlc-stage-run, sdlc-contract-check, sdlc-gate-runner, sdlc-matrix-writeback
- Replace `e2e/l3/marketplace-l3.spec.ts` shell with real API asserts (no page.route on path under test)
- Open/update PRs to `develop` with `Refs #N` only

## Gate list (must pass via sdlc-gate-runner)
- G-coverage: `.\scripts\quality\check-spec-coverage.ps1`
- G-anti-stub: `.\scripts\quality\check-no-shipped-stubs.ps1`
- G-l3-path: marketplace L3 spec must exist and be non-shell (assert real API)

## Done when
1. `Sessions/loop/evidence/3/gates.json` has `"overall": "pass"`
2. Matrix row L3 real API loop is `pass`
3. `sdlc-matrix-writeback` updated artifacts

## Forbidden
- Declaring PASS without evidence/
- Closing issue early / promote to main
- Counting L2 demo mocks as L3
