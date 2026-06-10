# CasaZen — Business Plan

> **Council**: Platform Launch (builder-validator) · **Status**: consensus (Round 3, all validators APPROVE) · **Date**: 2026-06-05
> **Source draft**: `draft-v3.md` · **Market anchor**: `Sessions/market-analysis-2026/AI-short/long-term-platform.md`
> **Sourcing**: **[FACT]** = project doc or Coordinator-verified codebase; **[EST]** = estimate with stated assumption (challenge these first).
> **Disclaimer**: Strategic guidance, not legal/tax advice. Items marked **[COUNSEL_REQUIRED]** need qualified Italian/EU counsel + commercialista before execution.

---

## 1. Executive summary

**Vision.** CasaZen is the **democratic rental operating system for Europe's independent operators** — one subscription platform running the *entire* rental lifecycle (short-term **and** long-term), with **commission-free direct booking** at the center and an **AI copilot** across pricing, guest messaging, and operations. Launch market: **Italy**, where regulatory pain is sharpest and CasaZen already holds its deepest moat.

**Wedge (F7).** Property managers running **10–200 units in Italy** who today stitch together a channel manager + website builder + spreadsheets + manual police/tax compliance. CasaZen replaces that stack with one OS and lets them keep the 15–20% OTA commission (F3) via direct booking.

**Three differentiation axes (all market-analysis-anchored):**
1. **Democratic scale, 1 → 500 units (F5, F8)** — one product a 2-unit host and a 200-unit agency both grow inside, vs enterprise-heavy Guesty/Hostaway and host-only tools that cap out early.
2. **AI copilot across the full lifecycle (F4, F5)** — not a bolt-on pricing widget: pricing (shipped), guest messaging, content, operations — directly attacking booking-window compression (F4).
3. **Transparent subscription, zero booking commission (F3, F6)** — per-unit/portfolio pricing, no take-rate on the operator's own bookings.

**Local moat (verified, stronger than first assumed).** CasaZen already enforces native Italian compliance — CIN (D.L. 145/2023), Alloggiati Web, tourist tax, GDPR — **and already ships a long-term lease subsystem** (lease creation → e-signature → RLI registration via Openapi.it Docuengine → receipt) plus **context-scoped RBAC** for short-rent vs long-rent layers **[FACT, verified codebase]**. No competitor couples native Italian STR compliance with a native LTR lease+RLI engine. **The STR+LTR "native both" position in F5 is already substantially real — not a roadmap promise.**

---

## 2. Market view — TAM / SAM / SOM

Global figures **[FACT]** (F1, F2); geographic/unit decompositions **[EST]**.

| Layer | Definition | Figure | Basis |
|-------|-----------|--------|-------|
| **TAM** | Global SMB rental-ops software (STR + small-LTR) | ~$2B (2025) → ~$4–5B (2033) **[EST]** | VR-software $1.5B→$3.2B (F2) **[FACT]** + LTR adjacency **[EST]** |
| **SAM** | IT+ES+FR, 1–500 units, STR+LTR | ~€150–250M/yr **[EST]** | Italy bottom-up, scaled |
| **SOM (Y3)** | Italy-first, wedge, 3-yr | ~€1.5M ARR **[EST]** | Italy wedge capture |

**Italy bottom-up (all [EST]):** ~600,000 active STR units (CIN era) · ~25% professionally managed → ~150,000 units · ~30 units/wedge account → **~5,000 wedge PM accounts**; plus ~200,000+ small hosts (1–9 units) for Phase 2 freemium. SOM Y3 = capture ~6–8% of 5,000 wedge accounts ≈ 300–400 paying accounts → **≈ €1.5M ARR** at the €275 Y3 ARPU.

**Trends (F1–F4).** Every point shifted OTA→direct is pure operator margin (F3), and CasaZen takes none of it (F6). 27% of bookings fall within 0–7 days (F4) → AI-assisted inbox response is a conversion lever. Software layer growth ~9.2% (F2) vs lodging ~3.7% (F1) = an active re-tooling/switching window.

---

## 3. Competitive landscape

Names per analysis/brief (F5); capability reads **[EST]**.

| Competitor | Strength | Gap CasaZen exploits |
|-----------|----------|----------------------|
| **Lodgify** | Direct-booking site builder + channel manager, SMB-friendly | Shallow PM ops; no native IT compliance; STR-only, no LTR; AI limited |
| **Guesty** | Enterprise PMS, deep features | Priced/sold for the top — *not democratic* (F5); STR-only; no native IT regulatory automation |
| **Hostaway** | Mid-market channel manager + automations | Sales-led, higher entry; STR-only; AI nascent; no native IT legal stack |
| **Smoobu** | Affordable SMB channel manager + simple site | Lighter automation/AI; STR-only; shallow compliance; no LTR |

**The gap, precisely (F5 + verified codebase):** no incumbent is simultaneously democratic (1–500), **STR+LTR-native** (CasaZen already has the LTR lease+RLI engine), **AI-copilot-driven**, **direct-booking-first / zero booking commission**, and **natively Italian-compliant**. CasaZen owns the two least-copyable quadrants (Italian STR compliance + Italian LTR lease/RLI) **today**.

**Porter (lightweight) [EST]:** rivalry high in generic STR PMS but **low in the STR+LTR+IT-compliance intersection**; buyer power moderate (sticky once compliance + direct revenue land); **supplier power = OTA APIs** (mitigated by direct shift, F3); new entrants face a real Italian-regulatory barrier; substitutes = the spreadsheets/point tools CasaZen replaces.

---

## 4. Customer segments

| Focus | Segment | Why | Source |
|-------|---------|-----|--------|
| Phase 1 | **PM 10–200 units, Italy** | Highest pain, clear ARPU, fastest direct-booking ROI | F7 **[FACT]** |
| Phase 2 | **Hosts 1–9 units** | Large pool, freemium/PLG entry, compliance hook | domain-context **[FACT]** |
| Later | **Small hotels / boutique B&B** | Adjacent unified-ops + direct demand, Scale tier | F8 ceiling **[EST]** |

---

## 5. Product tiers (F8)

| Capability | Starter (1–3) | Pro (4–100) | Scale (50–500+) | Status |
|-----------|:---:|:---:|:---:|--------|
| Unit management | ✓ | ✓ | ✓ | shipped |
| OTA sync (6 channels) | 2 ch | ✓ 6 | ✓ 6 + priority | shipped |
| IT compliance (CIN/Alloggiati/tax) | ✓ | ✓ | ✓ | shipped |
| GDPR tools | ✓ | ✓ | ✓ | shipped |
| AI dynamic pricing | preview | ✓ | ✓ + portfolio | shipped |
| Stripe payments | ✓ | ✓ | ✓ | shipped |
| LTR lease + e-sign + RLI | add-on | ✓ | ✓ | shipped (verify Ph 1.5) |
| Context-RBAC layers | ✓ | ✓ | ✓ | shipped |
| Direct-booking site + checkout | ✓ basic | ✓ branded | ✓ multi-brand | Phase 1 |
| Recurring-rent ledger (LTR) | add-on | ✓ | ✓ | Phase 1.5 |
| Unified inbox | — | ✓ | ✓ + SLA routing | Phase 2 |
| AI copilot messaging | trial caps | ✓ | ✓ (fair-use caps) | Phase 2 |
| Org seats / collaboration | 1 seat | up to 10 | unlimited + SSO | Phase 2 |
| Supplier marketplace | browse | ✓ transact | ✓ preferred | Phase 3 |
| Google Vacation Rentals | — | ✓ | ✓ | Phase 3 |
| Multi-brand / enterprise SLA | — | — | ✓ | Phase 4 |

---

## 6. Pricing model (F6)

**Principles:** subscription per unit/portfolio; **no booking commission** on operator bookings.

| Tier | Price **[EST]** | Logic |
|------|-----------------|-------|
| **Starter** | €0 freemium → €19–29/mo (1–3 units) | PLG entry; converts hosts |
| **Pro** | **€4–8 / unit / mo, banded** | Core revenue |
| **Scale** | custom, declining per-unit + SLA | 50–500+ units |

**Reconciliation (Financial):** Pro €4–8/unit × ~30 units ≈ €120–240/mo → midpoint ~€180 = **launch/SOM-entry ARPU €180–200 [EST]**. As accounts add units and Scale mix grows, ARPU drifts to **€275 by Y3 [EST]**, staying inside the **€150–400 benchmark (F9) [FACT]**.

---

## 7. Revenue mix target (F10)

| Stream | Share | Notes |
|--------|:---:|-------|
| SaaS subscription | **70–80%** | Core |
| Marketplace | **10–20%** | Supplier marketplace take-rate (Phase 3) — the only take-rate, never on guest bookings |
| Services | remainder | Onboarding, migration (Lodgify/Smoobu), premium support |

---

## 8. Go-to-market

| Channel | Motion | Anchor |
|---------|--------|--------|
| **PLG** | Freemium Starter + self-serve onboarding; activation = time-to-first-direct-booking | F7 |
| **Content & community** | Italian compliance content (CIN/Alloggiati/tourist-tax + **RLI/cedolare** for LTR) as lead magnet | compliance + LTR moat |
| **Agency & advisor partnerships** | PM agencies + *commercialisti* (referral, esp. LTR/cedolare) | **[EST]** |
| **Google Vacation Rentals** | Direct-booking distribution bypassing OTA commission | F3 |

---

## 9. Unit economics (Financial Strategist)

- **Burn:** Phase 0 ~€0–15/mo; Phase 1 all-in ~€65–200/mo (infra + commercialista + e-invoicing) **[EST]**.
- **Cash breakeven (founder unpaid): ~1–2 accounts.** Ramen-profitable (~€2,650/mo) ≈ 14–22 accounts. The binding constraint is demand/execution, not cash.
- **Contribution/account:** €180 ARPU × ~85% gross margin ≈ €153/mo.
- **LTV** (€180 ARPU, 85% GM, ~3%/mo churn ≈ 33-mo life) ≈ **€5,100 [EST]**; improves to ~€11,700 at €275 ARPU / 2% churn.
- **CAC ceiling** (LTV:CAC ≥ 3:1) ≈ **≤€1,700**; bootstrap blended CAC **€300–800 [EST]** → LTV:CAC ~6–17×.
- **Payback** at €600 CAC / €153 contribution ≈ **~4 months** (hard cap <12). CAC must be mostly non-cash; track **NRR ≥ 100%** to defend LTV against early churn.
- **Gross margin:** no per-booking COGS (subscription, no take-rate). AI is the only swing → hard fair-use caps keep **AI ≤10–15% of ARPU, GM ≥80%**.

---

## 10. Five-year vision (targets **[EST]**)

| Horizon | Paying accounts | ARPU/mo | ARR | Geography |
|---------|:---:|:---:|:---:|-----------|
| **Y1 (first paid cohort)** | 40–60 | €180–200 | floor ≈ €90k (€90–140k) | Italy |
| Y2 | 200–350 | €230 | ~€0.6–1.0M | Italy |
| Y3 | 400–700 | €275 | ~€1.3–2.3M | Italy + ES pilot |
| Y4 | 900–1,500 | €310 | ~€3.3–5.6M | IT+ES+FR |
| Y5 | 1,800–3,000 | €340 | ~€7.3–12.2M | EU SMB |

Planning **[EST]**, not forecast. Y1 ARR floor ≈ €90k = ~42 accounts × €180/mo × 12. 18-month-to-first-cohort horizon ⇒ Y1 is a partial year.

---

## 11. Company formation & tax — decision tree (Legal C1) **[COUNSEL_REQUIRED]**

**Correction:** *regime forfettario* applies **only to an individual / ditta individuale** — it **cannot** apply to an SRLS or SRL (always IRES + IRAP, 22% IVA, mandatory SDI e-invoicing).

| Option | Vehicle | Tax/IVA | Liability | Grants/hiring/EU-B2B | Verdict **[EST]** |
|--------|---------|---------|-----------|----------------------|---------|
| **A** | Ditta individuale, forfettario | No IVA (if eligible), flat substitute tax | **Unlimited** | Poor | Cheapest; poor fit for a venture |
| **B** | **SRLS (€1 capital)** | IRES/IRAP + 22% IVA + SDI | **Limited** | Good — esp. *startup innovativa* | **Recommended start** if limited liability needed early |
| **C** | SRL | IRES/IRAP + 22% IVA + SDI | Limited | Best (investment-ready) | Start as / convert to SRL when raising |

**Position [EST]:** SRLS/SRL is the more coherent vehicle given grant ambitions (Invitalia, PNRR, regional innovation funds — esp. as *startup innovativa*), hiring, and EU-B2B reverse-charge sales — accepting 22% IVA + SDI as the cost of doing business (Financial confirms IVA is a pass-through, not a margin cost). This gates `spec-saas-billing`.

---

## 12. Lawful cost-minimization levers (Legal — all confirmed lawful)

| Lever | Notes |
|-------|-------|
| SRLS €1 capital | Limited liability at minimal capital |
| Regime forfettario | **Individuals only** — not for SRLS/SRL |
| Freemium infra ($0→€5) | With the pre-real-guest-check-in upgrade guardrail (R5) |
| Open-source .NET/React | License-compliant |
| Invitalia Smart&Start / PNRR / regional | Genuine eligibility + documented use-of-funds; **loan/tax-offset, slow (3–12 mo) — not month-1 infra** |
| R&D tax credit (credito d'imposta R&S) | Documented dev costs + certification; F24 offset |
| *Startup innovativa* status | Unlocks Smart&Start + tax benefits |
| EU-B2B reverse charge | Lawful cash-flow benefit |

**No unlawful tactic proposed or accepted.** Nothing evades IVA, skips mandatory registration, or misrepresents compliance.

---

## 13. Risk register

| # | Risk | Type | L/I **[EST]** | Mitigation |
|---|------|------|:---:|-----------|
| R1 | Incumbents add IT compliance + AI | Competitive | Med/High | Move fast on direct booking; community + advisor lock-in; **LTR lease/RLI breadth they lack** |
| R2 | OTA API/ToS change | Tech/Supplier | Med/High | Direct shift (F3); Polly resilience shipped; abstract via `IChannelAdapter` |
| R3 | CIN/Alloggiati/tourist-tax change | Regulatory | High/Med | Rates DB-driven, never hardcoded; → Legal |
| R4 | EU AI Act / DAC7 / city short-let bans | Regulatory | Med/Med | Operational AI = limited risk; AI Act disclosure; pricing confidence logged; → Legal |
| R5 | $0 infra misses check-in-driven compliance jobs | Tech | Med/High | **Trigger = first real guest check-in** → ~€5/mo Railway Hobby; **GH Actions cron firing Alloggiati+GDPR must be PROVEN, not assumed** |
| R6 | AI cost/quality erodes margin | Tech/Fin | Med/Med | **Hard fair-use caps** (≤10–15% ARPU, GM ≥80%) |
| R7 | PLG fails to convert hosts | Market | Med/Med | Compliance as freemium hook; activation = first direct booking; agency backstop |
| R8 | LTR mis-framed as unattended tax filing | Regulatory | Med/High | RLI rescoped to **operator-attended/assisted**; CasaZen ≠ *intermediario abilitato* |

---

## 14. Next steps

1. Finalize entity/regime with a commercialista (§11) and, if pursuing grants, register as *startup innovativa*.
2. Stand up the Phase 1 legal pre-requisites (P.IVA + ATECO + **SDI e-invoicing live**) before the first SaaS charge.
3. Begin Phase 1 + parallel Phase 1.5 implementation per `implementation-roadmap.md`, feeding macro-specs into the AI-SDLC pipeline.
4. Prove the $0-window GH Actions cron fires Alloggiati + GDPR endpoints; set the €5/mo upgrade at first real guest check-in.
5. Engage counsel on the **[COUNSEL_REQUIRED]** pack (ToS/SLA, DPA + subprocessors, AI-Act disclosure, RLI delega, rent-receipt tax).
