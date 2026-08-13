# Stage 04 Code Review — PR #391

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/391 |
| Title | `fix(sdlc): block golden-journey host Maestro gap AC6-12` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-12e3` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `Sessions/quality/ac-matrix-mvp.md`, `Sessions/quality/requirements.json`, `scripts/quality/extract-requirements.ps1` |
| Work-unit | Delivery tick 3 `MATRIX:gj:AC6-12` (`SPEC:golden-journey-e2e:AC6` — Host app M1–M7 Maestro) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for stated work-unit: `SPEC:golden-journey-e2e:AC6` → `blocked` (no invented Maestro/device PASS); matrix AC6–AC12 note cites missing `casazen/mobile` + no Maestro CLI/device |
| Async patterns | N/A (no C# / no I/O services) |
| EF Core | N/A (no schema / DbContext changes) |
| Testing | N/A for quality harness scripts; verified: extract preserves `blocked` under new failHint; coverage lists AC6 blocked; queue top ≠ `MATRIX:gj:AC6-12` |
| SOLID | N/A (script hint + existing blocked guard; no new classes) |

## Diff summary

1. **`ac-matrix-mvp.md`**: Golden Journey AC6–AC12 row `fail` → `blocked` with honest blocker note (`casazen/mobile` missing; Maestro CLI/device unavailable). Does not claim M1–M7 PASS.
2. **`requirements.json`**: `SPEC:golden-journey-e2e:AC6.matrix_status` `fail` → `blocked` (`gap_id` unchanged `MATRIX:gj:AC6-12`).
3. **`extract-requirements.ps1`**: Adds failHint `Pattern = 'Host app M1'` → `SPEC:golden-journey-e2e:AC6` / `Status = 'fail'`. Existing guard (`matrix_status -ne 'blocked'`) prevents reopen on regenerate.

## Design AC map

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Spec AC6–AC12 (Maestro M1–M7) | Not closable without mobile repo + Maestro/device | `Sessions/loop/evidence/delivery-3/gates.json` overall=`blocked`; G-env-mobile exit 1 (repo missing); G-maestro exit 2 (CLI absent); no Maestro suite PASS claimed | blocked (honest) |
| extract preserve blocked | failHint cannot reopen blocked | `extract-requirements.ps1:85` + `:91-93`; local re-run keeps AC6 `matrix_status=blocked` | PASS |
| Queue skip | Blocked gap not top pick | `gate-G-queue.log` TOP PICK=`MATRIX:checkout:L3` | PASS |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

_None. (0 high)_

### 🟢 Medium

1. **`scripts/quality/extract-requirements.ps1:85`** — New failHint hardcodes `Status = 'fail'` while the matrix cell is now `blocked`. Same pattern as marketplace L3 (#388): the `:91-93` guard makes this safe; consider aligning the hint Status (or parsing the Status cell) so writers do not treat the hint as source-of-truth for blocked rows.

### ⚪ Low

1. **Pattern specificity** — `'Host app M1'` correctly matches Golden Journey description `Host app M1–M7` and does not collide with Native Host `AC20 Maestro M1–M7` (separate hint). No defect; keep wording stable if the matrix description is edited.
2. **Out of scope / informational** — Sibling GJ rows AC1–AC5 (`in-progress`) and AC13 (`missing-test`) remain open. Correct for this single work-unit; not a defect of AC6–12 env-block persistence.

## Verification performed

```text
gh pr diff 391 — 3 files; +4/-3
Sessions/loop/evidence/delivery-3/gates.json — overall=blocked; G-env-mobile=1; G-maestro=2; G-extract=0; G-coverage effective PASS (AC6 blocked); G-queue top=MATRIX:checkout:L3
pwsh extract-requirements.ps1 → SPEC:golden-journey-e2e:AC6 remains blocked (failHint did not reopen)
gate-G-coverage.log → 7 open P0, 3 blocked (includes AC6 + AC-L3 + ADR-003-R6)
```

No Maestro M1–M7 PASS invented in matrix, requirements, or evidence. Freeze-policy still applies via other P0 `fail` rows (coverage log confirms).

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 1 (stale failHints Status value vs matrix `blocked`) |
| ⚪ Low | 2 |

**Merge OK: yes** — no critical/high findings; change honestly marks host-Maestro gap blocked without inventing PASS, and failHint + blocked-preserve interact correctly. Security review is out of this agent’s scope.
