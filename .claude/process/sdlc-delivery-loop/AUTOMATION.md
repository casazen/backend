# Cursor Automation — CasaZen SDLC delivery loop

Prepared for Automations editor (Agents Window). Prefill cannot be opened from this agent session — finish in Cursor Agents Window.

Also mirrored at `Sessions/loop/automation-setup.md` (gitignored runtime copy for paste convenience).

## Draft

| Draft field | Value |
|---|---|
| Name | CasaZen SDLC delivery loop |
| Description | One delivery work-unit per run: queue → gap fix or one feature stage → gates → PR → Stage 04 fresh-context review → auto-merge to develop → informational notify. Continues until goal/queue empty, escalated, or merge_wait (checks pending). |
| Trigger | Cron — every 60 minutes (`0 * * * *`) or every 30 minutes (`*/30 * * * *`) |
| Tools | Standard agent tools (shell, git, gh). Optional Slack action in addition to webhook script. |
| Repo / branch | `casazen/backend` on `develop` (agent creates `feature/*` branches) |
| Secrets | `NOTIFY_WEBHOOK_URL` (required for real notify; script dry-runs without it) |
| Instructions | See prompt below |
| To finish in editor | Confirm repo/branch; add secret `NOTIFY_WEBHOOK_URL`; enable Cloud Agent if needed; save; optionally add Slack notify action |

## Agent instructions (paste)

```
You are running exactly one tick of the CasaZen SDLC Delivery Loop.

1. Read .claude/process/sdlc-delivery-loop/PROCESS.md and Sessions/loop/delivery-state.md.
2. If status is completed or escalated → stop and report.
3. If merge_wait is checks_pending → re-check PR checks with gh; merge to develop when green; stop if still pending; fail/escalate if checks failed.
4. Rewrite obsolete status awaiting_human_pr_review to running (or merge_wait) — do not pause for human merge.
5. Follow skill sdlc-delivery-tick exactly once:
   - sdlc-work-queue (scripts/quality/build-work-queue.ps1 -ApplyGoal)
   - Pick top work-unit (sticky feature_stage wins; else gap before new feature when P0 open)
   - Execute one gap fix OR one sdlc-stage-run stage only (unless this tick is review+merge retry)
   - sdlc-gate-runner → Sessions/loop/evidence/delivery-<tick>/
   - sdlc-matrix-writeback only when evidence overall=pass for gaps
   - After Stage 03 PASS or gap fix with mergeable commits: gh pr create/update → develop
   - Spawn fresh-context Stage 04 Task agents (code-reviewer + security-auditor) with PR diff + design AC map only
   - Stage 04 sdlc-gate-runner; on PASS: gh pr merge → develop (never main, never --force)
   - On checks pending: set merge_wait checks_pending and stop
   - sdlc-notify-human: pr_merged | review_failed | merge_wait | escalated | blocked (informational — not "please merge")
   - Update delivery-state, journal.md, metrics.md
6. Never promote develop→main unless prompt lists Phase B/C gates and they PASS.
7. Never declare PASS without evidence JSON. Never invent device/Maestro PASS — set blocked + Notes.
8. Same work-unit FAIL × 3 → sdlc-escalate + notify escalated + stop.
```

## Setup checklist

1. [ ] Commit and push delivery-loop process/skills + scripts to the branch Automation will check out (skills must be tracked for `@` references).
2. [ ] In Cursor → Agents → Automations → New: paste draft fields above.
3. [ ] Set cron trigger (recommend hourly).
4. [ ] Add secret `NOTIFY_WEBHOOK_URL` pointing to Slack Incoming Webhook, n8n/Make Telegram bridge, or email bridge.
5. [ ] Optional: add native Slack action as a second notify channel.
6. [ ] Confirm `gh` auth works for the Automation environment (PR create **and** merge to develop).
7. [ ] Smoke: run `/sdlc-delivery` once; confirm PR is reviewed by agents, merged to develop when checks green, and notify fires.
8. [ ] Keep `Sessions/loop/goal.md` `stop_on: continue_next_item` (default).

## Manual kickoff

In chat: `/sdlc-delivery` or invoke skill `sdlc-delivery-tick`.

Dry-run queue only:

```powershell
.\scripts\quality\build-work-queue.ps1 -DryRunPick -ApplyGoal
.\scripts\quality\notify-human.ps1 -Event pr_merged -WorkId TEST -Title t -Summary 'Informational: PR reviewed and merged to develop.' -DryRun
```
