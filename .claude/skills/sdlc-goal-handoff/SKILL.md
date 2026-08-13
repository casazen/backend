---
name: sdlc-goal-handoff
description: >-
  On delivery loop completion (goal_done / queue_empty / max_work_units),
  generate a graphical HTML demo for FE/BE features or a markdown report for
  gaps/other work, then POST via sdlc-notify-human. Least-impact goal webhook.
---

# sdlc-goal-handoff

Run **only** when the delivery loop is about to set `status: completed` for
`goal_done`, `queue_empty`, or `max_work_units`. Do **not** run on `pr_merged`,
`escalated`, `blocked`, or `merge_wait`.

## Steps

1. Generate + notify in one command:

```powershell
.\scripts\quality\build-goal-handoff.ps1 -Event goal_done -Notify
```

Use `-Event queue_empty` or `-Event max_work_units` to match the stop reason.
Add `-DryRun` when `NOTIFY_WEBHOOK_URL` is unset (still writes artifacts).

2. Script writes (gitignored under `Sessions/loop/`):

| File | When |
|---|---|
| `goal-handoff.md` | always |
| `goal-handoff.html` | `kind=demo` only — open in a browser (Mermaid via CDN) |
| `last-handoff.json` | always |

3. Classification (script-owned, do not override):

| kind | When |
|---|---|
| `demo` | Journal has `feature_stage`/`feature` **and** design/spec has FE routes or BE endpoints |
| `report` | Gaps only, or no FE/BE surface |

4. Then `sdlc-notify-human` fires with extra fields `artifact_kind`, `artifact_path`, `html_path`. Notify failure is **not** a gate FAIL.

5. Record `last_notify_event` on `delivery-state.md`. Notes may mention `Sessions/loop/goal-handoff.html` so the human can open the demo locally.

## Honesty

- Copy must say the work is on **develop** / test — not production — unless Stage 05 actually promoted.
- Do not invent screenshots or Playwright recordings. The HTML flow is generated from design/spec.
- Do not treat missing webhook as a failed tick.

## Forbidden

- Running this mid-pipeline (sticky still advancing).
- Asking the human to merge.
- Embedding secrets in the HTML/markdown.
