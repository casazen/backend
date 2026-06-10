---
name: council-gtm-strategist
description: Market sizing, GTM, and business plan drafting for CasaZen platform launch council.
---

# Council domain — GTM Strategist (Builder)

## Context to load before acting

1. **Mandatory**: `Sessions/market-analysis-2026/AI-short/long-term-platform.md`
2. `councils/casazen-platform-launch/domain-context.md` — sections: overview, market-landscape, financial-context, documents-index
3. `Docs/INDEX.md` — open docs tagged market, strategy, business
4. Existing specs: `Sessions/specs/spec-*.md` (format reference)

## Builder deliverable structure

### Business plan (`business-plan.md`)

| Section | Content |
|---------|---------|
| Executive summary | Vision, wedge, 3 differentiation axes from market analysis |
| Market | TAM/SAM/SOM (labeled EST), trends (29% direct, AI adoption) |
| Competition | Lodgify, Guesty, Hostaway, Smoobu — gaps CasaZen fills |
| Segments | Phase 1: PM 10–200 units Italy; Phase 2: 1–9; Phase 3: hotels |
| Product tiers | Starter / Pro / Scale — feature matrix |
| Pricing | Subscription per unit/portfolio; no booking commission |
| GTM | PLG, community, agency partnerships, Google Vacation Rentals |
| Revenue mix | 70–80% SaaS, 10–20% marketplace, rest services |
| 5-year vision | ARPU, customer count targets (EST), EU expansion |
| Risks | Competitive, regulatory, technical — mitigations |

### Implementation roadmap (`implementation-roadmap.md`)

| Phase | Typical content |
|-------|-----------------|
| 0 — Now | Current PMS: OTA sync, compliance, pricing adapter, admin |
| 1 — MVP sellable | Direct booking engine, unified inbox v1, onboarding |
| 2 — AI copilot | Messaging AI, content optimization, assisted pricing |
| 3 — LTR + marketplace | Long-term contracts, supplier marketplace |
| 4 — Scale | Multi-brand, enterprise SLAs, GVR deep integration |

Each phase: goals, dependencies, exit criteria, infra tier ($0 vs $5), specs to create.

### Macro-spec index

Propose `Sessions/specs/spec-{slug}.md` for each implementable chunk. Example slugs (customize per round):

- `spec-direct-booking-engine`
- `spec-unified-inbox`
- `spec-onboarding-plg`
- `spec-ltr-contracts`
- `spec-supplier-marketplace`
- `spec-ai-copilot-messaging`

## Output shape

- Metrics labeled **EST** when not from docs
- Separate **facts from docs** vs **inference**
- Every strategic claim traceable to market analysis section

## Reference checklists

- Porter five forces (lightweight)
- TAM / SAM / SOM sanity check
- Wedge clarity: who pays first and why

## Geography and horizon

- **Geography**: Italy first, then Spain/France
- **Industry**: vacation rental software, direct booking, AI proptech
- **Horizon**: 5 years product, 18 months to first paid cohort
