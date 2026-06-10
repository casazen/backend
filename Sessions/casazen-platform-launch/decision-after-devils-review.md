# Council Decision Record — CasaZen Platform Launch (after Devil's Advocate review)

> **Overlay, not a rewrite.** The consensus `decision.md` is **preserved unchanged**. This file records the decision-level amendments accepted in `devils-advocate-review.md` (challenges #7, #8, #9, plus the new spec/US and binding-gate additions). Sections marked **SUPERSEDES/EXTENDS** modify the same item in the original; everything else stands.

---

## §3 — User stories (EXTENDS; addresses #1)

Add:

| ID | As a… | I want… | Spec | Phase |
|----|-------|---------|------|:---:|
| **US-018** | operator / long-rent landlord | to onboard my own Stripe (Connect) account with KYC so guest payments / rent settle to me as merchant of record | **`spec-connect-onboarding`** (new) | 1 |

Spec count **17 → 18**. US-002 (`spec-direct-checkout`) and US-007 (`spec-ltr-recurring-rent`) now both **depend on US-018**.

---

## §4 — Architectural decisions (amendments)

### AD-5 — EXTENDS (addresses #1, #9): MoR mechanics made concrete

- **Onboarding owner:** **`spec-connect-onboarding`** owns connected-account creation, Stripe-hosted KYC, and the `charges_enabled` gate that `spec-direct-checkout`/`spec-ltr-recurring-rent` check before any `PaymentIntent`. CasaZen stores only the connected-account id + capability flags.
- **Marketplace fund flow (Phase 3, `spec-supplier-marketplace`) — previously unspecified:**
  - **Supplier is merchant of record** for their own service; the **only platform take-rate** is collected via Stripe Connect **destination/separate charges with `application_fee_amount`** — never a take-rate on guest bookings (consistent with the zero-booking-commission promise, F6).
  - **DAC7:** as an EU digital platform connecting sellers (suppliers) to users, CasaZen likely incurs a **DAC7 reporting obligation** (collect/report seller identity + consideration). Flagged **[COUNSEL_REQUIRED]** and added to `spec-supplier-marketplace` scope.
  - CasaZen never holds supplier funds beyond the Connect flow; no escrow without counsel sign-off.

### AD-9 — NEW (addresses #7): moat is lead-time, not permanent
The "no competitor couples IT STR + LTR RLI" position is **[EST]**, reframed as an estimated **12–24 month lead-time advantage** defended by switching cost — **not** an asserted, uncopyable moat. The two **[FACT]** assets (shipped IT STR compliance; shipped LTR lease+RLI+e-sign) stand; the *competitive negative* is downgraded to [EST]. R1 prices the catch-up risk.

### AD-10 — NEW (addresses #8): IVA neutrality is segment-specific
IVA is a pass-through **only for VAT-registered B2B customers** (the core wedge, who reclaim). For *forfettario*/non-VAT/EU-B2C buyers the 22% is a **real price wedge**, not a non-cost — a pricing/positioning input, especially for the Phase 2 small-host freemium pool.

---

## §5 — Regulatory & financial gates (EXTENDS; binding)

Add to the binding gates:

- **Connect onboarding gate (Phase 1):** an operator's connected account must be **`charges_enabled`** before its branded booking site can be published / take a guest payment (`spec-connect-onboarding`; closes the `spec-direct-checkout` AC3 `409` trap).
- **Infra trigger (REVISED, supersedes the original Infra gate):** upgrade $0 → ~€5/mo at **first publicly-bookable property / first confirmed direct booking** (earlier than first check-in), **and gate the public funnel** on the Alloggiati+GDPR cron being proven (or the €5 tier active). Removes the Alloggiati-24h-vs-cron collision (#2).
- **OSS threshold (engineering, EXTENDS):** the EU-B2C **€10k** OSS switchover is now an explicit build item — a cross-border revenue counter + auto-switch to destination VAT (`spec-saas-billing` **AC7b**), distinct from the `[COUNSEL_REQUIRED]` rates/reporting sign-off (#10).
- **`OrgId` migration safety (engineering, EXTENDS):** tested down-migrations, a pre-flight NULL-`OrgId` check before the NOT-NULL flip, and three separate deploys (nullable → backfill → NOT NULL) for zero-downtime (`spec-tenant-boundary` **AC10b**, #12).
- **Phase 0 security hotfix:** strip `OwnerId`/PII from anonymous `/search` immediately, ahead of `spec-public-booking-readmodel` (#13).

---

## §7 — Deliberation summary (EXTENDS)

Append:

| Round | Reviewer | Verdict | Outcome |
|:---:|:---:|:---:|---|
| 4 (post-consensus) | Devil's Advocate | **OBJECT** (2 blocking / 12 major / 3 minor) | 16 accept · 1 partial (#14) · 0 dismiss → 4 spec changes (1 new) + 3 narrative variants |

Consensus (Round 3) is unchanged; the Devil's Advocate pass hardened executability (Connect onboarding owner; Alloggiati/cron trigger) and reconciled headline numbers without reopening the strategy.

---

## Deliberation trail — Devil's Advocate Review

Decision-level amendments #7 (AD-9), #8 (AD-10), #9 (AD-5 marketplace + DAC7), US-018, and four binding-gate additions accepted; consensus `decision.md` preserved unchanged. Full disposition: `devils-advocate-review.md`.
