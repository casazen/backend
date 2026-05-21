# Phase 5 — Cutover

## Goal

Validate the WebhookRunner against each of the 7 rules (shadow mode), then
disable the corresponding GitHub Actions jobs with `if: false`.
The trigger conditions stay identical — only the executor changes.

---

## Pre-Cutover Checklist

- [ ] All 7 rules validated in shadow mode (both systems running)
- [ ] At least 48h of shadow operation with zero local runner errors
- [ ] Startup script runs reliably (ngrok + WebhookRunner + webhook registration)
- [ ] WebhookRunner auto-starts on boot (see Phase 1 scheduled task)
- [ ] GitHub PAT expiry > 90 days from today

---

## Shadow Mode Validation

During shadow mode both systems fire on the same event — expect duplicate
comments/labels. Use this to compare output quality before disabling Actions.

| step-transitions.yml job | Matching webhook rule | Shadow validated |
|---|---|---|
| `trigger-step1-clarification` | `issues.labeled` = `raw-requirement` | [ ] |
| `handle-clarification-reply` | `issue_comment.created` + `awaiting-clarification` | [ ] |
| `trigger-council` | `issues.labeled` = `council-ready` | [ ] |
| `trigger-step2` | `issues.labeled` = `approved` | [ ] |
| `trigger-step3` | `issues.labeled` = `in-sprint` | [ ] |
| `trigger-step3-post-merge` | `pull_request.closed` merged=true | [ ] |
| `trigger-unblock-on-close` | `issues.closed` state_reason=completed | [ ] |

---

## Step 1 — Disable GitHub Actions Jobs

Once all 7 rows above are checked, edit `.github/workflows/step-transitions.yml`.
Add `if: false` to every job. The `on:` block and job bodies stay untouched —
only execution is suppressed. This preserves the file as documentation.

```yaml
jobs:
  trigger-step1-clarification:
    if: false   # disabled — replaced by local WebhookRunner
    name: "Step 1 — Clarification"
    runs-on: ubuntu-latest
    # ... rest unchanged ...

  handle-clarification-reply:
    if: false   # disabled — replaced by local WebhookRunner
    name: "Step 1 — Process PO Reply"
    runs-on: ubuntu-latest
    # ... rest unchanged ...

  trigger-council:
    if: false   # disabled — replaced by local WebhookRunner
    # ...

  trigger-step2:
    if: false   # disabled — replaced by local WebhookRunner
    # ...

  trigger-step3:
    if: false   # disabled — replaced by local WebhookRunner
    # ...

  trigger-step3-post-merge:
    if: false   # disabled — replaced by local WebhookRunner
    # ...

  trigger-unblock-on-close:
    if: false   # disabled — replaced by local WebhookRunner
    # ...
```

---

## Step 2 — Commit and Push

```bash
git add .github/workflows/step-transitions.yml
git commit -m "chore: disable GitHub Actions jobs — replaced by local WebhookRunner"
git push origin main
```

---

## Step 3 — Smoke Test (Post-Cutover)

Trigger each event type once and confirm:

1. GitHub Actions tab shows all jobs **skipped** (grey, not green/red)
2. WebhookRunner log shows job queued and completed
3. Expected GitHub side-effect (comment or label change) appears **exactly once**

Quick sequence:
```
1. Add label "raw-requirement" to a test issue
   → Actions: trigger-step1-clarification = skipped
   → Local: /step1-refine N runs, bot posts clarification questions

2. Reply to the bot comment (human author)
   → Actions: handle-clarification-reply = skipped
   → Local: /step1-refine N mode=read-answers runs

3. Add label "council-ready"
   → Actions: trigger-council = skipped
   → Local: /step1-refine N mode=council runs
```

---

## Step 4 — Monitor for 24h

```powershell
# Check session statuses
Invoke-RestMethod http://localhost:5050/sessions | ConvertTo-Json -Depth 3

# Count failures
(Invoke-RestMethod http://localhost:5050/sessions |
    Where-Object { $_.status -eq "failed" }).Count
```

---

## Phase 7 — Cleanup (after 72h stable)

### Remove SOVEREIGN_API_KEY from GitHub Secrets

```
GitHub repo → Settings → Secrets → Actions → delete SOVEREIGN_API_KEY
```

The key lives only in local config from this point.

### Clean up stale worktrees

```powershell
git worktree list
git worktree remove .claude/worktrees/agent-* --force
```

---

## Rollback Procedure (< 5 minutes)

```bash
# Remove if: false from each job in step-transitions.yml, then:
git add .github/workflows/step-transitions.yml
git commit -m "chore: re-enable GitHub Actions (rollback)"
git push origin main
```

GitHub stores 72h of webhook delivery history.
Re-deliver missed events from: **Settings → Webhooks → Recent Deliveries → Redeliver**.

---

## Troubleshooting

```
WebhookRunner not responding
├── Is the process running?     → Get-Process dotnet
├── Is port 5050 open?          → Test-NetConnection localhost 5050
└── Is ngrok running?           → Get-Process ngrok / http://localhost:4040

GitHub showing errors
├── 401 → webhook secret mismatch — restart startup script
├── 404 → ngrok URL changed — restart startup script
└── 5xx → check WebhookRunner logs

Job stuck running
├── GET /sessions → find job with status="running" for a long time
├── DELETE /jobs/{jobId} to kill it
└── Re-deliver the webhook from GitHub Settings

claude exits non-zero
├── Check .agent-sessions/<jobId>.jsonl for stderr lines
└── Run manually to reproduce:
    claude --print --verbose --output-format stream-json \
      --model qwen-3.5-122b-sovereign \
      --allowedTools "Bash,Read,Grep,Glob" \
      -p "/step1-refine 42"
```
