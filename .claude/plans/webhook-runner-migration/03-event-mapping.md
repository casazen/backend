# Phase 3 — Event Mapping

> With the config-driven router from Phase 2, all event mapping lives in
> `casazen-webhook-config.json`. This file documents the mapping logic and the
> validation procedure — there is no additional code to write in this phase.

---

## What Changed from the Original Plan

In the original spec, `GitHubEventRouter` contained hard-coded switch statements
for every rule. After adopting the agentic-kanban pattern, the router is generic
and data-driven. All rules live in `casazen-webhook-config.json`.

**To add, modify, or remove a rule: edit the JSON file. No code change needed.**

---

## Rule Evaluation Order

Rules are evaluated top-to-bottom. The first match wins.
The router applies these filters for each rule:

1. `event` — must match `X-GitHub-Event` header
2. `action` — must match `body.action` (for issues/pull_request) or `created` (for issue_comment)
3. `label` — if set, must match the newly added label (`body.label.name`)
4. `requiredLabel` — if set, issue must currently carry this label
5. `authorType` — if set, comment author type must match (skips `Bot`)
6. `merged` — if set, `pull_request.merged` must match
7. `stateReason` — if set, `issue.state_reason` must match

---

## Context Variables Available per Event

These are the `{{PLACEHOLDER}}` keys available for use in the `prompt` and
`description` fields of each rule.

### `issues` events

| Variable | Source |
|---|---|
| `{{ISSUE_NUMBER}}` | `body.issue.number` |
| `{{ISSUE_TITLE}}` | `body.issue.title` |
| `{{ACTION}}` | `body.action` |
| `{{LABEL}}` | `body.label.name` (only for `labeled` action) |
| `{{STATE_REASON}}` | `body.issue.state_reason` (only for `closed` action) |
| `{{ISSUE_LABELS}}` | comma-separated list of all current labels on the issue |

### `issue_comment` events

| Variable | Source |
|---|---|
| `{{ISSUE_NUMBER}}` | `body.issue.number` |
| `{{AUTHOR_TYPE}}` | `body.comment.user.type` |
| `{{ISSUE_LABELS}}` | comma-separated list of all current labels on the issue |

### `pull_request` events

| Variable | Source |
|---|---|
| `{{PR_NUMBER}}` | `body.pull_request.number` |
| `{{MERGED}}` | `body.pull_request.merged` (string "True"/"False") |
| `{{TASK_NUMBER}}` | extracted from PR body via `(?i)(?<=Closes #)(\d+)` |
| `{{ISSUE_NUMBER}}` | alias for `{{TASK_NUMBER}}` (used for dedup key) |

---

## Complete Rule Reference (current config)

These 7 rules replicate all 7 jobs from `step-transitions.yml`.

```
Rule 1: issues.labeled(raw-requirement)
  Prompt: /step1-refine {{ISSUE_NUMBER}}
  Model:  default (qwen-3.5-122b-sovereign)
  Tools:  Bash, Read, Grep, Glob
  Teams:  off  |  Cache: off

Rule 2: issue_comment.created [human, awaiting-clarification label]
  Prompt: /step1-refine {{ISSUE_NUMBER}} mode=read-answers
  Model:  default
  Tools:  Bash, Read, Grep, Glob
  Teams:  off  |  Cache: off

Rule 3: issues.labeled(council-ready)
  Prompt: /step1-refine {{ISSUE_NUMBER}} mode=council
  Model:  default
  Tools:  Bash, Read, Grep, Glob
  Teams:  ON   |  Cache: off

Rule 4: issues.labeled(approved)
  Prompt: /step2-dispatch {{ISSUE_NUMBER}}
  Model:  default
  Tools:  Bash, Read, Grep, Glob
  Teams:  ON   |  Cache: off

Rule 5: issues.labeled(in-sprint)
  Prompt: /step3-implement {{ISSUE_NUMBER}}
  Model:  step3 (claude-haiku-4-5-20251001)
  Tools:  Bash, Read, Write, Edit, Grep, Glob
  Teams:  off  |  Cache: ON  |  SkipPermissions: true

Rule 6: pull_request.closed [merged=true, Closes #N in body]
  Prompt: Phase E post-merge for task #{{TASK_NUMBER}} (PR #{{PR_NUMBER}} merged to main)...
  Model:  phaseE (claude-haiku-4-5-20251001)
  Tools:  Bash, Read, Write, Edit, Grep, Glob
  Teams:  off  |  Cache: ON  |  SkipPermissions: true

Rule 7: issues.closed [state_reason=completed]
  Prompt: /step3-implement {{ISSUE_NUMBER}}
  Model:  default
  Tools:  Bash, Read, Write, Edit, Grep, Glob
  Teams:  ON   |  Cache: ON
```

---

## How to Add a New Rule

1. Open `casazen-webhook-config.json` at the repository root
2. Add a new entry to the `rules` array:

```json
{
  "event":         "issues",
  "action":        "labeled",
  "label":         "my-new-label",
  "prompt":        "/my-skill {{ISSUE_NUMBER}}",
  "model":         "default",
  "tools":         ["Bash", "Read", "Grep", "Glob"],
  "agentTeams":    false,
  "promptCaching": false,
  "description":   "My new automation"
}
```

3. Restart `Casazen.WebhookRunner` (config is loaded at startup)
4. No code changes required

---

## Validation Checklist — Phase 3

Use real GitHub webhook payloads from **Settings → Webhooks → Recent Deliveries**
and replay them with curl or Postman against `http://localhost:5050/webhook`.

Generate a valid HMAC signature for test payloads:

```powershell
$secret = "your-webhook-secret"
$payload = Get-Content "payload.json" -Raw -Encoding UTF8
$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($secret))
$hash = [System.Convert]::ToHexString($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload))).ToLower()
Write-Host "sha256=$hash"
```

### Test Matrix

| Rule | Event to send | Expected log |
|---|---|---|
| 1 | `issues.labeled` `raw-requirement` | `Queued … Step 1 — clarification` |
| 2 | `issue_comment.created` (human, label=`awaiting-clarification`) | `Queued … Step 1 — read PO answers` |
| 2 | `issue_comment.created` (bot) | `skipped — no matching rule` |
| 2 | `issue_comment.created` (human, no label) | `skipped — no matching rule` |
| 3 | `issues.labeled` `council-ready` | `Queued … Step 1 — council review` |
| 4 | `issues.labeled` `approved` | `Queued … Step 2 — task dispatcher` |
| 5 | `issues.labeled` `in-sprint` | `Queued … Step 3 — implementation` |
| 5 | `issues.labeled` `merged` | `skipped — no matching rule` |
| 6 | `pull_request.closed` merged=true, body=`Closes #42` | `Queued … Phase E for task #42` |
| 6 | `pull_request.closed` merged=false | `skipped — no matching rule` |
| 6 | `pull_request.closed` merged=true, no `Closes #` | `skipped — no matching rule` |
| 7 | `issues.closed` state_reason=`completed` | `Queued … Auto-unblock on issue close` |
| 7 | `issues.closed` state_reason=`not_planned` | `skipped — no matching rule` |

- [ ] All rows above pass
- [ ] JSONL session file created for each queued job in `.agent-sessions/`
- [ ] `GET /sessions` returns the queued sessions
- [ ] Two identical payloads for the same issue → only one job in queue
