# Stage 04 Code Review — PR #388

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/388 |
| Title | `fix(sdlc): block marketplace L3 gap without Auth0 E2E secrets` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-5b70` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `scripts/quality/extract-requirements.ps1`, `Sessions/quality/requirements.json`, `Sessions/quality/ac-matrix-mvp.md` |
| Work-unit | Delivery tick 2 `MATRIX:marketplace:L3` (env-blocked gap persistence) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for stated work-unit: `SPEC:micro-marketplace-v0:AC-L3` → `blocked` (not invented FE Playwright PASS); matrix note cites BE `CompleteFlow_TakeCompleteMarkPaid_Succeeds` only as API coverage |
| Async patterns | N/A (no C# / no I/O services) |
| EF Core | N/A (no schema / DbContext changes) |
| Testing | N/A for quality harness scripts; verified locally: extract preserves `blocked` under failHints; `check-spec-coverage` → `8 open, 2 blocked` with AC-L3 listed blocked |
| SOLID | N/A (script guard; no new classes) |

## Diff summary

1. **`requirements.json`**: Sole semantic status change vs `origin/develop` is `SPEC:micro-marketplace-v0:AC-L3`: `missing-test` → `blocked` (`gap_id` unchanged `MATRIX:marketplace:L3`). Other P0 rows only reordered by extract (statuses unchanged). Blocked total = 2 (with prior `ADR-003-R6`).
2. **`ac-matrix-mvp.md`**: L3 row `missing-test` → `blocked`; status vocabulary adds `blocked`; honest note that FE Auth0 E2E is required and BE CompleteFlow already covers API loop.
3. **`extract-requirements.ps1`**: failHints overwrite skipped when `matrix_status -eq 'blocked'` — prevents pattern `L3 real API loop` → `missing-test` from reopening the gap.

## Design AC map

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| `SPEC:micro-marketplace-v0:AC-L3` | Not closable without FE Auth0 E2E | `Sessions/loop/evidence/delivery-2/gates.json` overall=`blocked`; G-env exit 2; G-be-loop CompleteFlow PASS; no FE Playwright L3 PASS claimed | blocked (honest) |
| extract preserve blocked | failHints cannot reopen blocked | `extract-requirements.ps1:91-93`; local re-run keeps AC-L3 `blocked` | PASS |

## Findings

### 🔴 Critical

_None._

### 🟡 High

_None._

### 🟢 Medium

1. **`scripts/quality/extract-requirements.ps1:83`** — failHints still hardcodes `Status = 'missing-test'` for `L3 real API loop` even though the matrix cell is now `blocked`. The new guard makes this safe; consider aligning the hint (or parsing the Status cell) so future writers do not assume the hint is source-of-truth.

### ⚪ Low

1. **`Sessions/quality/requirements.json` (reorder hunks)** — Large churn from regenerate/sort without functional status changes beyond AC-L3; harder to review, no logic defect.
2. **Out of scope / informational** — Sibling marketplace row `Supplier take/complete` / `SPEC:micro-marketplace-v0:AC-supplier` remains `fail`/open. Correct for this single work-unit; not a defect of L3 env-block persistence.

## Verification performed

```text
git diff origin/develop...HEAD — 3 files; only AC-L3 status change in requirements.json
Sessions/loop/evidence/delivery-2/gates.json — overall=blocked; G-env=2; G-be-loop=0; G-extract=1 (other P0s)
pwsh extract-requirements.ps1 → AC-L3 remains blocked (failHints did not reopen)
pwsh check-spec-coverage.ps1 → 8 open P0, 2 blocked (includes AC-L3 + ADR-003-R6); exit 1
```

No FE Playwright L3 PASS invented in matrix, requirements, or evidence.

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 1 (stale failHints Status value) |
| ⚪ Low | 2 |

**Merge OK from code-review perspective: YES** — no critical/high findings; change honestly marks env-blocked L3 without inventing FE E2E PASS, and extract preserve-blocked matches delivery-loop intent. Security review is out of this agent’s scope.
