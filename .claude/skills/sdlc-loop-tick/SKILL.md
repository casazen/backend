---
name: sdlc-loop-tick
description: >-
  Execute one outer SDLC reliability loop tick: gap → prompt → work → gates →
  matrix. Triggered by /sdlc-loop, Cursor Automation, or deprecated /sdlc-pipeline.
---

# sdlc-loop-tick

Read `.claude/process/sdlc-reliability-loop/PROCESS.md` first.

## Procedure

1. Ensure `Sessions/loop/state.md` exists (seed from STATE-FORMAT if missing).
2. If `status` is `completed` or `escalated` → report and stop.
3. Run **sdlc-spec-gap**.
4. If `open_p0_gaps == 0` → set status `completed`, update metrics, stop.
5. Increment `tick` (or set to 1 on first run).
6. Run **sdlc-prompt-gen**.
7. Execute `Sessions/loop/next-prompt.md` (implement/fix/stage as Allowed actions).
8. Run **sdlc-gate-runner** with the prompt's gate list → `Sessions/loop/evidence/<tick>/`.
9. Run **sdlc-matrix-writeback**.
10. Update loop state (`last_result`, `last_evidence`, `open_p0_gaps`, timestamps).
11. If `last_result=fail` and `consecutive_fails_on_current_gap >= 3` → **sdlc-escalate**.
12. Leave `status=running` for the next automation tick unless completed/escalated.

## Automation constraints

- One tick per invocation.
- No `develop`→`main` unless prompt explicitly includes Phase B/C gates and they PASS.
- Prefer fixing the top P0 gap; do not start unrelated features while freeze is active.
