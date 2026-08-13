---
name: sdlc-delivery
description: >-
  Entrypoint for the CasaZen SDLC Delivery Loop (/sdlc-delivery, resume delivery).
  Invokes sdlc-delivery-tick for one work-unit (impl or review+auto-merge).
  Keeps reliability /sdlc-loop intact.
---

# sdlc-delivery

Canonical process: [`.claude/process/sdlc-delivery-loop/PROCESS.md`](../../process/sdlc-delivery-loop/PROCESS.md)

## When invoked

1. Read `PROCESS.md` and `Sessions/loop/delivery-state.md` (seed from STATE-FORMAT if missing).
2. Invoke **`sdlc-delivery-tick`** exactly once.
3. Do not run until empty in a single chat invocation — Automation cron resumes.

## Triggers

`/sdlc-delivery`, `resume delivery`, Cursor Automation with delivery-tick instructions.

## Skill map

| Skill | When |
|---|---|
| `sdlc-delivery-tick` | Default — one delivery work-unit |
| `sdlc-work-queue` | Refresh unified queue |
| `sdlc-notify-human` | After merge / review fail / escalate / goal done (informational) |
| Reliability skills | When work-unit kind is `gap` |
| Stage 04 agents | Fresh-context review before auto-merge |

Reliability loop (`/sdlc-loop`) remains for isolated P0 quality audits.

Merge to `develop` is automated after Stage 04 gate-runner PASS. Never pause for human merge.
