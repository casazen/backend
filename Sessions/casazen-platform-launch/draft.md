# CasaZen Platform Launch — Integrated Draft (Round 1)

> **Author**: GTM Strategist (Builder) · **Pattern**: builder-validator · **Round**: 1
> **Status**: PROPOSE — for challenge by Product Architect, Legal & Compliance, Financial Strategist
> **Topic**: Go-to-market + product roadmap for CasaZen as an AI-powered direct booking & rental OS (STR + LTR), from current codebase to sellable production platform.

---

## 0. Evidence base, sourcing, and conventions

### 0.1 Sourcing convention

| Tag | Meaning |
|-----|---------|
| **[FACT]** | Taken directly from a project document (market analysis data points, `BUSINESS.md`, `PROJECT.md`, `TECHNICAL.md`, hosting decision, existing specs). |
| **[EST]** | Inference / estimate made by the builder. Every EST states its assumption. Validators should challenge these first. |

### 0.2 Important sourcing caveat (flag for Coordinator)

The mandatory primary evidence file `Sessions/market-analysis-2026/AI-short/long-term-platform.md` **was not found on disk** at draft time. Its key data points are, however, relayed verbatim in `councils/casazen-platform-launch/domain-context.md` → section `market-landscape`, which explicitly cites that file as its source. **All market-analysis [FACT] claims in this draft are anchored to that relayed `market-landscape` table.** Action requested: Coordinator to confirm the file's location/restore it so validators can verify primary figures. Until then, treat market-size figures as document-relayed facts, not independently re-derived.

### 0.3 Market-analysis anchor facts (the spine of this draft)

All sourced from `domain-context.md › market-landscape` (relaying the market analysis):

| # | Anchor fact | Used for |
|---|-------------|----------|
| F1 | STR market ~$101.7B (2025) → $121.9B (2033), CAGR 3.7% **[FACT]** | Market view, demand stability |
| F2 | Vacation-rental software ~$1.5B (2024) → $3.2B (2033), CAGR ~9.2% **[FACT]** | TAM anchor (software, not lodging) |
| F3 | Direct booking ~29% vs 71% OTA (15–20% commission) **[FACT]** | Core wedge: commission-free direct |
| F4 | Last-minute bookings: 27% within 0–7 days (2026) **[FACT]** | Booking-window compression → AI + inbox value |
| F5 | Competitive gap: no "democratic" OS 1–500 units with native STR+LTR + AI copilot + direct booking **[FACT]** | Positioning / differentiation |
| F6 | Positioning: subscription per unit/portfolio, transparent, no hidden booking commission **[FACT]** | Pricing model |
| F7 | MVP wedge: PM 10–200 units, IT/ES/FR, direct booking + unified inbox + AI operational copilot **[FACT]** | Segment + roadmap Phase 1 |
| F8 | Tiers: Starter (1–3) → Pro (4–100) → Scale/Enterprise (50–500+) **[FACT]** | Product tiers |
| F9 | ARPU benchmark €150–400/mo per account (10–100 units) vs Lodgify/Guesty **[FACT]** | Pricing benchmark |
| F10 | Revenue mix target 70–80% SaaS, 10–20% marketplace, rest services **[FACT]** | Revenue model |

---

# A. Business Plan

## A.1 Executive summary

**Vision.** CasaZen becomes the **democratic rental operating system for Europe's independent operators** — one subscription platform that runs the *entire* rental lifecycle (short-term and long-term), puts **commission-free direct booking** at the center, and embeds an **AI copilot** across pricing, guest messaging, and operations. We start where the pain and the regulation are sharpest: **Italy**.

**Wedge** (F7). Property managers running **10–200 units in Italy** who today stitch together a channel manager + a website builder + spreadsheets + manual police/tax compliance. CasaZen replaces that stack with one OS and gives them a direct-booking channel that keeps the 15–20% OTA commission (F3) in their pocket.

**Three differentiation axes (all anchored to the market analysis):**

1. **Democratic scale, 1 → 500 units (F5, F8).** One product that a 2-unit host and a 200-unit agency both grow inside — versus enterprise-heavy Guesty/Hostaway (priced/sold for the top) and host-only tools that cap out early.
2. **AI copilot across the *full* lifecycle (F5, F4).** Not a bolt-on pricing widget: pricing (already shipped), guest messaging, content, and operations — directly attacking booking-window compression (F4) where fast, smart responses win the booking.
3. **Transparent subscription, zero booking commission (F3, F6).** Per-unit/portfolio pricing, no take-rate on the operator's own bookings — the structural opposite of the OTA model and of PMS tools that quietly meter bookings.

**Local moat (codebase-grounded, beyond the analysis).** CasaZen already enforces **native Italian compliance** — CIN (D.L. 145/2023), Alloggiati Web police reporting, municipality tourist tax, GDPR retention/erasure **[FACT: `BUSINESS.md`, `PROJECT.md`]**. None of the four main competitors automate the Italian regulatory stack natively. This is a defensible, hard-to-copy local wedge that the global market analysis does not capture.

## A.2 Market view — TAM / SAM / SOM

> Global lodging/software figures are **[FACT]** (F1, F2). All *geographic decompositions and unit counts are* **[EST]** with stated assumptions, because the relayed analysis gives global figures, not an Italy bottom-up.

**Headline demand context (F1, F2):** the underlying STR lodging market is large and stable (CAGR 3.7%), while the *software* layer we sell into is the fast part (CAGR ~9.2%). We are selling **picks-and-shovels into a growing operator base**, not betting on lodging-demand growth.

| Layer | Definition | Figure | Basis |
|-------|-----------|--------|-------|
| **TAM** | Global SMB rental-operations software (STR + small-LTR), the category an STR+LTR direct-booking OS can address | **~$2B (2025) → ~$4–5B (2033) [EST]** | Anchored on global VR-software $1.5B→$3.2B (F2) **[FACT]**, plus **[EST] +40–50%** LTR/small-landlord management-software adjacency |
| **SAM** | IT + ES + FR, operators of 1–500 units, STR+LTR, reachable by a subscription OS | **~€150–250M/yr [EST]** | Bottom-up below, scaled IT→IT+ES+FR |
| **SOM (Y3)** | Italy-first obtainable, wedge segment, 3-year horizon | **~€1.5M ARR [EST]** | Italy wedge capture below |

**Italy bottom-up (all [EST], assumptions explicit):**

- Active Italian STR units in the CIN era: **~600,000 [EST]** (assumption: CIN national registrations are in the high-hundreds-of-thousands and rising; needs Financial validator sanity-check).
- Professionally managed share (the 10–200-unit wedge): **~25% → ~150,000 units [EST]**.
- Avg units per wedge PM account: **~30 [EST]** → **~5,000 Italian wedge PM accounts [EST]**.
- Plus a secondary pool of **~200,000+ small hosts (1–9 units) [EST]** for Phase 2 freemium.
- ARPU midpoint **€275/mo (≈ €3,300/yr)** = midpoint of the €150–400 benchmark (F9) **[FACT band, EST midpoint]**.
- **SOM Y3 [EST]:** capture ~6–8% of 5,000 wedge accounts ≈ **300–400 paying accounts** → **≈ €1.0–1.3M ARR** from PMs, plus host-freemium upsell → **≈ €1.5M ARR [EST]**.

**Key trends driving the wedge (F3, F4):**

- **29% direct vs 71% OTA at 15–20% commission (F3)** — every point shifted to direct is pure operator margin; CasaZen's direct engine monetizes this without a take-rate (F6).
- **27% of bookings within 0–7 days (F4)** — booking-window compression rewards instant, AI-assisted responses and live multi-channel availability; this is the functional case for the unified inbox + AI copilot.
- **Software layer growing ~9.2% (F2)** while lodging grows ~3.7% (F1) — operators are *re-tooling*, i.e. a switching window CasaZen can exploit now.

## A.3 Competitive set — the specific gap CasaZen fills

> Competitor *names* are from the analysis/role brief (F5). Capability characterizations are **[EST]** market knowledge and should be pressure-tested by the Product Architect.

| Competitor | Core strength | Gap CasaZen exploits |
|-----------|---------------|----------------------|
| **Lodgify** | Direct-booking website builder + channel manager, SMB-friendly | Shallow PM ops & automation; no native Italian compliance (CIN/Alloggiati/tourist tax); STR-only, no LTR; AI limited **[EST]** |
| **Guesty** | Enterprise/pro PMS, deep features | Built and priced for the top of the market — *not democratic* (F5); opaque/high pricing; STR-only; no native Italian regulatory automation **[EST]** |
| **Hostaway** | Mid-market channel manager + automations | Sales-led, higher entry; STR-only; AI nascent; no native Italian legal stack **[EST]** |
| **Smoobu** | Affordable SMB channel manager + simple website | Lighter automation/AI; STR-only; limited compliance depth; no LTR **[EST]** |

**The gap, stated precisely (F5 + codebase):** *No incumbent offers a democratic (1–500 unit) OS that is simultaneously **STR+LTR-native**, **AI-copilot-driven across the lifecycle**, **direct-booking-first with zero booking commission**, and **natively compliant with Italian rental law**.* CasaZen already owns the hardest, least-copyable quadrant (Italian compliance) and is positioned to add the rest by roadmap.

**Porter five forces (lightweight) [EST]:**

- *Rivalry*: high among STR PMS, **but low in the STR+LTR+IT-compliance intersection** (the wedge).
- *Buyer power*: moderate — operators are price-sensitive but locked into painful manual compliance; switching is sticky once compliance + direct revenue land.
- *Supplier power*: **OTA APIs are the key dependency/risk** (ToS, rate limits) — mitigated by direct-booking shift (F3).
- *New entrants*: moderate (SaaS is buildable) but **Italian regulatory depth is a real barrier**.
- *Substitutes*: spreadsheets + point tools — exactly what we replace.

## A.4 Customer segments (phased)

| Phase | Segment | Why them | Source |
|-------|---------|----------|--------|
| **Phase 1** | **Property managers, 10–200 units, Italy** | Highest pain (compliance + multi-channel + manual ops), clear ARPU, fastest path to direct-booking ROI | F7 **[FACT]** |
| **Phase 2** | **Hosts, 1–9 units** | Large pool, freemium/PLG entry, compliance is scary to them → onboarding wedge | domain-context stakeholders **[FACT]** |
| **Phase 3** | **Small hotels / boutique B&Bs** | Adjacent demand for unified ops + direct booking; higher ARPU, needs Scale tier | F8 tier ceiling **[EST]** |

## A.5 Product tiers — feature matrix

Tiers per F8 (Starter 1–3 / Pro 4–100 / Scale 50–500+). Matrix maps **current [FACT]** vs **roadmap (phase)** features.

| Capability | Starter (1–3) | Pro (4–100) | Scale (50–500+) | Status |
|-----------|:---:|:---:|:---:|--------|
| Property/unit management | ✓ | ✓ | ✓ | **[FACT] shipped** |
| OTA channel sync (6 channels) | 2 channels | ✓ all 6 | ✓ all 6 + priority | **[FACT] shipped** |
| Italian compliance (CIN/Alloggiati/tourist tax) | ✓ | ✓ | ✓ | **[FACT] shipped** |
| GDPR tools (export/erasure/retention) | ✓ | ✓ | ✓ | **[FACT] shipped** |
| AI dynamic pricing | preview only | ✓ | ✓ + portfolio rules | **[FACT] shipped** |
| Stripe payments | ✓ | ✓ | ✓ | **[FACT] shipped** |
| **Direct-booking website + engine** | ✓ basic | ✓ branded | ✓ multi-brand | Phase 1 |
| **Unified inbox** | — | ✓ | ✓ + SLA routing | Phase 2 |
| **AI copilot messaging** | trial caps | ✓ | ✓ unlimited | Phase 2 |
| **LTR contracts module** | add-on | ✓ | ✓ | Phase 3 |
| **Supplier marketplace** | browse | ✓ transact | ✓ + preferred rates | Phase 4 |
| **Google Vacation Rentals** | — | ✓ | ✓ | Phase 4 |
| Team seats / RBAC | 1 seat | up to 10 | unlimited + SSO | Phase 1 (multi-tenant) |
| Support / SLA | community | email | priority + onboarding | tiering |

## A.6 Pricing model

**Principles (F6):** subscription per unit/portfolio; **no hidden booking commission** on the operator's own bookings — the explicit anti-OTA stance.

| Tier | Price **[EST]** | Logic |
|------|-----------------|-------|
| **Starter** | **€0 freemium → €19–29/mo [EST]** (1–3 units) | PLG entry; compliance + 2 channels; converts hosts (Phase 2) |
| **Pro** | **€4–8 / unit / mo, banded [EST]** | Lands inside the **€150–400/mo** benchmark (F9) for 10–100 units; the revenue core |
| **Scale** | **custom, declining per-unit + SLA [EST]** | 50–500+ units; enterprise support, multi-brand |

ARPU target band **€150–400/mo (F9) [FACT]**; **€275/mo planning midpoint [EST]**. Marketplace (Phase 4) and services (onboarding/migration) layer on top per the revenue mix (A.8).

## A.7 GTM channels

| Channel | Motion | Anchor |
|---------|--------|--------|
| **Product-led growth (PLG)** | Freemium Starter + self-serve `onboarding-plg` spec; time-to-first-direct-booking as the activation metric | F7 wedge, self-serve |
| **Content & community** | Italian compliance content as lead magnet (CIN/Alloggiati/tourist-tax guides) — we already *encode* this knowledge; host/PM communities | Compliance moat **[FACT]** |
| **Agency & advisor partnerships** | PM agencies + *commercialisti*/accountants who advise STR operators on tax/compliance → referral | Italy ops reality **[EST]** |
| **Google Vacation Rentals** | Direct-booking distribution surface that bypasses OTA commission | F3 direct-booking thesis |

## A.8 Revenue mix target (F10)

| Stream | Target share | Notes |
|--------|:---:|-------|
| **SaaS subscription** | **70–80%** | Core; tiers A.6 |
| **Marketplace** | **10–20%** | Supplier marketplace take-rate (Phase 4) — *the only* place a take-rate exists, and never on the operator's guest bookings |
| **Services** | **remainder** | Onboarding, migration from Lodgify/Smoobu, premium support |

All **[FACT]** (F10).

## A.9 Five-year vision (targets **[EST]**)

Assumptions: Italy-first → ES/FR from ~Y3 (skill geography); ARPU drifts up as Pro/Scale mix grows; logo churn assumed ~3%/mo early, improving.

| Horizon | Paying accounts **[EST]** | ARPU/mo **[EST]** | ARR **[EST]** | Geography |
|---------|:---:|:---:|:---:|-----------|
| Y1 (first paid cohort) | 40–80 | €180 | ~€130–170k | Italy |
| Y2 | 200–350 | €230 | ~€0.6–1.0M | Italy |
| Y3 | 400–700 | €275 | ~€1.3–2.3M | Italy + ES pilot |
| Y4 | 900–1,500 | €310 | ~€3.3–5.6M | IT + ES + FR |
| Y5 | 1,800–3,000 | €340 | ~€7.3–12.2M | EU SMB |

> These are **planning [EST]** numbers for direction, not forecasts. Financial Strategist owns CAC/LTV/payback/gross-margin and should replace these with a defensible model. The 18-month "first paid cohort" horizon (skill) implies Y1 = a partial year.

## A.10 Risk register

| # | Risk | Type | Likelihood/Impact **[EST]** | Mitigation |
|---|------|------|:---:|-----------|
| R1 | Incumbents add Italian compliance + AI | Competitive | Med / High | Move fast on direct-booking + compliance depth; community + advisor lock-in; LTR breadth they lack |
| R2 | OTA API/ToS changes, rate limits, deprecation | Technical/Supplier | Med / High | Direct-booking shift (F3) reduces dependency; Polly resilience already shipped **[FACT]**; abstract via `IOtaAdapter` |
| R3 | CIN / Alloggiati / tourist-tax regulatory change | Regulatory | High / Med | Rates already DB-driven, never hardcoded **[FACT]**; compliance is config; monitor D.L. updates → **defer specifics to Legal validator** |
| R4 | EU AI Act / DAC7 / city short-let bans | Regulatory | Med / Med | Operational AI = limited risk; log AI decisions (pricing confidence already logged) **[FACT]**; **defer to Legal validator** |
| R5 | $0 infra misses compliance jobs (Alloggiati 24h, GDPR) | Technical | Med / High | Hosting decision: upgrade trigger to ~€5/mo Railway Hobby when real bookings exist **[FACT: `decision-hosting-zero-budget.md`]**; GH Actions cron in the interim |
| R6 | AI API cost/quality erodes margin | Technical/Financial | Med / Med | Tier AI usage caps; cache; confidence thresholds; **defer unit economics to Financial validator** |
| R7 | PLG fails to convert hosts to paid | Market | Med / Med | Compliance as the freemium hook; activation = first direct booking; agency channel as backstop |
| R8 | Booking-window compression hurts slow operators (not us) | Market | — | This is a *tailwind* for CasaZen's AI inbox (F4) — reframed as opportunity |

---

# B. Implementation Roadmap (current state → sellable production)

> Each phase is **incrementally shippable** and feeds the existing **AI-SDLC pipeline** (`.claude/sdlc/`): each macro-spec in Section C enters **Stage 01 Planning → 02 Design → 03 Development**, mirroring how `spec-property-detail.md` and `spec-role-onboarding.md` already flow. Infra cost tiers use the **$0 vs ~$5/mo** decision from `decision-hosting-zero-budget.md` **[FACT]**.

## Phase 0 — Now (current CasaZen state) **[FACT, codebase-grounded]**

**Shipped and in production-shape:**

- **OTA sync, 6 channels** — Airbnb, Booking.com, Expedia, VRBO, TripAdvisor, Agoda; `IOtaAdapter` + `ChannelFactory` + Polly resilience (retry/circuit-breaker/rate-limit).
- **Italian compliance** — CIN (`CinCodeAttribute`, D.L. 145/2023), Alloggiati Web police reporting (background job, 24h), tourist tax (`TouristTaxRate` entity, never hardcoded), GDPR (`GdprService` + retention/erasure job).
- **Payments** — Stripe processing + signature-verified webhooks; partial-refund tracking.
- **AI pricing adapter** — `PricingAdapterConfig`/`PricingHistory`, confidence scoring, preview + manual sync + daily job.
- **Admin backend** — `spec-admin-backend` (Auth0 Management API for roles).
- **Property detail** — `spec-property-detail` (owner 360° view + pricing entrypoint).
- **Role scaffolding for LTR** — `spec-role-onboarding` introduces `RentalType {ShortTerm, LongTerm, Both}` + `LongTermLandlord` role + layer switcher. **LTR is already seeded.**
- **Direct-booking seed** — `GET /api/properties/search` is **already anonymous** (no JWT) — the public surface a booking engine can build on **[FACT: `TECHNICAL.md`]**.
- **Architecture** — layered (`Core`/`Infrastructure`/`Web`), 12 controllers, 14 entities, Hangfire jobs; React 19 SPA (feature-slice, TanStack Query, Auth0); Supabase PostgreSQL; Vercel FE.

**Phase 0 reality flags (for validators):**

- **DB doc drift**: `PROJECT.md`/`TECHNICAL.md` still say *SQL Server*, but current reality is **Supabase PostgreSQL** (`domain-context.md`, `decision-hosting-zero-budget.md`) **[FACT]**. Docs need updating; not a code blocker.
- **No SaaS self-billing yet**: CasaZen processes *guests'* payments via Stripe, but there is **no subscription billing to charge CasaZen's own customers**. This is a hard gate for "sellable" → see `spec-saas-billing` (Phase 1).
- **No multi-tenancy/org model surfaced**: ownership is per-`OwnerId`; selling to PM teams needs org/workspace + seats → `spec-multi-tenant-orgs` (Phase 1).

**Infra tier:** **$0** (Render/Railway Free + GH Actions cron) **[FACT]**.

## Phase 1 — MVP Sellable: Direct Booking + Self-Serve + Billing

- **Goals:** turn the PMS into a *product an external PM can buy and use to take a commission-free direct booking*. Direct-booking engine (public branded site + checkout on the existing anonymous search + Stripe), PLG onboarding, **SaaS subscription billing for CasaZen itself**, and **multi-tenant orgs/seats**.
- **Specs:** `spec-direct-booking-engine`, `spec-onboarding-plg`, `spec-saas-billing`, `spec-multi-tenant-orgs`.
- **Dependencies:** existing anonymous `/properties/search`, Stripe integration, `spec-admin-backend` (roles), `spec-role-onboarding` (onboarding flow), `spec-property-detail`.
- **Exit criteria:** an external PM can self-onboard, publish a branded direct-booking site, take a **commission-free** booking + payment, and **pay CasaZen a subscription** — all without manual ops. Italian compliance (CIN/Alloggiati/tax) fires automatically on the direct booking.
- **Infra:** **$0 for demo/test → upgrade to ~$5/mo Railway Hobby at first real paying booking** (compliance jobs must run on schedule — R5) **[FACT exit trigger]**.

## Phase 2 — Operations AI Copilot: Unified Inbox + AI Messaging

- **Goals:** attack booking-window compression (F4). Aggregate OTA + direct guest messages into one inbox; AI drafts/sends replies; extend the existing pricing AI into a true lifecycle copilot.
- **Specs:** `spec-unified-inbox`, `spec-ai-copilot-messaging`.
- **Dependencies:** OTA adapters (message pull), direct-booking guest records (Phase 1), AI provider integration, existing pricing-AI patterns.
- **Exit criteria:** a PM handles all guest comms in CasaZen; AI suggests replies with measurable response-time reduction; opt-in auto-send with guardrails.
- **Infra:** **~$5/mo** (always-on for inbox webhooks) + metered AI API cost (tiered caps — R6).

## Phase 3 — Long-Term Rentals (LTR) lifecycle

- **Goals:** activate the already-scaffolded LTR path. Long-term lease contracts, tenant lifecycle, recurring rent, and Italian lease specifics (RLI registration, cedolare secca references — **scope/feasibility to Legal validator**).
- **Specs:** `spec-ltr-contracts`, `spec-ltr-rli-registration`.
- **Dependencies:** `spec-role-onboarding` (`LongTermLandlord`, `RentalType`), `Property`/`Guest` entities, payments (recurring).
- **Exit criteria:** a `LongTermLandlord` can create/track a long-term contract, collect recurring rent, and reference RLI registration — making CasaZen genuinely STR+LTR (F5).
- **Infra:** **~$5/mo** (no new tier; recurring-rent jobs reuse Hangfire).

## Phase 4 — Distribution + Marketplace

- **Goals:** open the second revenue stream (F10) and widen direct distribution. Supplier marketplace (cleaning, maintenance, photography, linen) with a take-rate, and Google Vacation Rentals integration.
- **Specs:** `spec-supplier-marketplace`, `spec-google-vacation-rentals`.
- **Dependencies:** multi-tenant orgs (Phase 1), payments/payouts, direct-booking engine (GVR feeds direct).
- **Exit criteria:** a marketplace transaction completes with platform take-rate; a property is discoverable + bookable via Google Vacation Rentals.
- **Infra:** **~$5/mo +** (payout/escrow may need Stripe Connect; flag to Financial/Legal).

## Phase 5 — Scale + EU expansion

- **Goals:** multi-brand, enterprise SLAs/SSO, deeper portfolio AI, and **ES/FR compliance modules** (replicating the Italian compliance pattern per market).
- **Specs:** `spec-enterprise-scale`, `spec-eu-compliance-es-fr`.
- **Dependencies:** all prior phases; localized regulatory research (Legal).
- **Exit criteria:** a 200+-unit multi-brand agency runs on Scale tier; first non-Italian market live with native compliance.
- **Infra:** scale-up beyond Hobby as load dictates (out of $0/$5 band).

### B.1 Gap mapping — current PMS vs market-analysis vision (F5, F7)

| Vision capability (analysis) | Current state **[FACT]** | Gap | Phase | Spec |
|------------------------------|--------------------------|-----|:---:|------|
| Direct-booking website + engine | Anonymous `/properties/search` only; no checkout/site | Public site, cart, direct checkout | 1 | `spec-direct-booking-engine` |
| Self-serve onboarding (PLG) | `spec-role-onboarding` (role choice) exists | Full self-serve signup → activation | 1 | `spec-onboarding-plg` |
| Sell the SaaS (charge customers) | Stripe charges *guests* only | Subscription billing for CasaZen | 1 | `spec-saas-billing` |
| Sell to PM teams | Per-`OwnerId` ownership | Org/workspace + seats + RBAC | 1 | `spec-multi-tenant-orgs` |
| Unified inbox | None | OTA + direct message aggregation | 2 | `spec-unified-inbox` |
| AI copilot (messaging/lifecycle) | AI *pricing* only | Messaging + lifecycle copilot | 2 | `spec-ai-copilot-messaging` |
| LTR contracts | `RentalType`/`LongTermLandlord` scaffold | Contracts, recurring rent, RLI | 3 | `spec-ltr-contracts`, `spec-ltr-rli-registration` |
| Supplier marketplace | None | Marketplace + take-rate | 4 | `spec-supplier-marketplace` |
| Google Vacation Rentals | None | GVR feed + direct integration | 4 | `spec-google-vacation-rentals` |
| EU multi-market compliance | Italy only | ES/FR compliance modules | 5 | `spec-eu-compliance-es-fr` |

### B.2 AI-SDLC alignment note

Every Section C macro-spec is authored in `Sessions/specs/spec-{slug}.md` following the **exact structure of `spec-property-detail.md`** (Overview, User Story, Acceptance Criteria BE+FE, Technical Notes file table, Compliance/regulatory gates, Dependencies). The Coordinator writes full specs on consensus; the SDLC pipeline then consumes them issue-by-issue. This keeps the roadmap *executable*, not just strategic.

---

# C. Macro-Spec Index

> One row per implementable chunk → each becomes `Sessions/specs/spec-{slug}.md`, mirroring `spec-property-detail.md` (Overview · User Story · AC BE+FE · regulatory gates · Dependencies). The index **summarizes only**; full specs are written later by the Coordinator. The 5th column previews the regulatory gate that the full spec must cover (mirroring the spec format).

| spec slug | roadmap phase | one-line summary | depends on | key regulatory gate |
|-----------|:---:|------------------|-----------|---------------------|
| `spec-direct-booking-engine` | 1 | Public branded booking site + cart + direct checkout on existing anonymous `/properties/search`, commission-free, compliance auto-fires | anonymous search, Stripe, `spec-property-detail`, `spec-multi-tenant-orgs` | CIN display, tourist tax at checkout, GDPR consent capture |
| `spec-onboarding-plg` | 1 | Self-serve signup → activation (publish site + first booking) extending role onboarding | `spec-role-onboarding`, `spec-admin-backend` | GDPR consent, ToS acceptance |
| `spec-saas-billing` | 1 | Subscription billing to charge CasaZen's own customers (tiers/seats/usage) via Stripe Billing | Stripe, `spec-multi-tenant-orgs` | PSD2/Stripe, IVA/e-invoicing (→ Legal/Financial) |
| `spec-multi-tenant-orgs` | 1 | Org/workspace model + seats + RBAC so PM teams (not just `OwnerId`) can collaborate | `spec-admin-backend`, Auth0 | Data isolation, GDPR controller/processor roles |
| `spec-unified-inbox` | 2 | Aggregate OTA + direct guest messages into one inbox with routing | OTA adapters, `spec-direct-booking-engine` | GDPR (guest PII in messages), data retention |
| `spec-ai-copilot-messaging` | 2 | AI drafts/sends guest replies; extends pricing AI into lifecycle copilot | `spec-unified-inbox`, AI provider | EU AI Act transparency, log AI decisions |
| `spec-ltr-contracts` | 3 | Long-term lease contracts, tenant lifecycle, recurring rent | `spec-role-onboarding` (`LongTermLandlord`), payments | Lease law, recurring-payment consent (→ Legal) |
| `spec-ltr-rli-registration` | 3 | Italian lease registration (RLI) + cedolare secca references | `spec-ltr-contracts` | RLI / Agenzia delle Entrate, cedolare secca (→ Legal) |
| `spec-supplier-marketplace` | 4 | Marketplace for cleaning/maintenance/etc. with platform take-rate | `spec-multi-tenant-orgs`, payouts | Marketplace VAT, Stripe Connect/escrow, DAC7 (→ Legal/Financial) |
| `spec-google-vacation-rentals` | 4 | Google Vacation Rentals feed + direct-booking integration | `spec-direct-booking-engine` | GVR policy compliance, price accuracy |
| `spec-enterprise-scale` | 5 | Multi-brand, SSO, enterprise SLA, portfolio-level AI rules | all prior | SLA/DPA, SSO security |
| `spec-eu-compliance-es-fr` | 5 | Spain/France compliance modules replicating the Italian pattern | compliance services | ES/FR STR registration, local tax (→ Legal) |

**Macro-spec coverage check vs brief:** required slugs all present — `direct-booking-engine` ✓, `unified-inbox` ✓, `onboarding-plg` ✓, `ltr-contracts` ✓, `supplier-marketplace` ✓, `ai-copilot-messaging` ✓. Added (builder judgment): `saas-billing`, `multi-tenant-orgs` (both **hard gates to being "sellable"** — flagged for Product Architect), `ltr-rli-registration`, `google-vacation-rentals`, `enterprise-scale`, `eu-compliance-es-fr`.

---

## D. Open questions for validators (Round 1 → Round 2)

**Product Architect**
1. Are `spec-saas-billing` and `spec-multi-tenant-orgs` correctly placed as **Phase 1 blockers** (you cannot sell without them), or can a thinner "single-org + manual invoicing" MVP defer multi-tenancy to Phase 2?
2. Does the existing anonymous `/properties/search` + Stripe genuinely shortcut `spec-direct-booking-engine`, or is a separate public read-model/site-builder a larger lift than implied?

**Legal & Compliance**
3. For `spec-ltr-rli-registration`: is RLI/cedolare-secca automation realistically in scope, or should Phase 3 stop at contract generation + manual registration guidance?
4. Company-formation path (SRLS €1 + forfettario vs SRL) and **e-invoicing/IVA on SaaS subscriptions** — any blocker that must move *before* Phase 1 `spec-saas-billing`?

**Financial Strategist**
5. Are the **Italy bottom-up [EST]** (≈600k STR units, ~25% managed, €275 ARPU midpoint, ~€1.5M SOM Y3) defensible, and what CAC/LTV/payback would make the 18-month-to-first-cohort horizon viable on the $0→$5 infra path?

---

*End of Round 1 draft. Validators: please challenge [EST] figures first, then phase ordering, then compliance scope.*
