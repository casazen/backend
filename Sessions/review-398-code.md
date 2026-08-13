# Stage 04 Code Review — PR #398

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/398 |
| Title | `fix(sdlc): block native-host Maestro gap AC15` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-6653` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `Sessions/quality/ac-matrix-mvp.md`, `Sessions/quality/requirements.json` |
| Work-unit | Delivery tick 9 `MATRIX:native-host:AC15` (`SPEC:native-host-app:AC15` — Maestro 0 crash on device) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for stated work-unit: `SPEC:native-host-app:AC15` → `blocked` (no invented Maestro/device PASS); matrix AC15 note cites missing `casazen/mobile` + no Maestro CLI/device |
| Async patterns | N/A (no C# / no I/O services) |
| EF Core | N/A (no schema / DbContext changes) |
| Testing | N/A for product code; verified env gates: `gh api repos/casazen/mobile` → 404; `maestro` absent; coverage lists AC15 under blocked (not open); extract sticky `pass`/`stub`/`blocked` guard preserves durability |
| SOLID | N/A (matrix + requirements status only; no new classes) |

## Diff summary

1. **`ac-matrix-mvp.md`**: Native Host AC15 row `fail` → `blocked` with honest blocker note (`casazen/mobile` 404; Maestro CLI/device unavailable; cannot prove 0-crash on device). Updated header to tick 9. Does not claim Maestro 0-crash PASS.
2. **`requirements.json`**: `SPEC:native-host-app:AC15.matrix_status` `fail` → `blocked` (`gap_id` unchanged `MATRIX:native-host:AC15`). Timestamp bump + entry reorder from extract; other P0 statuses preserved (AC21 `pass`, AC20/AC4/GJ/marketplace `blocked`, checkout L3 `missing-test`).

## Design AC map

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Spec / matrix AC15 (Maestro 0 crash on device) | Not closable without mobile repo + Maestro/device | `Sessions/loop/evidence/delivery-9/gates.json` overall=`blocked`; G-env-mobile exit 1 (repo 404); G-maestro-cli exit 127; G-maestro-smoke-struct exit 1; notes: no device PASS invented | blocked (honest) |
| extract preserve blocked | failHint `AC15 Maestro 0 crash` → Status `fail` must not reopen | `extract-requirements.ps1` cell `pass`/`stub`/`blocked` sticky + blocked guard; PR does not invent PASS | PASS (durable) |
| Coverage / freeze | AC15 skipped as blocked; other open P0 remain | `gate-G-matrix-blocked.log`: 1 open P0 (`SPEC:direct-checkout:AC-L3`), 8 blocked including `SPEC:native-host-app:AC15`; freeze-policy still applies via remaining `fail` matrix rows | PASS for work-unit |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

_None. (0 high)_

### 🟢 Medium

_None. (0 medium)_

### ⚪ Low

1. **`requirements.json` reorder noise** — Non-AC15 entries moved with status preserved (AC21 `pass`, AC20/AC4/GJ/marketplace `blocked`, checkout L3 `missing-test`). Harmless extract churn; makes the AC15 `fail`→`blocked` harder to spot in the unified diff.
2. **Pre-existing extract failHint** — `extract-requirements.ps1` still hardcodes `Status = 'fail'` for AC15; sticky cell/`blocked` guards keep durability. Not introduced by this PR; same pattern as prior blocked-gap PRs (#392 AC20).
3. **Out of scope / informational** — Sibling Native Host AC20 remains `blocked` under the same missing-repo/device conditions; checkout L3 remains the sole open P0 (`missing-test`). Correct for this single work-unit.

## Verification performed

```text
gh pr view 398 — 2 files; +35/-35 (docs-only quality matrix/requirements)
Sessions/loop/evidence/delivery-9/gates.json — overall=blocked
  G-env-mobile exit 1 (casazen/mobile 404)
  G-maestro-cli exit 127 (CLI missing)
  G-maestro-smoke-struct exit 1 (no mobile/ tree)
  G-matrix-blocked exit 0 (OPEN_HAS_AC15=0; AC15 listed blocked)
gh api repos/casazen/mobile → HTTP 404
command -v maestro → missing
requirements.json AC15.matrix_status === blocked; AC21 pass preserved
```

No Maestro 0-crash PASS invented in matrix, requirements, or evidence. Freeze-policy still applies via other matrix `fail` rows (coverage log confirms).

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 0 |
| ⚪ Low | 3 |

**Merge OK: yes** — no critical/high findings; change honestly marks Native Host Maestro AC15 blocked without inventing PASS, aligned with delivery-9 evidence. Security review is out of this agent’s scope.
