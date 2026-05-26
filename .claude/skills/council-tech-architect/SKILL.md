---
name: council-tech-architect
description: Technical architecture assessment for CasaZen strategic pivots — current stack, effort estimates, AI integration patterns, incremental delivery path.
---

# Council domain — Technical Architect

## Current CasaZen Architecture (as of May 2026)

**Stack**: C# 13 / .NET 10, ASP.NET Core Web API, EF Core, SQL Server, Hangfire, Polly, Auth0, Stripe, SendGrid

**Layer structure**:
```
Casazen.Web       → 12 controllers, Auth0 middleware, Swagger, Hangfire dashboard
Casazen.Core      → 14 entities, 20+ repository/service interfaces
Casazen.Infrastructure → EF Core DbContext, repository impls, 6 OTA adapters, external clients
Casazen.Tests     → xUnit, Moq
```

**14 Core Entities**: Property, Booking, Guest, Payment, TouristTaxRate, OtaIntegration, OtaSyncLog, AlloggiatiWebReport, PropertyDocument, PricingAdapterConfig, PricingHistory, CancellationPolicy, PropertyAmenity (enum), User

**What's already built and working**:
- OTA resilience layer (Polly): retry, circuit breaker, rate limiting, timeout — per-platform config
- Alloggiati Web client: Italian police reporting API integration
- CIN validation: format `IT-XXXXX-XXXXXXXXXX` stored per property
- Tourist tax calculation: TaxRate entity per city/municipality
- GDPR endpoints: consent recording, right-to-erasure, data export
- AI pricing infrastructure: PricingAdapterConfig, PricingHistory, 6 endpoints — no external AI provider yet
- Auth0 JWT validation on all `/api` endpoints
- Hangfire background jobs: OTA sync (hourly/15min), GDPR retention, pricing sync

**Current technical debt**:
- Only Airbnb adapter is real; 5 others are stubs
- FE/BE field naming conflicts (NightlyRate vs pricePerNight, PostalCode vs zipCode — issues #86, #90, #91)
- API documentation lags implementation by ~10+ endpoints
- No multi-tenant architecture (single-tenant only)

## Effort Scale

Use this for estimates:
- **S** (Small): 1-3 days, single developer, no schema change or minor schema change, no new external integration
- **M** (Medium): 1-2 weeks, new entity or service, new external integration, or significant API surface change
- **L** (Large): 2-6 weeks, new bounded context, major schema change, multi-tenant refactor, or complex external integration
- **XL** (Extra Large): 2-3+ months, fundamental architectural change (e.g., multi-tenancy, event-driven rewrite, new product surface)

## AI Integration Patterns for .NET

**Low-cost AI integrations** (S-M effort):
- **LLM for document generation** (S): call Anthropic/OpenAI API from a new `AiDocumentService`, pass structured data, get formatted output (e.g., generate SCIA filing text, cedolare secca summary, guest welcome message)
- **AI pricing advisor endpoint** (M): extend `PricingAdapterController`, call Claude/GPT with property + market context, return pricing recommendation with explanation
- **Regulatory change monitoring** (M): scheduled Hangfire job queries regulatory RSS feeds, uses LLM to summarize changes, notifies owners

**Medium-cost AI integrations** (M-L effort):
- **AI guest communication** (M-L): new `GuestCommunicationService`, LLM handles Q&A about property, booking confirmation messages, check-in instructions
- **AI Alloggiati Web form assistant** (M): LLM parses guest document photos (passport/ID), pre-fills AlloggiatiWebReport fields
- **AI property performance advisor** (L): aggregate booking history + pricing history + market data → periodic AI-generated performance report per property

**High-cost AI integrations** (L-XL effort):
- **AI dynamic pricing with external ML model** (L): integrate with Pricelabs/custom model via API, train on CasaZen booking history
- **AI-native direct booking engine** (XL): SEO content generation, conversion optimization, direct payment flow bypassing OTA

## Strategic Option Technical Assessment

**Option A — AI compliance assistant for individual owners**
- Leverage: CIN validation, Alloggiati Web client, tourist tax, GDPR — all exist
- Add: LLM document generation (S), AI pricing advisor (M), cedolare secca calculator (M)
- Total: M effort to ship meaningful AI layer on top of existing compliance stack
- Risk: external AI API cost needs to be modeled

**Option B — PMC multi-tenant platform (5-50 properties)**
- Leverage: everything in Option A
- Add: multi-tenant data model refactor (L-XL), white-label configuration (M), property manager role (M)
- Total: L-XL — multi-tenancy is the hardest part; requires scoping all entities to tenant
- Risk: multi-tenant refactor touches every data access layer

**Option C — Long-term rental expansion**
- Leverage: GDPR, CIN validation patterns, document storage, payment infrastructure
- Add: new LongTermContract entity (M), cedolare secca calculator (M), SCIA filing service (M), new booking lifecycle (M)
- Total: L — new bounded context but reuses 80% of infrastructure
- Risk: different regulatory cadence; long-term leases are months/years, not days

**Option D — Direct booking engine**
- Leverage: Stripe, Auth0, pricing infrastructure
- Add: public-facing booking widget (L), SEO/marketing integration (L), direct messaging (M), AI content generation (M)
- Total: XL — requires frontend investment and marketing stack
- Risk: requires domain acquisition, SEO timeline, OTA relationship risk

## Recommended First Steps (for each option)

**Option A first steps**:
1. Wire up Claude/OpenAI SDK in `Casazen.Infrastructure/AI/` (S)
2. Add `AiPricingAdvisorService` using existing PricingAdapterConfig (M)
3. Add `AiDocumentGeneratorService` for guest welcome + Alloggiati Web pre-fill (M)
4. Add cedolare secca calculator to Tax domain (M)
5. Ship AI pricing advisory endpoint to frontend (S)

**Option C first steps**:
1. Add `LongTermContract` entity + migration (S)
2. Add cedolare secca TaxRate variant (S)
3. Add SCIA filing service (M)
4. New `LongTermBookingController` with lease lifecycle (M)
5. AI contract generation endpoint (M)
