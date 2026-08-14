# CasaZen — Business Plan (after Devil's Advocate review)

> **Overlay, not a rewrite.** The consensus `business-plan.md` is **preserved unchanged**. This file records the amendments accepted in `devils-advocate-review.md` (challenges #3, #4, #5, #7, #8, #11, #14, #17). Where a section here is marked **SUPERSEDES**, it replaces the same-numbered section of the original; everything else in the original still stands.
> **Sourcing**: **[FACT]** / **[EST]** as in the original. **[COUNSEL_REQUIRED]** unchanged.

---

## §2 — Market view (SUPERSEDES §2; addresses #3, #4, #17)

### #3 — SOM reconciled (arithmetic now consistent with §10)

The original SOM (`~€1.5M ARR` from "300–400 accounts × €275") did not multiply out (`300–400 × €275 × 12 = €0.99–1.32M`) and conflicted with §10 (Y3 = 400–700 accounts). Reconciled:

| Layer | Figure (corrected) | Basis |
|-------|--------------------|-------|
| **SOM (Y3)** | **€1.3–2.3M ARR [EST]** | 400–700 paying accounts × €275/mo × 12 — **aligned to §10** |
| **SOM (Y3) conservative core** | **~€1.5M ARR [EST]** | **~450 paying accounts** (~9% of the ~5,000 wedge) × €275 × 12 |

The earlier "6–8% capture → €1.5M" line is withdrawn: €1.5M needs **~9% capture at €275**, or 6–8% capture only reaches **~€1.0–1.3M**. Headline SOM is now the €1.3–2.3M band with a ~€1.5M conservative anchor.

### #4 — Units-per-account sensitivity (the "30 units" assumption)

"~30 units/account" is a single point. Holding ~150,000 professionally-managed units and €6/unit/mo (Pro midpoint):

| Units/account | Wedge accounts (≈150k ÷ u) | Pro ARPU @ €6/unit | SOM core (≈9% capture × €/acct × 12) |
|:---:|:---:|:---:|:---:|
| 20 | ~7,500 | €120 | ~€0.97M |
| 25 | ~6,000 | €150 | ~€0.97M |
| **30** | **~5,000** | **€180** | **~€0.97M** |
| 40 | ~3,750 | €240 | ~€0.97M |

**Key insight surfaced by the review:** SOM ARR is **roughly invariant** to the units/account split (it is total managed units × per-unit price × capture). What *is* sensitive is the **account count** behind every cohort target in §10 — so the assumption risk lives in *account-count* milestones (and therefore CAC totals), not in ARR. Cohort targets in §10 should be read as "±1 band" depending on the true average.

### #17 — Churn and TAM stated as ranges with rationale

- **Churn 3%/mo [EST]** → ~33-month average life. Rationale: SMB-SaaS monthly logo churn typically **2–5%/mo**; CasaZen sits mid-range pre-stickiness and should improve toward **2%/mo** once compliance + direct revenue create switching cost (the §9 LTV upside already models 2%). Treat 3%/mo as the **planning midpoint of a 2–5% band**, not a measured figure; instrument actual churn from the first cohort.
- **TAM ~$2B (2025) → $4–5B (2033) [EST]** → the **$2B** anchor is VR-software (F2, ~$1.5B→$3.2B) **[FACT]** plus an LTR-adjacency uplift **[EST]**; the LTR portion is the soft part of the range. Carry TAM as a **band ($1.5–2.5B today)**, not a point.

---

## §6 / §9 / §10 — ARPU defined precisely (addresses #5)

The €0 freemium Starter and the €180–200 ARPU coexist only if "ARPU" is defined. Amendment:

- **Paid-account ARPU [EST]** = €180–200 at launch → €275 by Y3. This is the figure used in **§9 unit economics (LTV/CAC)** and in **§10 ARR** — both of which count **paying accounts only**. Freemium Starter accounts contribute **€0 revenue** and are treated as **funnel/CAC**, never as ARPU or ARR. No double counting.
- **Free→paid conversion [EST]**: assume **~15–25%** of activated freemium Starters convert to a paid tier within 6–12 months; below ~15% the PLG motion (§8) must be reinforced by the agency/advisor channel. This conversion — not a blended ARPU — is the freemium KPI.
- Headline "ARPU" anywhere in this plan = **paid-account ARPU** unless explicitly written "blended".

---

## §1 / §3 — Moat reframed as lead-time, not asserted fact (addresses #7)

The claim "**No competitor couples native Italian STR compliance with a native LTR lease+RLI engine**" is downgraded from an implicit [FACT] to **[EST]** (it asserts a negative about every competitor's roadmap, which cannot be verified). Reframe:

> CasaZen's STR-compliance + LTR-lease/RLI combination is, to our knowledge, **not currently offered together by the named incumbents [EST]**, giving an estimated **12–24 month lead-time advantage** — a head start to defend with switching cost (compliance lock-in, direct-revenue dependence, community/advisor relationships), **not a permanent, uncopyable moat**. R1 in §13 already prices the "incumbents add it" risk.

The two genuinely hard-to-copy assets remain **[FACT, verified codebase]**: the shipped IT STR compliance stack and the shipped LTR lease+RLI+e-sign engine.

---

## §9 / §11 — IVA pass-through qualified (addresses #8)

"IVA is a pass-through, not a margin cost" is true **only for VAT-registered B2B customers who reclaim input VAT** — which is most of the **wedge** (PM agencies / professional hosts hold a P.IVA). Qualified statement:

- **VAT-registered B2B customers (core wedge):** 22% IVA is **neutral** (they deduct it) → pass-through holds; it is not a margin cost to CasaZen.
- **Forfettario hosts, private/non-VAT customers, EU-B2C:** they **cannot reclaim** → the 22% is a **real ~22% effective price increase** to them — a **competitiveness wedge** to manage in pricing/positioning (esp. for the Phase 2 small-host freemium pool, which skews non-VAT). This nuance feeds the §6 freemium-tier price point and the §11 vehicle decision.

---

## §9 — Paid-CAC scenario added (addresses #11)

The original economics assume CAC is "mostly non-cash". Added as an explicit, funded sensitivity (the assumption is a **risk**, not a given):

| Scenario | CAC | New paid accts/mo | Monthly cash CAC out | Note |
|---|:---:|:---:|:---:|---|
| **Bootstrap (plan baseline)** | €300–800, mostly non-cash | 3–6 | ~€0–1,500 | Demand-constrained; cash is not the binding limit |
| **Funded growth** | **€600 cash** | **10** | **€6,000/mo** | Each acct = ~4 months of €153 contribution to repay; needs funded runway |
| **Paid + founder salary** | €600 cash + ~€2,650/mo draw | 10 | ~€8,650/mo total | Moves cash breakeven well beyond the "1–2 accounts" figure |

**Implication:** the "cash breakeven ~1–2 accounts" headline holds **only** under the founder-unpaid, non-cash-CAC baseline. Any shift to paid acquisition or a founder salary must be funded; track **CAC payback < 12 months** and **NRR ≥ 100%** as the guardrails before scaling spend.

---

## §10 — Five-year horizon marked provisional (addresses #14, partial)

The "**18-month-to-first-cohort**" horizon is relabelled **[EST] planning assumption, not a committed schedule**. Indicative, non-binding per-phase durations (solo/small team) to make it falsifiable:

| Phase | Indicative duration **[EST]** | Gates it depends on |
|---|:---:|---|
| Phase 0 (baseline + security hotfix + docs) | ~2–4 weeks | — |
| Phase 1 (direct booking + Org + billing + Connect onboarding) | ~3–5 months | P.IVA + SDI live; Connect onboarding |
| Phase 1.5 (LTR complete + verify) — parallel | ~2–3 months | rebase after Phase 1 `OrgId` migration |
| → **First paid cohort** | **~6–9 months elapsed [EST]** | Phase 1 exit met |
| Phases 2–4 | post-cohort, demand-led | — |

These are **estimates for sequencing sanity**, not promises; actuals depend on counsel turnaround (`[COUNSEL_REQUIRED]` pack) and solo-founder throughput.

---

## Deliberation trail — Devil's Advocate Review

Amendments #3, #4, #5, #7, #8, #11, #14 (partial), #17 accepted and recorded here; consensus `business-plan.md` preserved unchanged. Full disposition: `devils-advocate-review.md`.
