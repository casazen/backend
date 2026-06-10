# Devil's Advocate Review — CasaZen Platform Launch

> **Council**: Platform Launch (builder-validator) · **Phase**: Step 4 (post-consensus adversarial review)
> **Date**: 2026-06-05 · **Reviewer**: Devil's Advocate (`.claude/agents/platform-launch/devils-advocate.md`)
> **Inputs reviewed**: `business-plan.md`, `implementation-roadmap.md`, `decision.md`, `draft-v3.md`, the market anchor, and a sample of `Sessions/specs/` (`spec-direct-checkout`, `spec-saas-billing`, `spec-tenant-boundary`, `spec-ltr-recurring-rent`).
> **Protocol note**: consensus originals are **preserved unchanged**. Narrative amendments live in `*-after-devils-review.md` variants. Concrete **spec** corrections were applied directly to the affected specs (they make the deliverable set correct/complete) and are recorded here.

---

## Verdict

**OBJECT** — 2 blocking, 12 major, 3 minor challenges. The strategy is sound and unusually well-grounded (the verified-codebase re-baseline is its strongest feature), but two gaps make the Phase 1/1.5 "sellable" exit **non-executable as written**, and several headline numbers do not reconcile across documents.

**Consolidation outcome**: 16 accepted, 1 partially accepted (#14), 0 dismissed.

---

## Disposition summary

| # | Severity | Challenge (short) | Disposition | Addressed in |
|---|:---:|---|:---:|---|
| 1 | **blocking** | No spec owns Stripe Connect operator/landlord onboarding (KYC/account creation) | **Accept** | **New** `Sessions/specs/spec-connect-onboarding.md` + roadmap variant (Phase 1 deliverable) + decision variant (US-018) |
| 2 | **blocking** | Alloggiati 24h duty rides on an unproven $0 cron whose upgrade trigger fires at the very event that creates the duty | **Accept** | `implementation-roadmap-after-devils-review.md` (trigger moved earlier + funnel gate) |
| 3 | major | SOM €1.5M ARR does not reconcile with its own inputs (300–400 acct × €275 = €0.99–1.32M) nor with §10 (Y3 400–700 acct) | **Accept** | `business-plan-after-devils-review.md` §2/§10 |
| 4 | major | "30 units/account" is a single-point assumption driving the whole funnel | **Accept** | business-plan variant §2 (sensitivity table) |
| 5 | major | €180–200 ARPU vs €0 freemium: blended vs paid-account ARPU conflated | **Accept** | business-plan variant §6/§9/§10 |
| 6 | major | Phase 1.5 labelled "PARALLEL — depends on neither billing nor multi-tenancy" contradicts RF1/RF3 (OrgId) | **Accept** | roadmap variant Phase 1.5 + RF notes |
| 7 | major | "No competitor couples IT STR + LTR RLI" asserted as fact; it is an [EST] | **Accept** | business-plan variant §1/§3 + decision variant (moat = lead-time) |
| 8 | major | "IVA is a pass-through, not a margin cost" overstated | **Accept** | business-plan variant §9/§11 + decision variant |
| 9 | major | Marketplace fund flow unspecified — MoR? application fee? DAC7? | **Accept** | `decision-after-devils-review.md` (AD-5 extension) + note to `spec-supplier-marketplace` |
| 10 | major | OSS €10k threshold has no monitoring/switchover AC (deferred entirely to counsel) | **Accept** | **Applied** → `spec-saas-billing.md` AC7b |
| 11 | major | Unit economics assume CAC "mostly non-cash"; no funded/paid-CAC scenario | **Accept** | business-plan variant §9 (paid-CAC scenario) |
| 12 | major | `OrgId` migration (nullable→backfill→NOT NULL) has no rollback/orphan-row safety | **Accept** | **Applied** → `spec-tenant-boundary.md` AC10b |
| 13 | major | Anonymous `/search` leaks `OwnerId` today — a live PII/IDOR issue parked in Phase 1 | **Accept** | roadmap variant Phase 0 (security hotfix, pulled forward) |
| 14 | major | "18-month-to-first-cohort" stated firmly with no per-phase basis | **Partial** | business-plan variant §10 ([EST] + indicative per-phase durations; not a committed schedule) |
| 15 | minor | `BookingsController` (plural) — wrong; existing controller is `BookingController` | **Accept** | **Applied** → `spec-direct-checkout.md` (3 refs) |
| 16 | minor | Stripe Connect described as a "dependency" though no deliverable owns it | **Accept** | resolved by #1; roadmap variant lists it as a Phase 1 deliverable |
| 17 | minor | Churn 3%/mo and TAM ~$2B stated without sourced rationale/ranges | **Accept** | business-plan variant §2 (ranges + rationale) |

---

## Detail — blocking

### #1 — Stripe Connect onboarding has no owner (blocking)
**Defect.** `decision.md` AD-5 and the roadmap mandate "operator/landlord = merchant of record via Stripe Connect", and `spec-direct-checkout` AC3 returns `409` if the operator is not onboarded — but **no spec creates the connected account, runs KYC, or populates `Org.StripeConnectedAccountId`**. `spec-tenant-boundary` only *defines* the field.
**Why it matters.** The Phase 1 exit ("takes a commission-free booking, operator = MoR") and the Phase 1.5 rent exit are unreachable; a builder hits the `409` path permanently.
**Fix (accepted).** Created `Sessions/specs/spec-connect-onboarding.md` (Express account create, hosted KYC Account Links, `account.updated` connected-webhook routing, `charges_enabled` charge-gate, landlord parity for LTR). `spec-direct-checkout` now requires it; roadmap variant lists it as a Phase 1 deliverable; decision variant adds **US-018**.

### #2 — Alloggiati duty vs the $0 cron upgrade trigger (blocking)
**Defect.** R5 sets the infra upgrade ($0 → ~€5/mo) at **first real guest check-in** — but check-in is exactly the event that starts the **Alloggiati 24h** clock. You cannot be *proving* the compliance cron at the same instant the legal duty lands; a missed/failed $0-window cron is a regulatory breach, not a demo glitch.
**Why it matters.** The cost-minimization lever directly collides with a hard legal deadline.
**Fix (accepted).** Roadmap variant moves the upgrade trigger **earlier — to the moment a property goes publicly bookable / first confirmed direct booking** (before any check-in can occur), and **gates the public funnel**: a property cannot be published as publicly bookable until either the $0-window GH Actions cron is proven firing Alloggiati + GDPR, or the €5 always-on tier is active.

---

## Detail — major (selected)

- **#3 SOM reconciliation.** `300–400 acct × €275 × 12 = €0.99–1.32M`, not €1.5M; and §10 lists Y3 at **400–700** accounts. Variant aligns §2 to §10: **SOM Y3 ≈ €1.3–2.3M**, conservative core **~€1.5M at ~450 paying accounts (~9% of the 5,000 wedge)**, arithmetic shown.
- **#4 Units/account sensitivity.** Variant adds a 20/25/30/40 units-per-account table; key insight surfaced: **SOM ARR is roughly invariant to the split** (driven by total managed units × per-unit price × capture) while **per-cohort account counts are highly sensitive** — so account-count targets, not ARR, carry the assumption risk.
- **#5 Blended vs paid ARPU.** Variant labels €180–200 as **paid-account** ARPU (correct for LTV/CAC and ARR, which count paying accounts only) and adds the free→paid conversion assumption so freemium accounts are treated as funnel/CAC, not revenue.
- **#6 Phase 1.5 "parallel" contradiction.** True at the *feature* level, false at the *data* level: RF1 (every tenant-scoped table carries `OrgId`) and RF3 (migration order) couple it to `spec-tenant-boundary`. Variant rewords to "**feature/design parallel; DB migrations serialized — Phase 1.5 tables carry `OrgId` from creation and rebase onto the post-Phase-1 EF snapshot.**"
- **#7 Moat overclaim.** Variant retags "no competitor couples IT STR + LTR RLI" as **[EST]** and reframes it as a **lead-time advantage** (12–24 months), not a permanent, asserted-as-fact moat.
- **#8 IVA pass-through.** Variant qualifies: IVA is neutral **only for VAT-registered B2B customers who reclaim** (the core wedge — they hold P.IVA). For *forfettario* hosts / non-VAT / EU-B2C buyers, 22% IVA is a **real ~22% price wedge** to manage, not a non-cost.
- **#9 Marketplace fund flow.** Decision variant specifies: **supplier = MoR** for their own service, Stripe Connect **separate charges / destination charges with `application_fee_amount`** for the platform take-rate (the only take-rate, never on guest bookings), and CasaZen's **DAC7** reporting obligation as a platform operator — flagged **[COUNSEL_REQUIRED]** and pushed into `spec-supplier-marketplace`.
- **#11 Paid-CAC scenario.** Variant adds a funded scenario (e.g. 10 paid accounts/mo × €600 = €6,000/mo cash CAC) showing the **"CAC mostly non-cash" claim is a risk, not a given**, with the runway implication.
- **#13 Anonymous `OwnerId` leak.** This is a **live** PII/IDOR exposure (`GET /api/properties/search` `[AllowAnonymous]` returns the raw `Property` incl. `OwnerId`), not a future feature. Variant pulls a **minimal Phase 0 security hotfix** (strip owner/PII fields from the anonymous response) ahead of the full `spec-public-booking-readmodel`.

---

## Detail — minor

- **#10 / #12 / #15** were applied **directly to the specs** (OSS counter AC7b in `spec-saas-billing`; rollback/orphan-row AC10b in `spec-tenant-boundary`; `BookingController` name fix in `spec-direct-checkout`).
- **#16** is resolved by #1 (Connect now a deliverable). **#17** ranges/rationale added in the business-plan variant §2.

---

## What holds up well

- **Verified-codebase re-baseline (AD-1).** Catching the stale docs (22/16/10 + Supabase + existing LTR/RLI + context-RBAC) is the single best move; it reshaped LTR from greenfield to "complete + verify" and is the plan's credibility anchor.
- **Merchant-of-record discipline (AD-5, C3/C6).** CasaZen never holding guest/tenant funds is correctly and consistently applied (the Connect onboarding gap notwithstanding).
- **Lawful-only cost minimization.** No unlawful tactic; grants correctly flagged as slow/non-month-1; `[COUNSEL_REQUIRED]` markers are honest.
- **AI as a hard product constraint (AD-7).** Fair-use caps tied to a numeric GM floor (≥80%) is the right framing for the one variable-cost line.
- **Spec format consistency.** All specs follow the `spec-property-detail.md` shape, ready for the AI-SDLC pipeline.

---

## Applied changes (this review)

| File | Change |
|---|---|
| `Sessions/specs/spec-connect-onboarding.md` | **Created** (closes blocking #1) |
| `Sessions/specs/spec-direct-checkout.md` | `BookingsController`→`BookingController` (×3, #15); added `spec-connect-onboarding` as required dep (#1) |
| `Sessions/specs/spec-saas-billing.md` | Added **AC7b** OSS threshold counter + switchover (#10) |
| `Sessions/specs/spec-tenant-boundary.md` | Added **AC10b** migration rollback + orphan-row pre-flight + 3-deploy sequencing (#12) |

Spec count: **17 → 18**.

## Narrative amendment variants (originals preserved)

- `business-plan-after-devils-review.md` — #3, #4, #5, #7, #8, #11, #14, #17
- `implementation-roadmap-after-devils-review.md` — #1/#16, #2, #6, #13, #14
- `decision-after-devils-review.md` — #7, #8, #9, US-018, gate additions

---

## Deliberation trail — Devil's Advocate Review

Step 4 executed (operator approved). Reviewer verdict **OBJECT** (2 blocking / 12 major / 3 minor). Coordinator consolidation: **16 accept, 1 partial (#14), 0 dismiss**. Consensus originals unchanged; spec corrections applied; narrative deltas captured in three `*-after-devils-review.md` variants. The two blocking items (Connect onboarding owner; Alloggiati-vs-cron trigger) are resolved before execution.
