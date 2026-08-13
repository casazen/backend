# Cursor Automation — CasaZen SDLC reliability loop

Prepared for Automations editor (Agents Window). Prefill cannot be opened from this session (`open_automation` unavailable) — finish in Cursor Agents Window with skill Automate.

## Draft

| Draft field | Value |
|---|---|
| Name | CasaZen SDLC reliability loop |
| Description | One outer-loop tick per run: refresh gaps, execute next-prompt, gate-runner evidence, matrix write-back. Continues until P0 gaps are closed or escalated. |
| Trigger | Cron — every day at 09:00 (`0 9 * * *`) |
| Tools | Standard agent tools (shell, git, gh). No MCP required for base tick. |
| Repo / branch | `casazen/backend` on `develop` (or `automation/sdlc-loop` if preferred) |
| Instructions | See prompt below |
| To finish in editor | Confirm repo/branch checkout; enable Cloud Agent compute if needed; save |

## Agent instructions (paste)

```
You are running one tick of the CasaZen SDLC reliability loop.

1. Read .claude/process/sdlc-reliability-loop/PROCESS.md and Sessions/loop/state.md.
2. If status is completed or escalated, stop and report.
3. Follow skill sdlc-loop-tick exactly once:
   - sdlc-spec-gap (or run scripts/quality/extract-requirements.ps1 + check-spec-coverage.ps1 -UpdateBacklog)
   - sdlc-prompt-gen → Sessions/loop/next-prompt.md
   - Execute next-prompt.md (implement/fix as allowed)
   - sdlc-gate-runner → Sessions/loop/evidence/<tick>/
   - sdlc-matrix-writeback only from evidence overall=pass
4. Do not promote develop→main unless next-prompt explicitly lists Phase B/C gates and they PASS.
5. Do not declare PASS without evidence JSON.
6. If the same gap fails 3 consecutive ticks, run sdlc-escalate and stop.
```

## Manual kickoff

In chat: `/sdlc-loop` or invoke skill `sdlc-loop-tick`.
