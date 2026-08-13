# Stage 04 Code Review — PR #403

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/403 |
| Title | `docs(sdlc): Stage 02 design for onboarding-plg (#271)` |
| Base / head | `develop` ← `feature/271-onboarding-plg` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff only: `Sessions/design-271.md`, `e2e/` scaffolds, `scripts/quality/check-ac-matrix.ps1` |
| Work-unit | Delivery tick 13 `SPEC:onboarding-plg` Stage **02 Design** — evidence overall=`pass` (G1–G10 + G9b) |
| Kind | Design/docs + path scaffolds + quality-script fix — **not** Stage 03 product delivery |
| Findings | 🔴 **0** · 🟡 **2** · 🟢 **3** · ⚪ **1** |

## Correctness vs stated work-unit

| Claim | Assessment |
|---|---|
| Stage 02 design PASS for `#271` | **Honest** — local `Sessions/loop/evidence/delivery-13/gates.json` `overall=pass`; G1–G10 + G9b all `exit_code=0`. Re-ran `check-ac-matrix.ps1 -DesignPath Sessions/design-271.md` → PASS (12 AC rows). |
| Not claiming Stage 03 FE/product complete | **Pass** — Notes + PR body defer activation checklist / subprocessors page / titled L2–L3 expansion to Stage 03. No invented FE activation implementation PASS. |
| No device / Maestro invented PASS | **Pass** — AC Test Map is web L1/L2/L3 only; no Maestro/`mobile/` paths; no native PASS claims. |
| Design cites spec | **Pass** — `Sessions/specs/spec-onboarding-plg.md` (G10). |
| Diff surface | Design + e2e path scaffolds + `check-ac-matrix.ps1` parent-path hardening — matches PR description. |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`) — Stage 02 design adaptation

| Area | Result |
|---|---|
| Correctness / AC | AC1–AC12 present with REQ-IDs `SPEC:onboarding-plg:ACn`. API/FE/security/migration/GDPR sections present. **Two design-contract inconsistencies** (see 🟡). |
| Async patterns | N/A (no new C# I/O in PR) |
| EF Core | Design documents existing `ConsentRecords` migration; no new migration in diff. **Marketing enum gap** vs Migration Plan (🟡). |
| Testing | Stage 02 path scaffolds only — titled `test('ACn: …')` stubs OK for G9 path-exists; not Stage 03 `-RequireTests` evidence. L1 path `PlgOnboardingIntegrationTests.cs` exists in tree. |
| SOLID | N/A for product classes; script change is a small pure helper (`Get-RepoParent`). |

## Diff summary

1. **`Sessions/design-271.md`**: Full Stage 02 design — data model, API contract (extend POST/PUT onboarding; status + legal GETs), FE flow, security, migration, GDPR, AC Test Map AC1–AC12.
2. **`e2e/README.md` + `e2e/onboarding-plg.spec.ts` + `e2e/l3/onboarding-plg-l3.spec.ts`**: Explicit backend path anchors for G9; README states FE repo is source of truth; L3 uses `test.skip` / `expect(true)` placeholders (not shipped product coverage).
3. **`scripts/quality/check-ac-matrix.ps1`**: `Get-RepoParent` when `Split-Path` returns empty on single-segment roots (`/workspace`) — correct Cloud fix; re-verified PASS.

## AC map alignment notes

| AC | Design coverage | Notes |
|---|---|---|
| AC1 | POST Org provision idempotent; plan default `Starter`; not overwritten on re-run | Aligns with spec + `EnsureOrgForUserAsync` (existing Org returned as-is). |
| AC2 | Consents block + 400 on incomplete/stale | Aligns. |
| AC3 | `ConsentRecord` append-only; `RecordedAt` (tree name; spec prose `acceptedAt`) | **🟡** enum lists `Subprocessors`/`Marketing` vs shipped `ConsentType { Tos, Privacy, Dpa, SubprocessorsAck }` with **no Marketing**. |
| AC4 | Anonymous subprocessors/dpa/tos (+ design adds privacy) | Aligns with AC + existing `LegalController`; privacy is sound extension. |
| AC5–AC6 | Status DTO + real-state derivations | Milestone derivations match AC6. **🟡** `activated` = “All activation milestones true” is not an explicit boolean formula. |
| AC7 | PUT without required consents; Org/consents retained | Mostly aligns; PUT parenthetical on plan is slightly muddy (🟢). |
| AC8–AC12 | FE routes/components + L2/L3 map | Scaffold paths OK for Stage 02; **do not** treat scaffolds as Stage 03 PASS. AC12 correctly L2 (demo guard) — not Maestro. |

## Status integrity (no invented PASS)

| Check | Result |
|---|---|
| Stage 02 gates | delivery-13 `overall=pass`; all listed gates exit 0 |
| Product AC implementation PASS | **Not claimed** |
| Device / Maestro | **None** — correctly absent |
| E2E scaffolds as L3 evidence | README + stubs make clear these are path anchors only |
| Partial BE honesty | Notes say BE/FE partial; Stage 03 closes gaps — accurate vs tree (`sitePublished` still stubbed in `OnboardingService`, etc.) |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

1. **`ConsentType` / Marketing vs Migration Plan** (`Sessions/design-271.md` Data model ~L29, GDPR ~L193, Migration Plan ~L183) — Design data model includes `ConsentType.Marketing` and `Subprocessors`, and GDPR Scope requires persisting marketing opt-in as `ConsentType.Marketing`. Shipped enum is `ConsentType { Tos, Privacy, Dpa, SubprocessorsAck }` (**no Marketing**; name is `SubprocessorsAck`). Migration Plan says “no additional schema expected unless … indexes.” That is an internal contradiction: either document an enum (+ any EF) change in Migration Plan for Stage 03, or drop Marketing from the data model and define another durable store. Leaving both claims blocks honest Stage 03 AC3/GDPR delivery.

2. **`activated` predicate underspecified** (`Sessions/design-271.md` OnboardingStatusDto ~L106) — Field is described only as “All activation milestones true” without the explicit conjunction. Spec AC5–AC6/AC10 imply checklist completion including `sitePublished` and `firstBookingTaken` (GTM activation). Tree today computes `activated = roleChosen && orgProvisioned && consentsAccepted && propertyCreated` (omits site/booking; `sitePublished` hardcoded false). Stage 02 design must state the Stage 03 target formula (recommend: all six checklist bools) so L1/L3 do not encode the wrong hide-when-activated behavior.

### 🟢 Medium

1. **API Contract Status “New” for existing surfaces** (`Sessions/design-271.md` ~L44–48) — Notes correctly say `OnboardingController` / `LegalController` already exist; Status column still labels them **New**. Prefer `Exists` / `Verify` / `Extend` so Stage 03 does not re-scaffold duplicate controllers.

2. **PUT plan wording vs AC7** (`Sessions/design-271.md` ~L88) — “optional plan on first provision path” is true for `EnsureOrgForUserAsync` (plan set only when Org created) but reads like PUT may change plan. Tighten to: PUT never overwrites `PlanTier` when `OrgId` already set; only `rentalType`/roles change.

3. **Enum name `Subprocessors` vs `SubprocessorsAck`** — Align design table with shipped enum (or explicitly “rename in Stage 03”) to avoid dual vocabulary in tests.

### ⚪ Low

1. **Backend e2e scaffolds import FE helpers** (`e2e/onboarding-plg.spec.ts` → `./test`, `./helpers/demo-profile`) — Expected for path-only anchors; README warns FE is SoT. Fine for Stage 02; Stage 03 FE PR owns runnable specs.

## Verification performed

```text
gh pr view/diff 403 — 5 files; design + e2e scaffolds + check-ac-matrix.ps1; base develop ← feature/271-onboarding-plg
Sessions/loop/evidence/delivery-13/gates.json — overall=pass; G1–G10 + G9b exit 0
pwsh check-ac-matrix.ps1 -DesignPath Sessions/design-271.md — PASS (12 AC rows)
Spec AC1–AC12 cross-check vs design API/FE/AC Test Map
Tree: ConsentType enum; OnboardingService activated/sitePublished; EnsureOrgForUserAsync plan idempotency
No Maestro/device paths; no Stage 03 FE activation checklist claimed complete
```

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical (open) | **0** |
| 🟡 High (open) | **2** |
| 🟢 Medium | 3 |
| ⚪ Low | 1 |

**Merge OK: no** — from code-review perspective, resolve or explicitly defer the two 🟡 items in `Sessions/design-271.md` (Marketing/`ConsentType` + Migration Plan honesty; explicit `activated` formula) before treating Stage 02 design as Stage 03-ready. Do **not** fail this PR for missing FE activation checklist implementation (Stage 03). Security review is out of this agent’s scope. Do not merge from this agent (parent/delivery tick owns merge).
