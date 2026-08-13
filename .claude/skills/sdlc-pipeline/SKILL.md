---
name: sdlc-pipeline
description: >-
  DEPRECATED entrypoint. Redirects to the SDLC reliability process and
  sdlc-loop-tick. Use /sdlc-loop or Cursor Automation. Kept for trigger
  compatibility (/sdlc-pipeline, avvia pipeline, resume pipeline).
---

# DEPRECATED — use the process + `sdlc-loop-tick`

`/sdlc-pipeline` is **no longer a monolithic skill**. Canonical process:

- [`.claude/process/sdlc-reliability-loop/PROCESS.md`](../../process/sdlc-reliability-loop/PROCESS.md)

## What to do when invoked

1. Read `PROCESS.md` and `Sessions/loop/state.md` (create via templates in `STATE-FORMAT.md` if missing).
2. Invoke skill **`sdlc-loop-tick`** for one outer-loop tick (or continue an in-flight feature pipeline via `sdlc-stage-run` + `sdlc-gate-runner` if `Sessions/pipeline-<slug>/state.md` is `running`).
3. Do **not** re-implement the old six-stage monolith in chat.

## Skill map

| Skill | When |
|---|---|
| `sdlc-loop-tick` | Default — one reliability tick |
| `sdlc-init` | New feature description / resume slug |
| `sdlc-stage-run` | Single stage work |
| `sdlc-gate-runner` | Executable gates only |
| `sdlc-spec-gap` / `sdlc-prompt-gen` | Gap refresh / next prompt |
| `sdlc-contract-check` | BE↔FE↔ADR compliance |
| `sdlc-matrix-writeback` | Matrix update from evidence |
| `sdlc-escalate` | Max fails on same gap |

Non-negotiable rules live in `PROCESS.md`, not here.
