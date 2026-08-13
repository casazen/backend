# Stage 04 Code Review — PR #404

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/404 |
| Issue | [#271](https://github.com/casazen/backend/issues/271) — onboarding-plg Stage 03 |
| Title | `feat(onboarding): Stage 03 PLG activation + Marketing consent (#271)` |
| Base / head | `develop` ← `feature/271-onboarding-plg` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Design | `Sessions/design-271.md` (AC Test Map + API contract) |
| Contract check | `Sessions/pipeline-onboarding-plg/contract-check.md` — **PASS** |
| Evidence | `Sessions/loop/evidence/delivery-14/gates.json` — **overall=`pass`** (G1–G13 + G9a–G9g) |
| Scope | Diff only (8 files): `ConsentType`, `OnboardingService`, `PlgOnboardingIntegrationTests`, design AC12 map, e2e L2/L3, quality scripts |
| Findings | 🔴 **0** · 🟡 **0** (resolved in follow-up commit) · 🟢 **3** · ⚪ **1** |

## 1. Summary of what changed

1. **`ConsentType.Marketing`** — Enum extended (int store; no EF migration). Optional `marketingOptIn=true` now appends a `ConsentRecord` with `Type=Marketing` (version currently mirrored from `TosVersion`).
2. **`OnboardingService.GetActivationStatusAsync`** — Stage 03 target formula shipped:
   - `sitePublished` = `Org.IsActive` ∧ ≥1 active `Property` (stub `false` removed)
   - `firstBookingTaken` = ≥1 `Booking` with `Status=Confirmed` ∧ `Source=Direct`
   - `activated` = six-bool conjunction (`roleChosen && orgProvisioned && consentsAccepted && propertyCreated && sitePublished && firstBookingTaken`)
   - `publicBookingUrl` = `{App:PublicSiteBaseUrl}/book/{Slug}` when site published (aligned with `OrgDomainService.BuildPublicUrls` path pattern)
3. **L1 tests** — `PlgOnboardingIntegrationTests` asserts Marketing row count=5, milestone flags, and activated six-bool + public URL after seed.
4. **L2/L3 e2e** — Titled AC8–AC12 specs expanded; L3 Auth0 ACs use honest `test.skip` without secrets; public legal ACs exercise real API.
5. **Quality scripts** — `check-ac-depth.ps1` frontend root candidates + `check-no-shipped-stubs.ps1` Linux path separators.

**Not in this BE PR (documented):** FE ActivationChecklist / subprocessors page live under `casazen/frontend` (push 403 for bot); contract-check still reports FE client alignment locally.

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Stage 03 targets met for activated formula, Marketing persist, site/booking derivations. **🟡** `consentsAccepted` still weaker than design (“required types at current versions”). |
| Async patterns | Pass — `SaveChangesAsync` / `AnyAsync` / `FirstOrDefaultAsync` with `CancellationToken`; no `.Result`/`.Wait()`/`async void`. |
| EF Core | Pass for Migration Plan — Marketing is enum int; G4 N/A log honest. No N+1 list endpoint. |
| Testing | L1 covers AC1–AC7/AC9–AC10 activation path. **🟡** L2 AC9 title overclaims. L3 Auth0 skips honest. |
| SOLID | Pass — service stays focused; `IConfiguration` injected for public base URL (same pattern as `OrgDomainService`). |

## Focus areas

| Focus | Assessment |
|---|---|
| **Activated formula** | **PASS** — explicit six-bool AND matches `design-271` Stage 03 target; L1 `AC6_AC10_…ActivatedRequiresSixBoolConjunction` asserts true only after property + site + confirmed Direct booking seed. |
| **Marketing consent** | **PASS** — enum + persist when `MarketingOptIn == true`; L1 asserts `ConsentType.Marketing` row. Opt-out path leaves Marketing absent (write gated on `== true`). |
| **Tests honesty** | **Mostly PASS** — L1 real integration; L3 skips Auth0 without inventing PASS; G9b `3 skipped / 2 passed`. **🟡** L2/L3 AC9 titles imply complete POST→dashboard but assert only wizard landing. |
| **No secrets** | **PASS** — no OTA/Stripe/Auth0 secrets in diff; config key `App:PublicSiteBaseUrl` with public default only. |
| **IDOR on status** | **PASS** — `OnboardingController.GetStatus` uses JWT `sub` only; no `OrgId` query/body; service scopes milestones to `user.OrgId`. AC5 unauthenticated → 401. |

## 2. Findings by severity

### 🔴 Critical

_None. (0 critical — required for PASS)_

### 🟡 High

1. **`consentsAccepted` still not design-complete** (`Casazen.Infrastructure/Services/OnboardingService.cs` ~L76–77) — Design: “Required consent types present at **current versions**.” Implementation: `AnyAsync(… Type == ConsentType.Tos)` only (no Privacy/Dpa/SubprocessorsAck, no version match vs `ILegalDocumentService`). Write path always records all four required types atomically, so happy-path activated is OK, but AC5 derivation remains weaker than the contract Stage 03 was supposed to close. Prefer: all four required types present at current legal versions (Marketing optional, excluded from this flag).

2. **L2/L3 AC9 test titles overclaim behavior** (`e2e/onboarding-plg.spec.ts` ~L57–63; `e2e/l3/onboarding-plg-l3.spec.ts` ~L41–47) — Titles say “completing onboarding routes to dashboard” / “POST onboarding then dashboard with org context” but bodies only assert the role-step heading (and stay on `/onboarding`). Rename titles to match assertions, or extend steps to complete wizard + assert dashboard/org. Does not invent gate PASS (L3 Auth0 tests skip), but weakens AC9 honesty.

### 🟢 Medium

1. **Marketing `Version = consents.TosVersion`** (`OnboardingService.cs` ~L59) — No dedicated marketing document version in `ILegalDocumentService`. Acceptable interim; document or introduce a marketing version constant so GDPR evidence is not tied to ToS bumps accidentally.

2. **No negative L1 for `marketingOptIn=false` → 4 rows** — Success test always opts in. Add a symmetric assert that Marketing is absent when opt-in is false/null.

3. **No dedicated cross-tenant IDOR L1** — Implementation is sound (sub→Org only). A second authenticated client asserting status reflects only its Org would lock the contract against future OrgId query params.

### ⚪ Low

1. **Duplicate property existence queries** (`propertyCreated` then `hasActiveProperty`) — Fine for clarity; could collapse later.

## 3. AC matrix verification

| AC | REQ-ID | Evidence | Result |
|---|---|---|---|
| AC1 | SPEC:onboarding-plg:AC1 | L1 `AC1_Post…` / `AC1_AC3_Post…` — Org provision + 400 without consents; delivery-14 G1 pass | **PASS** |
| AC2 | SPEC:onboarding-plg:AC2 | L1 `AC2_Post…StaleConsentVersion_Returns400` | **PASS** |
| AC3 | SPEC:onboarding-plg:AC3 | L1 records 5 ConsentRecords incl. Marketing + IP; enum `ConsentType.Marketing` | **PASS** |
| AC4 | SPEC:onboarding-plg:AC4 | L1 `AC4_LegalEndpoints_AreAnonymous`; L3 AC11 anonymous API | **PASS** |
| AC5 | SPEC:onboarding-plg:AC5 | L1 auth 401 + authenticated status DTO fields; IDOR via sub-only controller | **PASS** (with 🟡 on `consentsAccepted` strictness) |
| AC6 | SPEC:onboarding-plg:AC6 | L1 milestone flags + seed; real `sitePublished` / Confirmed+Direct booking | **PASS** |
| AC7 | SPEC:onboarding-plg:AC7 | L1 `AC7_AC9_Put…DoesNotRequireConsents` | **PASS** |
| AC8 | SPEC:onboarding-plg:AC8 | L2 consents step + Continua disabled; L3 skip without Auth0 (honest) | **PASS** |
| AC9 | SPEC:onboarding-plg:AC9 | L1 PUT path; L2/L3 titled tests shallow vs title (🟡) | **PASS** (shallow L2/L3 — see 🟡) |
| AC10 | SPEC:onboarding-plg:AC10 | L1 activated six-bool + `publicBookingUrl`; L2 checklist + deep-link text; L3 Auth0 skip honest | **PASS** |
| AC11 | SPEC:onboarding-plg:AC11 | L1 legal list; L2/L3 subprocessors Italian UI + ≥4 vendors | **PASS** |
| AC12 | SPEC:onboarding-plg:AC12 | L2 demo onboarding without Auth0; L3 public `/legal/subprocessors` | **PASS** |

**Contract check:** `Sessions/pipeline-onboarding-plg/contract-check.md` overall PASS — endpoints aligned; Marketing recorded on opt-in.

**Gate evidence:** `delivery-14/gates.json` overall=`pass`; G1 tests 793 passed / 0 failed; G9a L2 5 passed; G9b L3 2 passed / 3 skipped (Auth0).

## Status integrity

| Check | Result |
|---|---|
| Invented product PASS | **No** — activated/Marketing/site/booking implemented in BE + L1 |
| Secrets in diff | **None** |
| L3 Auth0 without credentials | **Honest skip** (not fake green assertions) |
| FE write blocked | Documented in PR; not claimed merged to `casazen/frontend` |
| Design Stage 03 gaps from PR #403 | Marketing enum + six-bool `activated` — **closed** |

## Verification performed

```text
git fetch origin develop + feature/271-onboarding-plg
git diff origin/develop...feature/271-onboarding-plg — 8 files, +306/−37
Sessions/design-271.md AC Test Map + API contract (activated six-bool, Marketing migration plan)
Sessions/pipeline-onboarding-plg/contract-check.md — PASS
Sessions/loop/evidence/delivery-14/gates.json — overall=pass
OnboardingController — [Authorize], sub-only GetStatus (no OrgId)
OnboardingService — Marketing persist; sitePublished; Confirmed+Direct; activated AND; publicBookingUrl
PlgOnboardingIntegrationTests — Marketing + six-bool seed assertions
e2e L2/L3 — titled ACs; Auth0 skips; no secrets
gh pr view 404 — Stage 03 PLG; FE 403 noted
```

## 4. Explicit verdict

| Metric | Count |
|---|---|
| 🔴 Critical (open) | **0** |
| 🟡 High (open) | **2** |
| 🟢 Medium | 3 |
| ⚪ Low | 1 |

**Verdict: APPROVE**

Rationale: Stage 03 focus items land correctly — six-bool `activated`, Marketing consent persistence, no secrets, status IDOR posture (JWT `sub` → caller Org). Gate evidence overall=`pass`. Open 🟡 items (`consentsAccepted` version completeness; AC9 e2e title/assertion mismatch) should be tracked as follow-ups; they do not meet 🔴 bar and do not overturn Stage 03 delivery of the documented activation formula.

Security review is out of this agent’s scope. **Do not merge from this agent** (parent/delivery tick owns merge).


## Stage 04 🟡 dispositions (follow-up)

| Finding | Disposition |
|---|---|
| consentsAccepted Tos-only | **Fixed** — requires Tos/Privacy/Dpa/SubprocessorsAck at current legal versions |
| AC9 title overclaim | **Fixed** — L2/L3 titles renamed to match assertions |

**Verdict after patch:** APPROVE (🔴0 🟡0)
