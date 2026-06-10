# AI Strategy & AI-Powered Supplier Marketplace

> Product strategy specification for positioning CasaZen as an **AI-native** property
> management platform, and for building a net-new **AI-powered supplier & services
> marketplace** on top of the existing booking/compliance core.
>
> Status: **Draft / vision** — not yet implemented. This document captures the
> intended direction so engineering and product can plan against it.

---

## 1. Current state — the "AI" reality check

CasaZen markets itself around *"AI-driven dynamic pricing"* (see `BUSINESS.md`), but
**there is currently no AI/ML anywhere in the codebase**. An audit of the solution found:

- **No** LLM, ML model, embedding, prediction, or forecasting code.
- The `PricingAdapterService` (`Casazen.Infrastructure/Services/PricingAdapterService.cs`)
  that powers "AI-driven dynamic pricing" is a **fixed rule-based multiplier**:
  - Public holiday → ×1.5
  - Summer (Jun–Aug) → ×1.3; Winter (Nov–Feb) → ×0.8; shoulder → ×1.0
- `PricingHistory.AiConfidence` is **stored but never computed by any model** — the value
  is passed in by callers.
- The glossary promises pricing based on *"seasonality, demand signals, and public
  holidays"*, but **demand signals (occupancy, booking pace, competitor prices, events)
  do not exist** in the code.

**Risk**: publicly claiming "AI-based" while shipping fixed rules is a reputational and
potentially a misleading-advertising risk. The claim must be backed by real capability
before the message is amplified.

### What *is* genuinely strong today (the real moat)

The differentiator is **not** pricing AI — competitors (PriceLabs, Beyond, Wheelhouse)
own that with years of data. CasaZen's moat is the **only platform combining AI with
automated Italian short-term-rental compliance**. No global PMS (Guesty, Lodgify, Smoobu,
Hostaway) does Italian compliance seriously:

- CIN registration (D.L. 145/2023)
- Alloggiati Web police reporting (24h obligation)
- Per-city tourist tax engine
- GDPR retention / erasure
- Multi-OTA channel manager (6 platforms) with Polly resilience
- Stripe + **Stripe Connect** already wired (foundation for marketplace payouts)

---

## 2. AI roadmap for the PMS core

Ordered by differentiation × feasibility. The unifying theme: **AI that exploits the
Italian-compliance moat**, not generic pricing AI.

| # | Capability | Why it differentiates | Notes |
|---|---|---|---|
| 1 | **Real predictive pricing** (replace fixed rules) | Makes the existing claim honest | Model on occupancy, booking pace, competitor prices, local events, lead time. `AiConfidence` becomes real. |
| 2 | **AI Compliance Assistant** (unique) | Nobody in Italy has this | LLM (Claude) reads new regional/municipal rules → flags impact per property; OCR + extraction pre-fills Alloggiati fields from an ID photo; explains tax math in natural language. Aligns with the existing `regulatory_agent` / `analyzer_agent` rules. |
| 3 | **Guest communication AI** | Most visible, expected in 2026 | Multilingual auto-replies (pre-checkin, in-stay), per-OTA optimized listing copy, message triage/sentiment. |
| 4 | **Revenue & anomaly insights** | Retention driver | NL summaries ("RevPAR down 12% because…"), anomaly detection (double-booking, off-market price, availability gaps), cancellation prediction. |
| 5 | **Smart document / OCR pipeline** | Removes the #1 host pain | Identity extraction from passport/ID for check-in and Alloggiati. |

### Table-stakes gaps (non-AI but required to be credible as a PMS)

Unified guest messaging inbox, reporting/analytics engine, cleaning/team task management,
unified multi-channel calendar. These are not in the backend today.

### Recommended positioning

**"The AI-native PMS for short-term rentals in Italy — compliant by design."**
Not "yet another pricing AI." Sequence:

1. **Now**: stop calling the rule-based pricing "AI" in public materials until a model
   ships (claim risk).
2. **AI MVP (1–2 months)**: LLM-powered Compliance Assistant + guest communication —
   high impact, low data requirement, leverages the unique moat.
3. **Mid-term**: predictive pricing on real data to honor the original claim.

---

## 3. AI-Powered Supplier & Services Marketplace (net-new vision)

> The ambition: go beyond property management into a **two-sided marketplace** that
> connects property owners/guests with **local service suppliers** — a plumber in Como,
> a locksmith in Turin, a Vespa tour in Naples — where **AI runs matching, supplier
> acquisition, and reputation-based prioritization**. This is the "do something
> completely new" bet.

### 3.1 Why this is uniquely ours

- Every CasaZen property already has a **verified geolocation, owner, and guest base** —
  built-in demand and built-in geographic targeting that a standalone marketplace lacks.
- **Stripe Connect is already integrated** → supplier payouts and platform commission
  are a short step away.
- The **Italian compliance DNA** extends naturally to suppliers: P.IVA validation,
  *fattura elettronica* (SDI), insurance/liability docs — a trust layer competitors
  (TaskRabbit, Thumbtack, generic directories) don't provide for the Italian STR niche.

### 3.2 Three AI pillars

#### Pillar A — AI Service Matching (demand side)
A host or guest expresses a need in **natural language** ("ho bisogno di un idraulico a
Como per una perdita, urgente, parla inglese"). The system:

1. **Intent extraction (LLM)** → structured request: `category=plumber`,
   `location=Como`, `urgency=high`, `language=en`, `budget?`, `time_window?`.
2. **Candidate retrieval** → geo filter (service area covers Como) + semantic match of
   request against each supplier's capability profile (embeddings + vector search).
3. **Ranking** → a composite score (see Pillar C) produces an ordered shortlist with a
   human-readable reason per supplier ("highly rated, responds in <15 min, speaks
   English, covers Como").

#### Pillar B — AI Supplier Discovery & Onboarding CRM (supply side)
A "growth engine" that **finds and recruits suppliers** so the marketplace has liquidity:

1. **Discovery** → AI surfaces candidate suppliers per category/city from public sources,
   normalizes and de-duplicates them into a lead pipeline.
2. **Enrichment** → LLM builds a structured capability profile (services, areas,
   languages, hours) from messy source data; flags missing compliance docs.
3. **Outreach** → AI drafts personalized, localized invitation/onboarding messages and
   manages a CRM pipeline (`Prospect → Contacted → Onboarding → Active → Suspended`).
4. **Self-onboarding assist** → conversational onboarding that collects P.IVA, insurance,
   service areas, pricing, and validates them (P.IVA checksum, document presence).

#### Pillar C — AI Reputation & Priority Ranking
A scoring engine that **evaluates services** and produces the dynamic priority order used
at call time (Pillar A, step 3). Inputs:

- Guest/host reviews (rating + LLM sentiment/quality extraction from free text)
- Operational signals: response time, acceptance rate, job completion rate, dispute rate
- Price fit vs. category benchmark
- Compliance status (valid P.IVA, insurance, fattura elettronica capability)
- Recency/availability and language match to the request

Output: a per-supplier **ReputationScore** plus a **per-request priority rank** (the same
supplier can rank differently depending on the request's location, urgency, and language).

### 3.3 Proposed domain model (new entities)

| Entity | Purpose | Key attributes |
|---|---|---|
| `ServiceProvider` | A supplier on the platform | Name, categories[], service areas (geo/cities), languages[], pricing model, P.IVA, insurance docs, capacity, status, Stripe Connect account |
| `ServiceCategory` | Taxonomy of services | Code (plumber, locksmith, cleaning, experience…), parent, localized labels |
| `ServiceRequest` | A need expressed by host/guest | Raw text, extracted intent (category, location, urgency, language, budget, time window), requester, property (optional), status |
| `ServiceMatch` | A request↔provider pairing | Score, rank, match reason, offered/accepted state |
| `ServiceJob` | An accepted match → executed work | Lifecycle (Requested→Accepted→InProgress→Completed→Cancelled), price, payout via Stripe Connect, invoice ref |
| `ProviderReview` | Post-job feedback | Rating, text, LLM-extracted quality signals |
| `ReputationScore` | Computed provider score | Composite score + component breakdown, last computed |
| `ProviderLead` | CRM acquisition pipeline | Source, enriched profile, outreach stage, owner/assignee |

### 3.4 AI/technical building blocks

- **LLM (Claude)** for: intent extraction, profile enrichment, outreach copy, review
  sentiment/quality extraction, NL match explanations.
- **Embeddings + vector search** for semantic request↔capability matching.
- **Ranking service** combining geo distance, availability, semantic relevance,
  reputation, price fit, language — tunable weights, explainable output.
- **Stripe Connect** (already integrated) for supplier payouts + platform commission.
- **Background jobs (Hangfire)** for discovery crawls, score recomputation, outreach
  cadence — fits the existing job architecture.

### 3.5 Risks & open questions (must address before build)

- **Marketplace liquidity / cold-start**: no value until enough suppliers exist per
  city/category. Pillar B (AI acquisition) is the mitigation but must prove out.
- **Trust & safety / quality**: vetting, dispute handling, liability for bad service.
- **Legal**: intermediation liability, **fattura elettronica / SDI** obligations for
  marketplace transactions, GDPR when sharing guest data with third-party suppliers
  (data-sharing agreements, minimization), platform-vs-employer classification of
  suppliers.
- **AI quality**: matching/ranking must be explainable and auditable; guard against
  bias and pay-to-win ranking that erodes trust.
- **Scope discipline**: this is a second product. Sequence it *after* the AI MVP
  (§2) so the core PMS credibility is established first.

### 3.6 Suggested phasing

1. **Phase 0 — Foundations**: `ServiceProvider`, `ServiceCategory`, manual supplier
   onboarding, basic geo+category search (no AI). Validate demand on real properties.
2. **Phase 1 — AI Matching**: LLM intent extraction + semantic ranking + NL explanations.
3. **Phase 2 — Reputation engine**: reviews, operational signals, dynamic priority rank.
4. **Phase 3 — AI Acquisition CRM**: discovery, enrichment, automated outreach pipeline.
5. **Phase 4 — Payments & compliance**: Stripe Connect payouts, commission, fattura
   elettronica, supplier compliance gating.

---

## 4. Cross-references

- Current pricing logic: `Casazen.Infrastructure/Services/PricingAdapterService.cs`
- Stripe Connect (payout foundation): `Casazen.Infrastructure/Payments/`,
  `Casazen.Web/Controllers/ConnectController.cs`
- Compliance moat: `BUSINESS.md` (CIN, Alloggiati, tourist tax, GDPR)
- Regulatory agents referenced in `.claude/rules/compliance.md`
