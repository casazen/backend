---
name: sdlc-notify-human
description: >-
  Notify human via NOTIFY_WEBHOOK_URL for delivery events (pr_merged,
  review_failed, merge_wait, escalate, goal done, blocked). Informational —
  not a request to merge. Uses scripts/quality/notify-human.ps1.
---

# sdlc-notify-human

## Steps

1. Build payload:

```json
{
  "event": "pr_merged | review_failed | merge_wait | escalated | goal_done | queue_empty | blocked | stage_pass | max_work_units | pr_opened",
  "work_id": "<id>",
  "pr_url": "<url or empty>",
  "title": "<short>",
  "summary": "<1-3 sentences; informational — do not ask human to merge>",
  "evidence_path": "<path or empty>",
  "action_required": false
}
```

2. Run:

```powershell
.\scripts\quality\notify-human.ps1 -Event <event> -WorkId <id> -PrUrl <url> -Title <title> -Summary <summary> -EvidencePath <path>
```

Add `-DryRun` when webhook secret is unset and you only need to validate payload shape (log to stdout + `Sessions/loop/last-notify.json`).

3. If `NOTIFY_WEBHOOK_URL` is missing:
   - Write payload to `Sessions/loop/last-notify.json`
   - Append blocker note to `delivery-state.md` Notes
   - Do **not** treat notify failure as gate FAIL
   - Exit script with code 2 (warned) — tick continues

4. Record `last_notify_event` on delivery-state.

## Event meanings

| Event | Meaning |
|---|---|
| `pr_opened` | PR created/updated (optional; merge still pending review) |
| `pr_merged` | Stage 04 PASS + auto-merged to `develop` |
| `review_failed` | Stage 04 / review gates failed; no merge |
| `merge_wait` | Review OK; CI checks still pending — next cron retries |
| `escalated` | Same work-unit failed × 3 |
| `blocked` | Device/secrets missing |
| `goal_done` / `queue_empty` / `max_work_units` | Loop stop conditions |
| `stage_pass` | Non-PR stage advanced |

## Forbidden

- Embedding secrets in git-tracked files
- Phrasing notify as “please merge this PR” (merge is automated)
- Skipping documentation when webhook is unavailable during Automation
