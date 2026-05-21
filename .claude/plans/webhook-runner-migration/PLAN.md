# Webhook Runner Migration Plan

## Objective

Replace GitHub Actions LLM execution with a local webhook-driven pipeline.
The new webhook runner is triggered by **the same labels and events** currently
defined in `step-transitions.yml` — nothing more, nothing less.
Once validated, GitHub Actions jobs are disabled (`if: false`).

## Current Architecture

```
GitHub Event (label added, comment created, PR merged, issue closed)
  → GitHub Actions (ubuntu-latest)
    → npx @anthropic-ai/claude-code
      → ANTHROPIC_BASE_URL: https://adesso-ai-hub.3asabc.de/v1
        → GitHub API (comments, labels)
```

## Target Architecture

```
GitHub Event (same labels and events as today)
  → GitHub Webhook (HTTPS POST)
    → Ngrok Tunnel
      → Casazen.WebhookRunner (localhost:5050)
        → JobQueue (dedup + concurrency control)
          → ClaudeCodeRunner (Process.Start)
            → claude CLI (local)
              → ANTHROPIC_BASE_URL (adesso-hub or local model)
                → GitHub API (comments, labels)
```

## Trigger Mapping

The webhook rules in `casazen-webhook-config.json` replicate exactly the 7 jobs
from `step-transitions.yml`. No new triggers, no agentic-kanban patterns.

| step-transitions.yml job | webhook rule |
|---|---|
| `trigger-step1-clarification` | `issues.labeled` = `raw-requirement` |
| `handle-clarification-reply` | `issue_comment.created` + label `awaiting-clarification` |
| `trigger-council` | `issues.labeled` = `council-ready` |
| `trigger-step2` | `issues.labeled` = `approved` |
| `trigger-step3` | `issues.labeled` = `in-sprint` |
| `trigger-step3-post-merge` | `pull_request.closed` merged=true |
| `trigger-unblock-on-close` | `issues.closed` state_reason=completed |

## Migration Strategy: Blue/Green

GitHub Actions remain active during build and validation (shadow mode).
Once all 7 rules are validated, Actions jobs are disabled with `if: false`.
Rollback: remove the `if: false` lines — takes under 5 minutes.

```
Phase 0  Prerequisites          ~1h
Phase 1  Ngrok tunnel           ~30m
Phase 2  WebhookRunner project  ~4h
Phase 3  Event mapping          ~30m   ← JSON config only, no code
Phase 4  GitHub configuration   ~30m   ← add webhook, shadow mode ON
Phase 5  Cutover                ~1h    ← validate + add if:false to Actions jobs
Phase 6  Grafana stack          ~3h    ← metrics + traces + dashboards
Phase 7  Cleanup                ~30m
```

Total estimate: ~11h spread over 3-4 sessions.

> Tunnel: **ngrok** (not Cloudflare) — auto-registration of GitHub webhook via
> `start-webhook-runner.ps1` mirrors `agentic-kanban/scripts/ngrok-start.ts`.

## Reference Project

`agentic-kanban` (`C:\Users\luca.la-malfa\source\repos\agentic-kanban`) implements
the same pattern for GitLab/Azure DevOps in TypeScript/Bun. Key patterns adopted:

| Pattern | agentic-kanban source | Used in |
|---|---|---|
| JSON rule config | `examples/calculator/config/config.json` | `casazen-webhook-config.json` |
| `{{PLACEHOLDER}}` context injection | `packages/queue/src/enqueuer.ts` | `GitHubEventRouter.InterpolatePrompt` |
| `--output-format stream-json` | `packages/core/src/agent.ts` | `ClaudeCodeRunner` |
| JSONL session store | `packages/core/src/store.ts` | `JsonlSessionStore` |
| Kill agent | `packages/core/src/agent.ts killAgent()` | `ClaudeCodeRunner.Kill()` |
| Ngrok startup script | `scripts/ngrok-start.ts` | `scripts/start-webhook-runner.ps1` |

## Files in This Plan

| File | Content |
|---|---|
| `01-ngrok-tunnel.md` | Install ngrok, startup script with auto-webhook-registration |
| `02-webhook-server-spec.md` | Full spec + code for `Casazen.WebhookRunner` (all components) |
| `03-event-mapping.md` | JSON rule config + context variable reference + test matrix |
| `04-github-configuration.md` | GitHub webhook registration (auto via script or manual) + shadow mode |
| `05-cutover.md` | Cutover checklist, `if: false` on Actions jobs, rollback procedure |
| `06-grafana-observability.md` | Grafana + Loki + Prometheus + Tempo stack; metrics, traces, dashboards |

## Success Criteria

- [ ] All 7 rules mirror exactly the 7 jobs in `step-transitions.yml`
- [ ] Signature verification rejects invalid payloads
- [ ] Duplicate events for the same issue are deduplicated
- [ ] GitHub Actions jobs disabled with `if: false` after validation
- [ ] Zero events lost during cutover window (72h GitHub delivery history)

## Rollback Plan

Remove `if: false` from each job in `step-transitions.yml` and push.
GitHub Actions resume immediately. No data is lost.

---

## Phase Summaries

### Phase 0 — Prerequisites

Verify local environment has all required tools before writing any code.

→ See each phase file for full checklist.

### Phase 1 — Cloudflare Tunnel

Give the local webhook server a stable public HTTPS URL without opening firewall
ports. Cloudflare Tunnel runs as a Windows service, survives reboots, and is free.

→ `01-cloudflare-tunnel.md`

### Phase 2 — WebhookRunner Project

New standalone .NET 10 Minimal API project `Casazen.WebhookRunner` (added to the
solution). Responsibilities:

- Receive POST /webhook from GitHub
- Verify HMAC-SHA256 signature
- Route event to the correct Claude Code command
- Queue jobs to avoid concurrent runs on the same issue
- Execute `npx @anthropic-ai/claude-code` as a subprocess
- Stream stdout/stderr to structured logs

→ `02-webhook-server-spec.md`

### Phase 3 — Event Mapping

Exact translation of every job in `step-transitions.yml` into a local handler.
Includes model selection, environment variables, and tool allowlists.

→ `03-event-mapping.md`

### Phase 4 — GitHub Configuration

Create the GitHub webhook pointing at the Cloudflare Tunnel URL.
Keep Actions in shadow mode (workflow_dispatch only) during validation.

→ `04-github-configuration.md`

### Phase 5 — Cutover

Validation checklist, final switch, and smoke test for each event type.

→ `05-cutover.md`

### Phase 6 — Cleanup

Remove Action triggers, delete SOVEREIGN_API_KEY from GitHub Secrets (move to
local config), prune unused worktrees.
