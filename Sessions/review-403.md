# Stage 04 Review — PR #403

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/403 |
| Title | `docs(sdlc): Stage 02 design for onboarding-plg (#271)` |
| Base / head | `develop` ← `feature/271-onboarding-plg` |
| Work-unit | Delivery tick 13 `SPEC:onboarding-plg` Stage 02 Design |
| Evidence | `Sessions/loop/evidence/delivery-13/` overall=`pass` (G1–G10, G9b) |
| Code review | `Sessions/review-403-code.md` — 🔴**0** 🟡**2** |
| Security audit | `Sessions/review-403-security.md` — 🔴0 🟡0 |

## Code-reviewer summary (contribute)

Stage 02 design + e2e path scaffolds + `check-ac-matrix.ps1` parent-path fix. Gate narrative matches delivery-13; no Stage 03 FE activation checklist FAIL; no Maestro/device invented PASS. AC1–AC12 map present with `SPEC:onboarding-plg:ACn`.

**Open 🟡 (2):**
1. Design `ConsentType` includes `Marketing` / `Subprocessors` and GDPR requires Marketing rows, but Migration Plan says no further schema while tree enum is `{ Tos, Privacy, Dpa, SubprocessorsAck }` — fix design/migration honesty before Stage 03.
2. `activated` underspecified (“all milestones”) vs AC5–AC6/AC10 and current BE formula — design must state explicit Stage 03 predicate.

**Merge OK (code-review): no** until those 🟡 are fixed or formally deferred in design Open Questions.

## Security-auditor summary

Auth/`[AllowAnonymous]` legal justifications, IDOR (`sub`→Org), consent IP/PII, GDPR Guest N/A, secrets hygiene: design PASS. 🔴0 🟡0. Merge OK from security: **yes**. Runtime re-audit still required at Stage 03.

## AC matrix (Stage 02 design — not Stage 03 evidence)

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Stage 02 G1–G10 + G9b | Design PASS | delivery-13 gates.json overall=pass | PASS (process) |
| AC Test Map AC1–AC12 | Paths + REQ-IDs | design-271 + check-ac-matrix PASS | PASS (Stage 02) |
| Product / FE activation ACs | Not claimed this tick | Stage 03 pending | N/A |
| Device / Maestro | Not in scope | No paths / no PASS | N/A (honest) |

## Gate summary (Stage 04 framing)

| Gate | Result |
|---|---|
| G1 PR mergeable | Pending parent (MERGEABLE check) |
| G2 No critical findings | PASS (0 🔴 code + security) |
| G3 High findings | **FAIL** — 2 open 🟡 from code-review (unless deferred/fixed) |
| G4 Cross-repo | N/A Stage 02 (design + backend path scaffolds; FE SoT deferred) |
| G5–G10 Security surfaces | Design PASS / N/A (security audit) |
| G11 AC matrix complete | Stage 02 map PASS; Stage 03 `-RequireTests` not claimed |
| G12 Anti-stub on diff | Scaffolds documented as non-SoT; not product ship |
| G13 Evidence-only PASS | delivery-13 + review artifacts |

## Merge decision

**Merge OK: no** (aggregate) — security clear; code-review holds on 2 design-contract 🟡. Parent tick should patch `Sessions/design-271.md` (or defer 🟡 in Open Questions with Stage 03 owners) then re-run Stage 04 G3.
