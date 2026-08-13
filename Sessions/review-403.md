# Stage 04 Review — PR #403

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/403 |
| Title | `docs(sdlc): Stage 02 design for onboarding-plg (#271)` |
| Base / head | `develop` ← `feature/271-onboarding-plg` |
| Work-unit | Delivery tick 13 `SPEC:onboarding-plg` Stage 02 Design |
| Evidence | `Sessions/loop/evidence/delivery-13/` overall=`pass` (G1–G10, G9b) |
| Code review | `Sessions/review-403-code.md` — 🔴**0** 🟡**0** (2 fixed in design patch) |
| Security audit | `Sessions/review-403-security.md` — 🔴0 🟡0 |

## Code-reviewer summary

Stage 02 design + e2e path scaffolds + `check-ac-matrix.ps1` parent-path fix. No Stage 03 FE claimed; no Maestro/device invented PASS.

**🟡 dispositions (resolved in design patch):**
1. ConsentType / Marketing vs Migration Plan → aligned to `SubprocessorsAck`; Stage 03 adds `Marketing` enum (documented in Migration Plan)
2. `activated` formula → explicit six-bool conjunction as Stage 03 target

**Merge OK (code-review): yes** after patch.

## Security-auditor summary

Auth/`[AllowAnonymous]` legal justifications, IDOR (`sub`→Org), consent IP/PII, GDPR Guest N/A, secrets hygiene: design PASS. 🔴0 🟡0. Merge OK from security: **yes**.

## AC matrix (Stage 02 design — not Stage 03 evidence)

| AC / req | Claim | Evidence | Result |
|---|---|---|---|
| Stage 02 G1–G10 + G9b | Design PASS | delivery-13 gates.json overall=pass | PASS (process) |
| AC Test Map AC1–AC12 | Paths + REQ-IDs | design-271 + check-ac-matrix PASS | PASS (Stage 02) |
| Product / FE activation ACs | Not claimed this tick | Stage 03 pending | N/A |
| Device / Maestro | Not in scope | No paths / no PASS | N/A (honest) |

## Gate summary (Stage 04)

| Gate | Result |
|---|---|
| G1 PR mergeable | PASS when checks green |
| G2 No critical findings | PASS (0 🔴) |
| G3 High findings | PASS (🟡 fixed in design) |
| G4 Cross-repo | N/A Stage 02 (FE SoT deferred) |
| G5–G10 Security | PASS / N/A (security audit) |
| G11 AC matrix | Stage 02 map PASS; Stage 03 `-RequireTests` not claimed |
| G12 Anti-stub | Scaffolds documented as non-SoT |
| G13 Evidence-only | delivery-13 + review artifacts |

## Merge decision

**Merge OK: yes** (aggregate) after design 🟡 fixes — auto-merge to `develop` when required CI checks green.
