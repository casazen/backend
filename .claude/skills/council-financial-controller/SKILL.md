---
name: council-financial-controller
description: Financial modeling for CasaZen strategic options — SaaS unit economics, pricing models, AI cost advantage, Italian SME WTP, payback analysis.
---

# Council domain — Financial Controller

## Italian Property Management SaaS Pricing Context

**Benchmark pricing** (from `council/domain-context.md`):
- Channel managers (Lodgify, Smoobu): €30-150/month per property [market data]
- Guesty / Hostaway (PMC-tier): €300+/month for accounts [market data]
- AI pricing bolt-ons (PriceLabs, PriceDynamics): €10-20/month/property [market data]
- Italian WTP ceiling for individual owners: €30-60/month (price-sensitive SMEs) [EST]
- Italian PMC WTP: €100-300/month (for multi-property tools with reporting) [EST]

**Revenue model options**:
1. **Per-property subscription**: €X/property/month — predictable, scales with portfolio size
2. **Tiered SaaS**: Free (1 property) → Starter (€25/month, 3 properties) → Pro (€59/month, 10 properties) → PMC (€149/month, unlimited)
3. **Transaction fee**: 0.5-1% of booking value processed through CasaZen — aligns incentives but lumpy revenue
4. **Direct booking commission**: 3-5% of direct bookings — only viable if direct booking engine built (Option D)
5. **White-label licensing**: €500-2000/month per PMC using white-label — high ACV, low volume

## Unit Economics Framework

**Acquisition assumptions** (Italy SaaS for property owners) [EST]:
- CAC via content/SEO: €50-150 per owner
- CAC via paid (Google/Meta): €200-500 per owner
- Organic/word-of-mouth CAC: €20-50 (compliance-led virality: "my friend said CasaZen does the police filings")

**Retention assumptions** [EST]:
- Annual churn for compliance-led tool: 15-20% (regulatory switching cost reduces churn)
- Annual churn for pure channel manager: 30-40% (easy to switch)
- Monthly churn target: <2%

**LTV calculation** (at €49/month, 20% annual churn):
- Average customer life: 5 years [EST]
- LTV: €49 × 12 × 5 = €2,940 [EST]
- LTV/CAC: €2,940 / €150 = ~20x [EST] — excellent for content-led acquisition

**Payback period** (at €49/month, €150 CAC):
- Gross margin assumption: 70% (SaaS + AI API costs)
- Monthly contribution: €49 × 0.70 = €34.30
- Payback: €150 / €34.30 = ~4.4 months [EST] — well within 18-month target

## AI Cost Structure

**AI API costs** (per property per month) [EST based on Claude/OpenAI pricing]:
- Alloggiati Web pre-fill (1-3 LLM calls/booking, avg 2 bookings/month): ~€0.02/month
- AI pricing advice (4 queries/month per owner): ~€0.05/month
- AI welcome message (2 messages/booking): ~€0.02/month
- Regulatory monitoring (1 weekly summary): ~€0.01/month
- **Total AI API cost**: ~€0.10-0.20/month/property [EST]

**AI gross margin impact**: AI costs are <0.5% of revenue at €49/month — negligible. The AI features justify the price premium without materially affecting margins.

## Strategic Option Financial Cases

### Option A — AI Compliance Assistant, Individual Owners

| Metric | Value | Label |
|---|---|---|
| Target segment | ~100K active Italian owners (pain buyers) | EST |
| Year-1 target | 2,000 paying customers | EST |
| Price | €49/month/account (up to 3 properties) | Recommended |
| Year-1 ARR | €49 × 12 × 2,000 = €1.18M | EST |
| CAC (content-led) | €100-150 | EST |
| LTV/CAC | ~20x | EST |
| Payback | ~4.5 months | EST |
| AI API cost | ~€0.20/account/month | EST |
| Gross margin | ~70% | EST |
| **Primary risk** | Churn higher than 20% if AI features disappoint | |

**Sensitivity**: if churn doubles to 40%/year → LTV drops to €1,470 → LTV/CAC = 10x → still viable. If CAC triples to €450 → payback = 13 months → still within 18-month target.

### Option B — PMC Platform

| Metric | Value | Label |
|---|---|---|
| Target segment | ~5,000 tech-forward Italian PMCs | EST |
| Year-1 target | 200 PMC accounts | EST |
| Price | €149/month/account (unlimited properties) + €15/property/month >10 | Recommended |
| Avg revenue/account | €250/month (15 properties avg) | EST |
| Year-1 ARR | €250 × 12 × 200 = €600K | EST |
| CAC (direct sales/referral) | €500-1,500 | EST |
| LTV/CAC | €250×12×6 years / €1,000 = 18x | EST |
| **Primary risk** | 12-18 month sales cycle; multi-tenancy build cost ~€50-100K | |

**Key sensitivity**: PMC sales cycle. If average time to first invoice = 6 months, Year-1 ARR may be €300K not €600K.

### Option C — Long-Term Rental Expansion

| Metric | Value | Label |
|---|---|---|
| Target segment | ~500K Italian landlords needing compliance help | EST |
| Year-1 target | 5,000 paying customers | EST |
| Price | €29/month (simpler product, lower WTP) | Recommended |
| Year-1 ARR | €29 × 12 × 5,000 = €1.74M | EST |
| CAC (SEO/content on Italian rental regulation) | €30-80 (highly searchable topic) | EST |
| LTV/CAC | ~30x | EST |
| **Primary risk** | Product surface is different — requires new feature set, not extension of existing |

**Key sensitivity**: if Italian landlords prefer free tools (government website) for tax calculations, WTP may be lower (€15/month) → Year-1 ARR = €900K → still viable.

## Recommendation Framework

**Best financial case**: Option C (largest SAM, lowest CAC, highest LTV/CAC)
**Best risk-adjusted case**: Option A (leverages existing product, shorter time to revenue)
**Highest ceiling**: Option B (highest ACV, enterprise expansion path)
**Avoid**: Option D (direct booking engine) — high upfront investment, XL tech cost, OTA relationship risk, marketing spend before revenue.
