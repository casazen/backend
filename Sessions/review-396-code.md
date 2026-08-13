# Stage 04 Code Review — PR #396

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/396 |
| Title | `fix(sdlc): block native-host calendar gap AC4` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-9065` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `Sessions/quality/ac-matrix-mvp.md`, `Sessions/quality/requirements.json` |
| Work-unit | Delivery tick 7 `MATRIX:native-host:AC4` (`SPEC:native-host-app:AC4` — Calendar month/week grid) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for stated work-unit: `SPEC:native-host-app:AC4` → `blocked` (no invented calendar month/week PASS); matrix AC4 note cites missing `casazen/mobile` + no `mobile/` tree |
| Async patterns | N/A (no C# / no I/O services) |
| EF Core | N/A (no schema / DbContext changes) |
| Testing | N/A for product code; verified env gates: `gh api repos/casazen/mobile` → 404; `test -d mobile` → missing; coverage lists AC4 under blocked (not open); extract preserves `blocked` |
| SOLID | N/A (matrix + requirements status only; no new classes) |

## Diff summary

1. **`ac-matrix-mvp.md`**: Native Host AC4 row `fail` → `blocked` with honest blocker note (`casazen/mobile` 404; no `mobile/` tree; historically FlatList-only). Does not claim calendar month/week grid PASS.
2. **`requirements.json`**: `SPEC:native-host-app:AC4.matrix_status` `fail` → `blocked` (`gap_id` unchanged `MATRIX:native-host:AC4`). Timestamp bump + entry reorder from `extract-requirements.ps1`; other req statuses unchanged (AC15 `fail`, AC21 `missing-test`, checkout L3 `missing-test`, AC20/GJ/marketplace rows remain `blocked`).

## Design AC map

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Spec / matrix AC4 (Calendar month/week grid) | Not closable without Expo calendar UI in `casazen/mobile` | `Sessions/loop/evidence/delivery-7/gates.json` overall=`blocked`; G-env-mobile exit 1 (repo 404); G-mobile-tree exit 1 (no `mobile/`); notes: no AC4 PASS invented | blocked (honest) |
| extract preserve blocked | AC4 status must remain `blocked` after extract | `gate-G-extract.log` exit 0; `open_p0ish=3`; `requirements.json` AC4.`matrix_status`=`blocked` | PASS (durable) |
| Coverage / freeze | AC4 skipped as blocked; other P0 fail/missing-test remain | `gate-G-coverage.log`: 3 open P0, 7 blocked including `SPEC:native-host-app:AC4`; freeze-policy still applies | PASS for work-unit |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

_None. (0 high)_

### 🟢 Medium

_None. (0 medium)_

### ⚪ Low

1. **`requirements.json` reorder noise** — Non-AC4 entries moved (checkout L3, GJ AC6, marketplace L3/supplier, AC15, AC20) with status preserved. Harmless extract churn; makes the AC4 `fail`→`blocked` harder to spot in the unified diff.
2. **Out of scope / informational** — Sibling Native Host AC15 (`Maestro 0 crash`) remains `fail` and AC21 (`Backend push tests`) remains `missing-test` under related missing-repo/device conditions. Correct for this single work-unit; not a defect of AC4 env-block persistence.
3. **Maestro gate is supporting only** — G-maestro exit 1 is noted as supporting signal; AC4 is UI (calendar grid), not Maestro. Correct framing in `gates.json`; no overclaim.

## Verification performed

```text
gh pr diff 396 — 2 files; +34/-34 (substantive: AC4 fail→blocked)
Sessions/loop/evidence/delivery-7/gates.json — overall=blocked; G-env-mobile=1; G-mobile-tree=1; G-maestro=1; G-extract=0; G-coverage exit 1 expected (other open P0)
gh api repos/casazen/mobile → HTTP 404
test -d mobile → missing
check-spec-coverage → SPEC:native-host-app:AC4 [blocked]; not in open P0 list
requirements.json AC4.matrix_status === blocked
spec-native-host-app.md AC4 — Calendar month/week view; bookings + iCal blocks
```

No Calendar month/week PASS invented in matrix, requirements, or evidence. Freeze-policy still applies via remaining P0 `fail` / `missing-test` rows (coverage log confirms).

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 0 |
| ⚪ Low | 3 |

**Merge OK: yes** — no critical/high findings; change honestly marks Native Host Calendar AC4 blocked without inventing PASS, aligned with delivery-7 evidence. Security review is out of this agent’s scope.
