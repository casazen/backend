# Stage 04 Code Review — PR #392

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/392 |
| Title | `fix(sdlc): block native-host Maestro gap AC20` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-35af` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `Sessions/quality/ac-matrix-mvp.md`, `Sessions/quality/requirements.json` |
| Work-unit | Delivery tick 4 `MATRIX:native-host:AC20` (`SPEC:native-host-app:AC20` — Maestro M1–M7 device green) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for stated work-unit: `SPEC:native-host-app:AC20` → `blocked` (no invented Maestro/device PASS); matrix AC20 note cites missing `casazen/mobile` + no Maestro CLI/device |
| Async patterns | N/A (no C# / no I/O services) |
| EF Core | N/A (no schema / DbContext changes) |
| Testing | N/A for product code; verified env gates: `gh api repos/casazen/mobile` → 404; `maestro` absent; coverage lists AC20 under blocked (not open); extract failHint guard preserves `blocked` |
| SOLID | N/A (matrix + requirements status only; no new classes) |

## Diff summary

1. **`ac-matrix-mvp.md`**: Native Host AC20 row `fail` → `blocked` with honest blocker note (`casazen/mobile` missing; Maestro CLI/device unavailable; structural smoke alone insufficient). Does not claim M1–M7 PASS.
2. **`requirements.json`**: `SPEC:native-host-app:AC20.matrix_status` `fail` → `blocked` (`gap_id` unchanged `MATRIX:native-host:AC20`). Timestamp bump + entry reorder from `extract-requirements.ps1`; other req statuses unchanged (AC15 `fail`, GJ AC6 `blocked`, marketplace L3 `blocked`, checkout L3 `missing-test`).

## Design AC map

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Spec / matrix AC20 (Maestro M1–M7 device green) | Not closable without mobile repo + Maestro/device | `Sessions/loop/evidence/delivery-4/gates.json` overall=`blocked`; G-env-mobile exit 1 (repo 404); G-maestro exit 2 (CLI absent); notes: no Maestro M1–M7 PASS invented | blocked (honest) |
| extract preserve blocked | Existing failHint `AC20 Maestro M1` → Status `fail` must not reopen | `extract-requirements.ps1:80` + `:91-93` guard; PR does not invent PASS | PASS (durable) |
| Coverage / freeze | AC20 skipped as blocked; other P0 fail remain | `gate-G-coverage.log`: 6 open P0, 4 blocked including `SPEC:native-host-app:AC20`; freeze-policy still applies | PASS for work-unit |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

_None. (0 high)_

### 🟢 Medium

_None. (0 medium)_

### ⚪ Low

1. **`requirements.json` reorder noise** — Non-AC20 entries moved (AC15, checkout L3, GJ AC6, marketplace L3) with status preserved. Harmless extract churn; makes the AC20 `fail`→`blocked` harder to spot in the unified diff.
2. **Pre-existing extract failHint** — `extract-requirements.ps1:80` still hardcodes `Status = 'fail'` for AC20; `:91-93` blocked guard is what keeps durability. Not introduced by this PR; same pattern as prior blocked-gap PRs.
3. **Out of scope / informational** — Sibling Native Host AC15 (`Maestro 0 crash`) remains `fail` under the same missing-repo/device conditions. Correct for this single work-unit; not a defect of AC20 env-block persistence.

## Verification performed

```text
gh pr diff 392 — 2 files; +34/-34 (substantive: AC20 fail→blocked)
Sessions/loop/evidence/delivery-4/gates.json — overall=blocked; G-env-mobile=1; G-maestro=2; G-extract=0; G-coverage exit 1 expected (other open P0)
gh api repos/casazen/mobile → HTTP 404
command -v maestro → missing
check-spec-coverage → SPEC:native-host-app:AC20 [blocked]; not in open P0 list
requirements.json AC20.matrix_status === blocked
```

No Maestro M1–M7 PASS invented in matrix, requirements, or evidence. Freeze-policy still applies via other P0 `fail` rows (coverage log confirms).

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 0 |
| ⚪ Low | 3 |

**Merge OK: yes** — no critical/high findings; change honestly marks Native Host Maestro AC20 blocked without inventing PASS, aligned with delivery-4 evidence. Security review is out of this agent’s scope.
