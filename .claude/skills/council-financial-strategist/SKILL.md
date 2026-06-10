---
name: council-financial-strategist
description: Unit economics, burn minimization, and financial validation for CasaZen platform launch.
---

# Council domain — Financial Strategist (Validator)

## Context to load before acting

1. `councils/casazen-platform-launch/domain-context.md` — section: financial-context
2. `Sessions/decision-hosting-zero-budget.md` — $0 vs $5 hosting architecture
3. `Sessions/market-analysis-2026/AI-short/long-term-platform.md` — §3 pricing, §5.4 revenue, §5.6 unit economics
4. `docs/INFRA.md` — current hosting stack costs

## Financial thesis template

Validate builder's plan against:

1. **Can launch at ~€0–50/mo infra** through Phase 0–1?
2. **Path to first €1k MRR** — how many customers at which tier?
3. **Gross margin** — cloud + AI API as % of ARPU
4. **Upgrade triggers** — when $5 Railway, when Auth0 paid tier, etc.

## Cost model (monthly, label EST)

| Line item | Phase 0 ($0) | Phase 1 (first customers) | Phase 2 (scale) |
|-----------|--------------|---------------------------|-----------------|
| Backend hosting | Render/Railway Free | Railway Hobby ~$5 | Scale with usage |
| Database | Supabase free tier | Supabase Pro if needed | |
| Frontend | Vercel free | Vercel Pro if needed | |
| Auth0 | Free tier limits | Paid if MAU exceeds | |
| Stripe | Pay per transaction | Same | |
| SendGrid | Free tier | Paid if volume | |
| AI API (OpenAI/etc.) | Dev usage | Per-customer allocation | |
| Domain + email | ~€15/yr | | |
| Commercialista | ~€50–150/mo EST | | |
| Legal (one-time) | ToS/privacy setup EST | | |

## Revenue model validation

From market analysis:

- **ARPU target**: €150–400/mo per account (10–100 units) — label EST
- **Pricing tiers**: Starter (low flat), Pro (per-unit), Scale (custom)
- **No booking commission** — subscription only (aligns with positioning)

### Breakeven sketch

```
Monthly fixed costs (EST) = infra + tools + accounting
Breakeven customers = fixed costs / ARPU
```

Run sensitivity: ARPU ±20%, CAC if known (EST).

## Grants and incentives

| Program | Eligibility notes | Potential value |
|---------|-------------------|-----------------|
| Invitalia Smart&Start | Innovative startup, age/territory conditions | Loan/grant mix |
| Regional (e.g. Lombardia) | Varies | EST — verify locally |
| Credito d'imposta R&S | Documented dev costs | Tax credit |

Flag: grants take time — do not depend for month-1 infra.

## Output shape

- **Financial thesis** (2–4 bullets)
- **Drivers and sensitivities** — main driver named
- **Risks to numbers** — ranked by magnitude
- **Metrics table** — phase | burn | revenue | net

## Planning horizon

- **Months 0–6**: validate wedge, ≤10 paying accounts
- **Months 6–18**: €10k MRR target (EST — challenge builder assumptions)
- **Currency**: EUR

## Sources

Cite `Sessions/decision-hosting-zero-budget.md` for infra $0 path. Mark all projections **EST**.
