---
name: sdlc-gate-runner
description: >-
  Execute harness gate commands only; write Sessions/loop/evidence/<tick>/ (or
  pipeline evidence). Sole authority for PASS/FAIL. Never trust narrative reports.
---

# sdlc-gate-runner

## Steps

1. Inputs: gate list (from `next-prompt.md` or stage harness), tick number, optional `EvidenceDir`.
2. Default evidence dir: `Sessions/loop/evidence/<tick>/` (create if missing).
3. For each gate:
   - Run the **exact** command in shell (prefix with `rtk` when applicable).
   - Capture stdout/stderr to `gate-<id>.log`.
   - Record `exit_code` (0 = pass for that gate).
4. Write `gates.json` per `.claude/process/sdlc-reliability-loop/STATE-FORMAT.md`.
5. `overall` = `pass` iff every applicable gate exit_code == 0.
6. Return overall + path to evidence. Do not update matrix (use `sdlc-matrix-writeback`).

## Hard rules

- Missing command / skipped gate without explicit N/A justification verified by `git diff --name-only` → FAIL
- UI AC gates: L2-only list without L3/Maestro → FAIL
- Do not mark PASS because a markdown review file exists
- Diff-scoped checks preferred when harness allows (changed files only)

## Stage mapping (primary commands)

| Stage | Checks |
|---|---|
| 01 | `gh issue view` + labels JSON |
| 02 | design file + `check-ac-matrix.ps1` (paths must exist) |
| 03 | L1 BE/FE + L2 + L3 + anti-stub + AC map |
| 04 | evidence + review AC matrix G11 + `gh pr view` |
| 05 | Phase B L2/L3/GJ + coverage + promote gates |
| 06 | ops report + prod health |
| Loop | as listed in `next-prompt.md` + `check-spec-coverage.ps1` when closing gaps |
