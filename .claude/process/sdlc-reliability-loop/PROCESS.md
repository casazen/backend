# SDLC Reliability Loop — Process (not a skill)

Canonical process for CasaZen delivery quality. Atomic skills under `.claude/skills/sdlc-*` implement steps; this document owns the state machine.

**Triggers:** `/sdlc-loop`, `/sdlc-pipeline` (compat), Cursor Automation cron tick, `resume loop`.

---

## Two nested loops

### Outer loop (portfolio)

Closes **all open P0 gaps** derived from ADRs + active specs + `Sessions/quality/ac-matrix-mvp.md`.

```
gap scan → prioritize → generate prompt → one unit of work → gate-runner → matrix write-back → stop or next tick
```

State: `Sessions/loop/state.md`  
Backlog: `Sessions/quality/gap-backlog.md` + `Sessions/quality/requirements.json`  
Evidence: `Sessions/loop/evidence/<tick>/`  
Next work: `Sessions/loop/next-prompt.md`

**Done when:** `open_p0_gaps == 0` (every P0 req is `pass` or explicit `stub` with `status:stub`).  
**Escalate when:** same gap FAIL ≥ 3 consecutive ticks → `sdlc-escalate` + HITL.

### Inner pipeline (feature)

Stages `01-planning` → `06-operations` as in `.claude/sdlc/`. Advance **only** when `sdlc-gate-runner` writes evidence PASS for that stage.

State: `Sessions/pipeline-<slug>/state.md`

---

## Tick procedure (one automation / agent run)

1. Read this file + `Sessions/loop/state.md`.
2. If `status` is `completed` or `escalated` → stop and report.
3. Run skill `sdlc-spec-gap` (refresh requirements + backlog).
4. If `open_p0_gaps == 0` → set `completed`, write metrics, stop.
5. Run `sdlc-prompt-gen` → overwrite `Sessions/loop/next-prompt.md`.
6. Execute the prompt (skills: `sdlc-init` / `sdlc-stage-run` / fix / `sdlc-contract-check` as directed).
7. Run `sdlc-gate-runner` for the gate list in the prompt; write `Sessions/loop/evidence/<tick>/`.
8. Run `sdlc-matrix-writeback` from evidence.
9. Update `Sessions/loop/state.md` (tick++, gaps, last_result).
10. If same gap failed 3 times → `sdlc-escalate`. Else leave state `running` for next cron tick.

**One tick = one unit of work.** Do not promote `develop`→`main` unless the prompt explicitly lists Phase B/C gates and gate-runner PASS.

---

## Non-negotiable rules

1. **No narrative PASS** — stage/matrix advance only from `Sessions/loop/evidence/<tick>/gates.json` (or stage evidence) with exit code 0.
2. **No N/A on changed layers** — `git diff --name-only` must be empty for any layer marked N/A.
3. **L2 alone does not close UI ACs** — L3 (or Maestro device) required for UI ACs.
4. **P0 fail freezes promote** — see `Sessions/quality/freeze-policy.md`; `check-spec-coverage.ps1` must pass before Phase C.
5. **Never close Issue #N before Stage 05 Phase B AC matrix ✅**.
6. **Never push directly to `main` or `develop`** — feature PRs → develop; release PR develop → main.
7. **Anti-stub** — `check-no-shipped-stubs.ps1`; stubs need `status:stub` label.
8. **No "Co-Authored-By: Claude"** in commits.

---

## Skill map

| Skill | Role |
|---|---|
| `sdlc-spec-gap` | Extract/refresh requirements + gap backlog |
| `sdlc-prompt-gen` | Write `next-prompt.md` from top gap |
| `sdlc-init` | Create/resume feature pipeline state |
| `sdlc-stage-run` | Run one SDLC stage (work only; no PASS claim) |
| `sdlc-gate-runner` | Execute harness commands; write evidence |
| `sdlc-contract-check` | ADR + design API ↔ BE ↔ FE compliance |
| `sdlc-matrix-writeback` | Update ac-matrix + spec status from evidence |
| `sdlc-loop-tick` | Orchestrate one outer tick (steps 1–10) |
| `sdlc-escalate` | Escalation artifact + stop |

Coordinator agents in `.claude/sdlc/<stage>/agents/` remain the workers for stage content.

---

## Requirement IDs

| Source | ID form |
|---|---|
| Spec AC | `SPEC:<slug>:AC<n>` |
| ADR MUST | `ADR-00N-R<k>` |
| Matrix row | Must reference a REQ-ID in Notes or AC column when machine-checked |

Priority: matrix P0 `fail` / `missing-test` / `in-progress` first, then uncovered ADR Requirements, then active-spec ACs without matrix rows. Respect `Sessions/specs/README.md` dependency graph (prefer path toward golden-journey).
