# CasaZen Platform Launch — Integrated Draft v2 (Round 2)

> **Author**: GTM Strategist (Builder) · **Pattern**: builder-validator · **Round**: 2
> **Status**: PROPOSE — revised draft resolving all 13 validator conditions
> **Supersedes**: `draft.md` (Round 1, left untouched). This file is self-contained.
> **Topic**: Go-to-market + product roadmap for CasaZen as an AI-powered direct booking & rental OS (STR + LTR), from current codebase to sellable production platform.

---

## Changes from Round 1

> Each of the 13 consolidated, non-conflicting validator conditions → the change made. All anchored to the Coordinator's verified codebase correction (22 entities / 16 controllers / 10 jobs / Supabase PostgreSQL; existing LTR lease+RLI+e-sign subsystem; existing context-RBAC).

| # | Source | Condition (short) | Change made in v2 |
|---|--------|-------------------|-------------------|
| 1 | PA | Correct Phase 0 to real codebase | §B Phase 0 rewritten: 22 entities / 16 controllers / 10 jobs / Supabase Postgres; LTR subsystem (`LeaseContract`/`LeaseRegistration`/`Party`/`LeaseEvent`, `LeasesController`, 3 lease jobs, `LeaseWorkflowService`) and context-RBAC (`AppContext`/`UserContextMembership`/`Role`/`RolePermission`, `RequireContext:{ctx}:{perm}`) listed explicitly |
| 2 | PA | Rescope+reorder LTR (already built) → complete+verify, parallel Phase 1.5 | New **Phase 1.5** (parallel, no dep on billing/multi-tenancy): `spec-ltr-recurring-rent` (the real gap — `MonthlyRent` exists, no recurring ledger/job), `spec-ltr-frontend`, `spec-ltr-verification`; greenfield LTR specs removed |
| 3 | PA | Split direct-booking-engine into 3 | `spec-direct-booking-engine` → `spec-public-booking-readmodel` + `spec-direct-checkout` + `spec-branded-booking-site`; "anonymous search + Stripe = **seed, not shortcut**" stated in Phase 0 & Phase 1 |
| 4 | PA | Split multi-tenant-orgs across phases | `spec-tenant-boundary` (Phase 1: `Org` + `OrgId` FK on Property/Booking/LeaseContract/Payment + plan entitlement) + `spec-org-seats-collaboration` (Phase 2: invitations/seat RBAC extending `UserContextMembership`/`RequireContext`); `spec-saas-billing` stays Phase 1 |
| 5 | PA | Unified-inbox entities + ingestion job + adapter assumption | `spec-unified-inbox` now specifies `Conversation`/`Message`/`Thread` entities + async Hangfire `InboundMessageIngestionJob`; per-adapter inbound assumption documented (OTA messaging APIs vary; some channels = email-only fallback) |
| 6 | Legal C1 | Correct entity/regime decision tree | §A.11 replaces "SRLS €1 + forfettario": **forfettario = individual/ditta only**, never SRLS/SRL (IRES/IRAP + 22% IVA). Tree: (A) ditta forfettario vs (B) SRLS €1 vs (C) SRL; SRLS/SRL flagged as more coherent for grants/hiring/EU-B2B; tax trade-off deferred to Financial |
| 7 | Legal C2 | IVA/OSS + SDI in billing + entry gate | `spec-saas-billing` regulatory gate = IVA/OSS matrix (IT 22% / EU-B2B reverse charge + VIES check / EU-B2C OSS >€10k) + SDI e-invoicing (Stripe ≠ Italian *fattura elettronica*); **"P.IVA + SDI live" Phase 1 entry gate before first charge** added |
| 8 | Legal C3 | Stripe Connect, operator = merchant of record | `spec-direct-checkout` + `spec-saas-billing` mandate **Stripe Connect, operator as MoR** for guest funds; CasaZen charges subscription only, never holds/settles guest money |
| 9 | Legal C4 | Rescope RLI to assisted, not unattended filing | `spec-ltr-rli-registration` rescoped: counsel-reviewed contract templates + cedolare-secca decision support + RLI pre-fill/export + guided manual-registration checklist (30-day deadline + notifications). **No unattended filing.** Reconciliation with PA#2: existing Openapi.it RLI integration **stays** but is framed as operator-attended/confirmed (Openapi.it = filing channel; CasaZen ≠ *intermediario abilitato*) |
| 10 | Legal C5 | DPA + subprocessors + AI Act disclosure | Added as explicit regulatory gates in `spec-onboarding-plg` (DPA + subprocessor list: Supabase EU, Auth0, Stripe, SendGrid) and `spec-ai-copilot-messaging` (EU AI Act transparency disclosure) |
| 11 | Fin | Infra trigger = first real guest check-in; prove cron | §B Phase 0/Phase 1 infra trigger changed from "first paying customer" → **"first real guest check-in via the platform"** (Alloggiati 24h clock = check-in-driven); R5 now requires the $0-window GH Actions cron firing Alloggiati + GDPR endpoints be **proven, not assumed** |
| 12 | Fin | Relabel ARPU; fix Y1 ARR floor; reconcile pricing | §A.6/§A.9: ARPU **€180–200 launch/SOM-entry**, **€275 = Y3**; **Y1 ARR floor ≈ €90k**; reconciled to €4–8/unit × ~30 units (≈ €120–240/mo, midpoint ~€180); 5-yr table made consistent |
| 13 | Fin | AI fair-use as hard product constraint | `spec-ai-copilot-messaging` AC now includes **hard caps**: cheap-model default + confidence-gated frontier routing + caching + overage metering; target **AI ≤ 10–15% of ARPU, gross margin ≥ 80%** |

**Coverage: 13/13.** One reconciliation note (conditions 2 ↔ 9): the existing RLI/e-sign integration is *retained and verified* (PA#2) **and** reframed as operator-attended/assisted (Legal C4) — these are complementary, handled in `spec-ltr-verification` (it works) + `spec-ltr-rli-registration` (it's framed as assisted). Detail in §C.

---

## 0. Evidence base, sourcing, and conventions

### 0.1 Sourcing convention

| Tag | Meaning |
|-----|---------|
| **[FACT]** | From a project document or the Coordinator's verified codebase correction. |
| **[EST]** | Builder inference/estimate, with stated assumption. Validators challenge these first. |

### 0.2 Sourcing caveats (carried from Round 1, updated)

- **Codebase baseline is now the Coordinator-verified reality** (22 entities / 16 controllers / 10 jobs / Supabase PostgreSQL; LTR + context-RBAC subsystems). `docs/PROJECT.md` and `docs/TECHNICAL.md` are **stale** (say "SQL Server", "14 entities", "12 controllers") and need a docs-update pass — tracked as a Phase 0 hygiene item, not a code blocker. **[FACT]**
- The mandatory market file `Sessions/market-analysis-2026/AI-short/long-term-platform.md` is still **not on disk**; market-size facts remain anchored to `domain-context.md › market-landscape` (which cites it). Action for Coordinator unchanged.

### 0.3 Market-analysis anchor facts (unchanged spine)

Source: `domain-context.md › market-landscape` (relaying the market analysis).

| # | Anchor fact | Used for |
|---|-------------|----------|
| F1 | STR market ~$101.7B (2025) → $121.9B (2033), CAGR 3.7% **[FACT]** | Demand stability |
| F2 | VR software ~$1.5B (2024) → $3.2B (2033), CAGR ~9.2% **[FACT]** | TAM anchor |
| F3 | Direct booking ~29% vs 71% OTA (15–20% commission) **[FACT]** | Core wedge |
| F4 | 27% of bookings within 0–7 days (2026) **[FACT]** | Inbox + AI value |
| F5 | Gap: no democratic 1–500 OS w/ native STR+LTR + AI copilot + direct booking **[FACT]** | Positioning |
| F6 | Subscription per unit/portfolio, transparent, no booking commission **[FACT]** | Pricing model |
| F7 | Wedge: PM 10–200 units, IT/ES/FR, direct booking + inbox + AI copilot **[FACT]** | Segment/Phase 1 |
| F8 | Tiers: Starter (1–3) → Pro (4–100) → Scale (50–500+) **[FACT]** | Product tiers |
| F9 | ARPU benchmark €150–400/mo per account vs Lodgify/Guesty **[FACT]** | Pricing band |
| F10 | Revenue mix 70–80% SaaS, 10–20% marketplace, rest services **[FACT]** | Revenue model |

---

# A. Business Plan

## A.1 Executive summary

**Vision.** CasaZen is the **democratic rental operating system for Europe's independent operators** — one subscription platform running the *entire* rental lifecycle (STR **and** LTR), with **commission-free direct booking** at the center and an **AI copilot** across pricing, guest messaging, and operations. We start in **Italy**, where the regulatory pain is sharpest and where CasaZen already has the deepest moat.

**Wedge (F7).** Property managers running **10–200 units in Italy**, replacing channel manager + website builder + spreadsheets + manual compliance with one OS — and keeping the 15–20% OTA commission (F3) via direct booking.

**Three differentiation axes (market-analysis-anchored):**

1. **Democratic scale 1 → 500 units (F5, F8).**
2. **AI copilot across the full lifecycle (F4, F5).**
3. **Transparent subscription, zero booking commission (F3, F6).**

**Local moat (now stronger than Round 1 assumed).** CasaZen already enforces native Italian compliance — CIN (D.L. 145/2023), Alloggiati Web, tourist tax, GDPR — **and already ships a long-term lease subsystem** (lease creation → e-signature → RLI registration via Openapi.it Docuengine → receipt) plus **context-scoped RBAC** for short-rent vs long-rent operating layers **[FACT, verified codebase]**. No competitor (Lodgify/Guesty/Hostaway/Smoobu) couples native Italian STR compliance with a native LTR lease+RLI engine. **The STR+LTR "native both" position in F5 is already substantially real — not a roadmap promise.**

## A.2 Market view — TAM / SAM / SOM

> Global figures **[FACT]** (F1, F2). Geographic/unit decompositions **[EST]**.

| Layer | Definition | Figure | Basis |
|-------|-----------|--------|-------|
| **TAM** | Global SMB rental-ops software (STR + small-LTR) | **~$2B (2025) → ~$4–5B (2033) [EST]** | Anchored on VR-software $1.5B→$3.2B (F2) **[FACT]** + **[EST]** LTR adjacency |
| **SAM** | IT+ES+FR, 1–500 units, STR+LTR | **~€150–250M/yr [EST]** | Italy bottom-up, scaled |
| **SOM (Y3)** | Italy-first, wedge, 3-yr | **~€1.5M ARR [EST]** | Italy wedge capture |

**Italy bottom-up (all [EST]):** ~600,000 active STR units (CIN era) · ~25% professionally managed → ~150,000 units · ~30 units/wedge account → **~5,000 wedge PM accounts** · plus ~200,000+ small hosts (1–9) for Phase 2 freemium. SOM Y3 = capture ~6–8% of 5,000 wedge accounts ≈ 300–400 paying accounts → ≈ €1.3–2.3M ARR at the €275 **Y3** ARPU → **≈ €1.5M ARR [EST]**.

**Trends (F3, F4):** every point shifted OTA→direct is pure operator margin (F3) and CasaZen takes no cut of it (F6); 27% sub-7-day bookings (F4) make AI-assisted inbox response a booking-conversion lever; software layer growth ~9.2% (F2) vs lodging ~3.7% (F1) = an active re-tooling/switching window.

## A.3 Competitive set — the gap CasaZen fills

> Names per analysis/brief (F5); capability reads **[EST]**, to be pressure-tested by Product Architect.

| Competitor | Strength | Gap CasaZen exploits |
|-----------|----------|----------------------|
| **Lodgify** | Direct-booking site builder + channel manager, SMB | Shallow PM ops; no native IT compliance; STR-only, no LTR; AI limited **[EST]** |
| **Guesty** | Enterprise PMS, deep | Priced/sold for the top — *not democratic* (F5); STR-only; no native IT regulatory automation **[EST]** |
| **Hostaway** | Mid-market channel mgr + automations | Sales-led, higher entry; STR-only; AI nascent; no native IT legal **[EST]** |
| **Smoobu** | Affordable SMB channel mgr + site | Lighter automation/AI; STR-only; shallow compliance; no LTR **[EST]** |

**Gap, precisely (F5 + verified codebase):** no incumbent is simultaneously democratic (1–500), **STR+LTR-native** (CasaZen already has the LTR lease+RLI engine), **AI-copilot-driven**, **direct-booking-first / zero booking commission**, and **natively Italian-compliant**. CasaZen owns the two least-copyable quadrants (IT STR compliance + IT LTR lease/RLI) **today**.

**Porter (lightweight) [EST]:** rivalry high in generic STR PMS but **low in the STR+LTR+IT-compliance intersection**; buyer power moderate (sticky once compliance + direct revenue land); **supplier power = OTA APIs** (mitigated by direct shift, F3); new entrants face a real Italian-regulatory barrier; substitutes = spreadsheets/point tools we replace.

## A.4 Customer segments (phased)

| Phase focus | Segment | Why | Source |
|------|---------|-----|--------|
| Phase 1 | **PM 10–200 units, Italy** | Highest pain, clear ARPU, fastest direct-booking ROI | F7 **[FACT]** |
| Phase 2 | **Hosts 1–9 units** | Large pool, freemium/PLG, compliance hook | domain-context **[FACT]** |
| Later | **Small hotels / boutique B&B** | Adjacent unified-ops + direct demand, Scale tier | F8 ceiling **[EST]** |

## A.5 Product tiers — feature matrix

Tiers per F8. Status reflects **verified** codebase.

| Capability | Starter (1–3) | Pro (4–100) | Scale (50–500+) | Status |
|-----------|:---:|:---:|:---:|--------|
| Unit management | ✓ | ✓ | ✓ | **[FACT] shipped** |
| OTA sync (6 channels, `IChannelAdapter`) | 2 ch | ✓ 6 | ✓ 6 + priority | **[FACT] shipped** |
| IT compliance (CIN/Alloggiati/tourist tax) | ✓ | ✓ | ✓ | **[FACT] shipped** |
| GDPR tools | ✓ | ✓ | ✓ | **[FACT] shipped** |
| AI dynamic pricing | preview | ✓ | ✓ + portfolio | **[FACT] shipped** |
| Stripe payments | ✓ | ✓ | ✓ | **[FACT] shipped** |
| **LTR lease + e-sign + RLI** | add-on | ✓ | ✓ | **[FACT] shipped (verify Phase 1.5)** |
| Context-RBAC layers (short-/long-rent) | ✓ | ✓ | ✓ | **[FACT] shipped** |
| **Direct-booking site + checkout** | ✓ basic | ✓ branded | ✓ multi-brand | Phase 1 |
| **Recurring-rent ledger (LTR)** | add-on | ✓ | ✓ | Phase 1.5 |
| **Unified inbox** | — | ✓ | ✓ + SLA routing | Phase 2 |
| **AI copilot messaging** | trial caps | ✓ | ✓ (fair-use caps) | Phase 2 |
| Org seats / collaboration | 1 seat | up to 10 | unlimited + SSO | Phase 2 |
| **Supplier marketplace** | browse | ✓ transact | ✓ preferred | Phase 3 |
| **Google Vacation Rentals** | — | ✓ | ✓ | Phase 3 |
| Multi-brand / enterprise SLA | — | — | ✓ | Phase 4 |

## A.6 Pricing model (reconciled per Financial #12)

**Principles (F6):** subscription per unit/portfolio; **no booking commission** on operator bookings.

| Tier | Price **[EST]** | Logic |
|------|-----------------|-------|
| **Starter** | €0 freemium → €19–29/mo (1–3 units) | PLG entry; converts hosts |
| **Pro** | **€4–8 / unit / mo, banded** | Core revenue |
| **Scale** | custom, declining per-unit + SLA | 50–500+ units |

**Reconciliation:** Pro €4–8/unit × ~30 units (typical wedge account) ≈ **€120–240/mo → midpoint ~€180** = the **launch/SOM-entry ARPU €180–200 [EST]**. As accounts add units and Scale mix grows, ARPU drifts to **€275 by Y3 [EST]**, staying inside the **€150–400 benchmark (F9) [FACT]**.

## A.7 GTM channels

| Channel | Motion | Anchor |
|---------|--------|--------|
| PLG | Freemium Starter + `spec-onboarding-plg`; activation = time-to-first-direct-booking | F7 |
| Content & community | Italian compliance content (CIN/Alloggiati/tourist-tax + **RLI/cedolare** for LTR) as lead magnet | compliance + LTR moat **[FACT]** |
| Agency & advisor partnerships | PM agencies + *commercialisti* (referral, esp. LTR/cedolare) | **[EST]** |
| Google Vacation Rentals | Direct-booking distribution bypassing OTA commission | F3 |

## A.8 Revenue mix target (F10)

| Stream | Share | Notes |
|--------|:---:|-------|
| SaaS subscription | **70–80%** | Core (A.6) |
| Marketplace | **10–20%** | Supplier marketplace take-rate (Phase 3) — the only take-rate, never on guest bookings |
| Services | remainder | Onboarding, migration (Lodgify/Smoobu), premium support |

All **[FACT]** (F10).

## A.9 Five-year vision (targets **[EST]**, relabeled per Financial #12)

Assumptions: Italy-first → ES/FR from ~Y3; ARPU starts at the reconciled launch band and drifts up; early logo churn ~3%/mo improving.

| Horizon | Paying accounts **[EST]** | ARPU/mo **[EST]** | ARR **[EST]** | Geography |
|---------|:---:|:---:|:---:|-----------|
| **Y1 (first paid cohort)** | **40–60** | **€180–200** | **floor ≈ €90k** (≈ €90–140k) | Italy |
| Y2 | 200–350 | €230 | ~€0.6–1.0M | Italy |
| Y3 | 400–700 | **€275** | ~€1.3–2.3M | Italy + ES pilot |
| Y4 | 900–1,500 | €310 | ~€3.3–5.6M | IT+ES+FR |
| Y5 | 1,800–3,000 | €340 | ~€7.3–12.2M | EU SMB |

> Y1 ARR floor ≈ €90k = ~42 accounts × €180/mo × 12. Planning **[EST]**, not forecast; Financial owns the defensible CAC/LTV/payback/margin model. 18-month-to-first-cohort horizon ⇒ Y1 is a partial year.

## A.10 Risk register

| # | Risk | Type | L/I **[EST]** | Mitigation |
|---|------|------|:---:|-----------|
| R1 | Incumbents add IT compliance + AI | Competitive | Med/High | Move fast on direct booking; community + advisor lock-in; **LTR lease/RLI breadth they lack** |
| R2 | OTA API/ToS change | Tech/Supplier | Med/High | Direct shift (F3); Polly resilience shipped **[FACT]**; abstract via `IChannelAdapter` |
| R3 | CIN/Alloggiati/tourist-tax change | Regulatory | High/Med | Rates DB-driven, never hardcoded **[FACT]**; → Legal |
| R4 | EU AI Act / DAC7 / city short-let bans | Regulatory | Med/Med | Operational AI = limited risk; AI Act disclosure in spec; pricing confidence logged **[FACT]**; → Legal |
| R5 | $0 infra misses **check-in-driven** compliance jobs (Alloggiati 24h, GDPR) | Tech | Med/High | **Trigger = first real guest check-in** → upgrade to ~€5/mo Railway Hobby; **GH Actions cron firing Alloggiati+GDPR endpoints must be PROVEN in the $0 window, not assumed** (Financial #11) **[FACT trigger]** |
| R6 | AI cost/quality erodes margin | Tech/Fin | Med/Med | **Hard fair-use caps** in `spec-ai-copilot-messaging` (≤10–15% ARPU, GM ≥80%) (Financial #13) |
| R7 | PLG fails to convert hosts | Market | Med/Med | Compliance as freemium hook; activation = first direct booking; agency backstop |
| R8 | LTR mis-framed as unattended tax filing | Regulatory | Med/High | RLI rescoped to **operator-attended/assisted**; Openapi.it = filing channel; CasaZen ≠ *intermediario abilitato* (Legal C4) |

## A.11 Company formation & tax — corrected decision tree (Legal C1 / condition 6)

> Strategic guidance, **not legal/tax advice**; external counsel required pre-formation. Tax-trade-off final call = **Financial Strategist**.

**Correction:** the *regime forfettario* applies **only to an individual / ditta individuale** — it **cannot** apply to an SRLS or SRL, which are always **IRES + IRAP** companies with **22% IVA** and **mandatory SDI electronic invoicing**. Round 1's "SRLS €1 + forfettario" was invalid.

| Option | Vehicle | Tax/IVA | Liability | Grants/hiring/EU-B2B | Verdict **[EST]** |
|--------|---------|---------|-----------|----------------------|---------|
| **A** | Ditta individuale, forfettario | No IVA (if eligible), flat substitute tax | **Unlimited** | Poor (not grant-friendly; caps) | Cheapest to start; **poor fit** for a venture |
| **B** | **SRLS (€1 capital)** | IRES/IRAP + **22% IVA** + **SDI** | **Limited** | Good — esp. **startup innovativa** incentives | **Recommended start** if limited liability needed early |
| **C** | SRL | IRES/IRAP + 22% IVA + SDI | Limited | Best (full flexibility, investment-ready) | Convert to / start as SRL when raising/scaling |

**Position [EST]:** SRLS/SRL is the **more coherent vehicle** given grant ambitions (Invitalia, PNRR digitalization, regional innovation funds — esp. as *startup innovativa*), future hiring, and EU-B2B reverse-charge sales — accepting 22% IVA + SDI as the cost of doing business. The 22%-IVA-vs-forfettario economic trade-off is **Financial's** to finalize. This decision directly gates `spec-saas-billing` (see condition 7).

---

# B. Implementation Roadmap (current state → sellable production)

> Phases are incrementally shippable and feed the **AI-SDLC pipeline** (`Stage 01 Planning → 02 Design → 03 Development`), each spec mirroring `spec-property-detail.md`. Infra tiers use the **$0 → ~€5/mo** trigger from `decision-hosting-zero-budget.md`, **now keyed to first real guest check-in** (Financial #11) **[FACT]**.

## Phase 0 — Now (verified baseline) **[FACT, Coordinator-verified]**

**Scale:** **22 entities · 16 controllers · 10 Hangfire jobs · Supabase PostgreSQL** (not SQL Server). React 19 SPA (feature-slice, TanStack Query, Auth0), Vercel FE. Layered `Core`/`Infrastructure`/`Web`.

**Shipped subsystems:**

- **OTA sync, 6 channels** — `IChannelAdapter` (+ `ChannelFactory`), Polly resilience; `OtaIntegration`, `OtaSyncLog`; jobs `OtaSyncJob`, `BookingPullJob`.
- **Italian STR compliance** — CIN (`CinCodeAttribute`), Alloggiati Web (`AlloggiatiWebReport` + `AlloggiatiWebReportJob`, 24h), tourist tax (`TouristTaxRate`/`TaxRate`), GDPR (`GdprController` + `GdprDataRetentionJob`).
- **LTR lease subsystem (substantially built)** — entities `LeaseContract` (incl. `MonthlyRent`, `FiscalRegime`, `RegistrationDeadline`, `Parties`, `Registration`, `Events`, `HasExtraEUTenant`), `LeaseRegistration` (**RLI registration via Openapi.it Docuengine**), `Party`, `LeaseEvent`; `LeasesController` (**create → e-sign → RLI-register → receipt**); `LeaseWorkflowService`/`ILeaseWorkflowService`; jobs `LeaseSignStatusPollingJob`, `LeaseRegistrationStatusPollingJob`, `ESignWebhookJob`.
- **Context-scoped RBAC** — `AppContext`, `UserContextMembership`, `Role`, `RolePermission`, `ContextAuthorizationService`; policy convention **`RequireContext:{context}:{permission}`** (e.g. `RequireContext:short-rent:property.write` — verified in `PropertiesController`); short-rent / long-rent operating layers.
- **Payments** — Stripe processing + signature-verified webhooks (`WebhooksController`, `StripeWebhookJob`), partial refunds.
- **AI pricing** — `PricingAdapterConfig`/`PricingHistory`, confidence scoring; `DynamicPricingJob`.
- **Admin / users / me** — `AdminController`, `UsersController`, `MeController`, `AuthController`, Auth0 Management API for roles; `User.RentalType` migration present.

**Phase 0 reality flags (for validators):**

1. **Docs drift [FACT]:** `PROJECT.md`/`TECHNICAL.md` say SQL Server / 14 entities / 12 controllers — stale. Docs-update is a Phase 0 hygiene item.
2. **No SaaS self-billing [FACT]:** Stripe charges *guests*; there is **no subscription billing to charge CasaZen's own customers** → `spec-saas-billing` (Phase 1) is a hard "sellable" gate.
3. **No tenant boundary [FACT]:** context-RBAC exists, but data is per-`OwnerId` + context-scoped, **not Org/tenant-isolated**; selling to PM teams/agencies needs an `Org` tenant key → `spec-tenant-boundary` (Phase 1).
4. **Direct booking = seed, not shortcut [FACT]:** `GET /api/properties/search` is `[AllowAnonymous]` **but returns the raw `Property` entity including `OwnerId`** — a public read-model/DTO is required before any public surface ships. The anonymous endpoint + Stripe is a **seed**; the engine is a real build (3 specs).
5. **LTR recurring-rent gap [FACT]:** `LeaseContract.MonthlyRent` is a static field — there is **no recurring-rent ledger or billing job**; this is the true LTR gap (Phase 1.5).

**Infra tier:** **$0** (Render/Railway Free + GH Actions cron). The cron firing Alloggiati + GDPR endpoints must be **proven** while at $0 (R5).

## Phase 1 — MVP Sellable: Direct Booking + Tenant Boundary + Billing

- **Goals:** make CasaZen *buyable and sellable*: a public commission-free direct-booking engine, an `Org` tenant boundary, and subscription billing for CasaZen itself.
- **Specs:** `spec-public-booking-readmodel`, `spec-direct-checkout`, `spec-branded-booking-site`, `spec-tenant-boundary`, `spec-saas-billing`, `spec-onboarding-plg`.
- **Dependencies:** anonymous search (seed) → public DTO; Stripe → **Stripe Connect (operator MoR)**; `spec-admin-backend`/`spec-role-onboarding`; context-RBAC.
- **Entry gate (Legal C2):** **P.IVA + SDI e-invoicing live before the first charge** (ties to §A.11 vehicle choice).
- **Exit criteria:** an external PM self-onboards, gets an `Org`, publishes a branded direct-booking site, takes a **commission-free** booking + guest payment (**operator = merchant of record via Stripe Connect**), and **pays CasaZen a subscription** (correct IVA/OSS + SDI invoice) — compliance (CIN/Alloggiati/tax) auto-fires.
- **Infra:** **$0 demo/test → ~€5/mo Railway Hobby at first real guest check-in** (R5, Financial #11).

## Phase 1.5 — LTR Complete + Verify (PARALLEL to Phase 1)

> Depends on **neither billing nor multi-tenancy** → runs in parallel (PA #2). The LTR engine exists; this phase closes the one real gap, adds the FE, verifies the existing flow, and reframes RLI as assisted.

- **Goals:** ship recurring-rent billing for leases; build the LTR frontend over `LeasesController`; verify the create→e-sign→RLI→receipt flow end-to-end; frame RLI as operator-attended.
- **Specs:** `spec-ltr-recurring-rent`, `spec-ltr-frontend`, `spec-ltr-verification`, `spec-ltr-rli-registration` (rescoped/assisted).
- **Dependencies:** existing `LeaseContract`/`LeaseRegistration`/`LeaseWorkflowService`/lease jobs; Stripe (recurring); context `long-rent` RBAC.
- **Exit criteria:** a `long-rent` landlord generates a (counsel-reviewed) contract, e-signs, gets **assisted** RLI registration + cedolare decision support + 30-day deadline checklist, and the platform **automatically bills recurring monthly rent** via a new ledger + Hangfire job.
- **Infra:** **~€5/mo** (recurring-rent + lease polling jobs reuse Hangfire).

## Phase 2 — Operations AI Copilot: Unified Inbox + AI Messaging + Org Seats

- **Goals:** attack booking-window compression (F4): unified inbox (OTA + direct), AI messaging copilot with hard fair-use caps, and org seats/collaboration so PM teams work together.
- **Specs:** `spec-unified-inbox`, `spec-ai-copilot-messaging`, `spec-org-seats-collaboration`.
- **Dependencies:** OTA adapters (inbound messages — *per-adapter, varies*), direct-booking guests (Phase 1), AI provider, `spec-tenant-boundary` + existing `UserContextMembership`/`RequireContext`.
- **Exit criteria:** all guest comms in one inbox; AI drafts replies (cheap-model default, confidence-gated frontier, cached, metered) keeping **AI ≤10–15% ARPU / GM ≥80%**; team members invited with seat-scoped RBAC.
- **Infra:** **~€5/mo** (always-on inbox webhooks) + metered AI cost (capped).

## Phase 3 — Distribution + Marketplace

- **Goals:** open the second revenue stream (F10) and widen direct distribution. Supplier marketplace (cleaning/maintenance/photography) with a take-rate; Google Vacation Rentals.
- **Specs:** `spec-supplier-marketplace`, `spec-google-vacation-rentals`.
- **Dependencies:** `spec-tenant-boundary`, Stripe Connect payouts, `spec-direct-checkout` (GVR feeds direct).
- **Exit criteria:** a marketplace transaction completes with platform take-rate; a property is discoverable + bookable via GVR.
- **Infra:** **~€5/mo +** (Connect payouts/escrow; flag to Financial/Legal).

## Phase 4 — Scale + EU expansion

- **Goals:** multi-brand, enterprise SLA/SSO, portfolio AI, and **ES/FR compliance modules** (replicate the IT compliance + lease pattern per market).
- **Specs:** `spec-enterprise-scale`, `spec-eu-compliance-es-fr`.
- **Dependencies:** all prior; localized regulatory research (Legal).
- **Exit criteria:** a 200+-unit multi-brand agency runs on Scale; first non-Italian market live with native compliance.
- **Infra:** scale-up beyond Hobby as load dictates.

### B.1 Gap mapping — current vs vision (re-mapped to verified baseline)

| Vision capability (F5/F7) | Verified current state **[FACT]** | Real gap | Phase | Spec(s) |
|---------------------------|-----------------------------------|----------|:---:|---------|
| Direct-booking public surface | Anonymous `/search` returns **raw `Property` (incl `OwnerId`)** | Public DTO/read-model | 1 | `spec-public-booking-readmodel` |
| Direct guest checkout | Stripe (guest charges) exists | Guest booking + Stripe **Connect** checkout + compliance auto-fire | 1 | `spec-direct-checkout` |
| Branded booking website | None | Public FE booking surface | 1 | `spec-branded-booking-site` |
| Sell to PM teams (tenancy) | per-`OwnerId` + context-RBAC, **no Org** | `Org` tenant key + `OrgId` FK + entitlement | 1 | `spec-tenant-boundary` |
| Charge customers (SaaS) | Stripe charges guests only | Subscription billing + IVA/OSS + SDI | 1 | `spec-saas-billing` |
| Self-serve onboarding | `spec-role-onboarding` exists | Full PLG signup→activation + DPA/AI-Act gates | 1 | `spec-onboarding-plg` |
| LTR recurring rent | `LeaseContract.MonthlyRent` static | Recurring-rent ledger + Hangfire job | 1.5 | `spec-ltr-recurring-rent` |
| LTR frontend | Backend `LeasesController` only | LTR FE over existing workflow | 1.5 | `spec-ltr-frontend` |
| LTR flow assurance | create→e-sign→RLI→receipt exists | Verify E2E | 1.5 | `spec-ltr-verification` |
| LTR registration framing | RLI via Openapi.it exists | Reframe **assisted** + cedolare/checklist | 1.5 | `spec-ltr-rli-registration` |
| Unified inbox | None | `Conversation`/`Message`/`Thread` + ingestion job | 2 | `spec-unified-inbox` |
| AI copilot (messaging) | AI **pricing** only | Messaging copilot + **hard fair-use caps** | 2 | `spec-ai-copilot-messaging` |
| Team seats | context-RBAC primitives only | Invitations + seat RBAC | 2 | `spec-org-seats-collaboration` |
| Supplier marketplace | None | Marketplace + take-rate | 3 | `spec-supplier-marketplace` |
| Google Vacation Rentals | None | GVR feed + direct integration | 3 | `spec-google-vacation-rentals` |
| EU multi-market | Italy only | ES/FR compliance modules | 4 | `spec-eu-compliance-es-fr` |

### B.2 AI-SDLC alignment

Each Section C spec is authored in `Sessions/specs/spec-{slug}.md` in the `spec-property-detail.md` shape (Overview · User Story · AC BE+FE · Technical Notes file table · Compliance gates · Dependencies), then consumed issue-by-issue. Phase 1 and Phase 1.5 can run as two parallel tracks since their dependencies are disjoint.

---

# C. Macro-Spec Index (post-split)

> One row per implementable chunk → `Sessions/specs/spec-{slug}.md`, mirroring `spec-property-detail.md` (incl. regulatory gates). Index summarizes; full specs written later by the Coordinator.

| spec slug | phase | one-line summary | depends on | key regulatory gate |
|-----------|:---:|------------------|-----------|---------------------|
| `spec-public-booking-readmodel` | 1 | Public `Property` DTO + harden `[AllowAnonymous]` `/search` (currently leaks raw entity incl `OwnerId`) | existing anonymous search | **GDPR data minimization** (no `OwnerId`/PII in public payload); CIN display only |
| `spec-direct-checkout` | 1 | Guest booking + **Stripe Connect (operator = MoR)** checkout/PaymentIntent; compliance auto-fire on booking/check-in | `spec-public-booking-readmodel`, Stripe, Booking/Guest, Alloggiati/tax | **Stripe Connect operator-MoR (CasaZen never holds guest funds)**; tourist tax at checkout; Alloggiati on check-in; PSD2/SCA |
| `spec-branded-booking-site` | 1 | New public FE booking surface (branded per Org) | `spec-public-booking-readmodel`, `spec-tenant-boundary` | GDPR cookie/consent + ToS; AI-Act note if AI content |
| `spec-tenant-boundary` | 1 | Introduce `Org`/tenant key + `OrgId` FK on Property/Booking/LeaseContract/Payment + plan entitlement | context-RBAC, migrations | **Tenant data isolation**; GDPR controller/processor delineation |
| `spec-saas-billing` | 1 | Subscription billing for CasaZen's own customers (tiers/seats) via Stripe Billing/Connect | `spec-tenant-boundary`, Stripe | **IVA/OSS matrix** (IT 22% / EU-B2B reverse charge + VIES / EU-B2C OSS >€10k) + **SDI e-invoicing** (Stripe ≠ *fattura elettronica*); **"P.IVA + SDI live" entry gate before first charge** |
| `spec-onboarding-plg` | 1 | Self-serve signup → activation (publish site + first booking), extending role onboarding | `spec-role-onboarding`, `spec-admin-backend`, `spec-tenant-boundary` | GDPR consent + ToS + **DPA + subprocessor list (Supabase EU, Auth0, Stripe, SendGrid)** |
| `spec-ltr-recurring-rent` | 1.5 | Recurring-rent **ledger + Hangfire job** (closes the `MonthlyRent`-has-no-billing gap) | `LeaseContract`, Stripe, Hangfire | Recurring-payment **PSD2/SCA** + consent; rent receipt/invoice |
| `spec-ltr-frontend` | 1.5 | LTR FE over `LeasesController` (create → e-sign → RLI → receipt + rent) | `LeasesController`, `LeaseWorkflowService`, context `long-rent` | GDPR (tenant/`Party` PII); no raw PII in views |
| `spec-ltr-verification` | 1.5 | E2E verify existing lease flow + 3 lease jobs (sign/registration polling, e-sign webhook) | LTR subsystem | Lease `DataRetentionUntil`/erasure correctness; receipt integrity |
| `spec-ltr-rli-registration` | 1.5 | **Rescoped/assisted**: counsel-reviewed templates + cedolare decision support + RLI pre-fill/export + guided 30-day checklist & notifications over existing Openapi.it integration | `LeaseRegistration`, `LeaseWorkflowService` | **No unattended filing** — operator-attended; Openapi.it = filing channel; CasaZen ≠ *intermediario abilitato*; 30-day RLI deadline |
| `spec-unified-inbox` | 2 | New `Conversation`/`Message`/`Thread` entities + async **`InboundMessageIngestionJob`** aggregating OTA + direct messages | OTA adapters (`IChannelAdapter`), `spec-direct-checkout` | **Per-adapter inbound assumption** (OTA messaging APIs vary; email fallback); GDPR guest-PII + retention |
| `spec-ai-copilot-messaging` | 2 | AI drafts/sends replies; extends pricing AI to lifecycle copilot | `spec-unified-inbox`, AI provider | **EU AI Act transparency disclosure** + log AI decisions + DPA; **hard fair-use caps**: cheap-model default + confidence-gated frontier routing + caching + overage metering → **AI ≤10–15% ARPU, GM ≥80%** |
| `spec-org-seats-collaboration` | 2 | Invitations + seat RBAC, **extending `UserContextMembership` / `RequireContext`** | `spec-tenant-boundary`, context-RBAC | Least-privilege RBAC; secure invitation tokens |
| `spec-supplier-marketplace` | 3 | Marketplace (cleaning/maintenance/etc.) + platform take-rate | `spec-tenant-boundary`, Stripe Connect payouts | Marketplace **VAT**; Connect/escrow; **DAC7** reporting |
| `spec-google-vacation-rentals` | 3 | GVR feed + direct-booking integration | `spec-direct-checkout` | GVR **price-accuracy** policy; data-feed compliance |
| `spec-enterprise-scale` | 4 | Multi-brand, SSO, enterprise SLA, portfolio-level AI rules | all prior | DPA/SLA; SSO security |
| `spec-eu-compliance-es-fr` | 4 | ES/FR compliance modules replicating the IT pattern | compliance services | ES/FR STR registration + local tax + lease law |

**Coverage check vs Round 1 required slugs:** `direct-booking-engine` → **split** into `public-booking-readmodel` + `direct-checkout` + `branded-booking-site` ✓ (PA#3); `unified-inbox` ✓ (PA#5 entities+job); `onboarding-plg` ✓ (Legal C5 gates); `ltr-contracts` → **rescoped** to `ltr-recurring-rent` + `ltr-frontend` + `ltr-verification` + `ltr-rli-registration` ✓ (PA#2, Legal C4); `supplier-marketplace` ✓; `ai-copilot-messaging` ✓ (Fin#13 caps). `multi-tenant-orgs` → **split** `tenant-boundary` (P1) + `org-seats-collaboration` (P2) ✓ (PA#4); `saas-billing` kept in P1 ✓. Total: **17 specs**.

---

## D. Residual open questions

1. **(Product Architect)** Phase 1 vs 1.5 parallelism: do `spec-tenant-boundary`'s `OrgId` FK on `LeaseContract` and `Payment` create a merge-order dependency with the Phase 1.5 LTR specs (i.e., should LTR specs assume pre- or post-`OrgId` schema)? This is the one place the two parallel tracks touch.
2. **(Financial)** Confirm the reconciled launch ARPU €180–200 (€4–8/unit × ~30 units) and Y1 floor ≈ €90k as the planning baseline, and set the CAC ceiling implied by the $0→€5 infra path.
3. **(Legal)** For `spec-saas-billing`, confirm whether the §A.11 vehicle decision (SRLS/SRL → 22% IVA + SDI) must be finalized as a **hard predecessor** to the Phase 1 "P.IVA + SDI live" entry gate, or can run concurrently with development up to the first-charge gate.

---

*End of Round 2 draft. All 13 conditions resolved (13/13); see "Changes from Round 1" for the map.*
