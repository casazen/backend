## Financial Strategist — Round MVP-1 Response

**Vote**: APPROVE

**Reasoning**:
Partial MVP (direct booking without subscription billing) is financially viable for **design-partner validation** using manual plan assignment (`spec-tenant-boundary` AC11b). Full sellable gate still requires `spec-saas-billing`. Infra remains at **$0** until first publicly-bookable property per DA amendment — current pipeline work does not trigger upgrade.

**Details**:

### Demo MVP without saas-billing?
**Yes** — for 3–5 design partners:
- Operator gets `Org` + manual `PlanTier` via admin/plan settings.
- Guest checkout via Connect (test → live).
- CasaZen revenue = $0 until billing ships — acceptable for 4–8 week validation window.
- **Not sellable** at scale without subscription + SDI invoicing.

### Infra cost during pipeline
| Phase | Tier | Trigger |
|-------|------|---------|
| Now (specs 1–3 in dev) | $0 | No public bookable property |
| First public bookable / confirmed booking | ~€5/mo Railway Hobby | DA-revised trigger |
| Stripe Connect | $0 platform fee on Express; 2.9%+€0.25 per txn on connected account | Operator bears |

### Unit economics (partial MVP)
- **GM positive** on guest txn: CasaZen takes no booking commission (F6).
- **Burn**: dev time only at $0 infra — aligned with zero-budget strategy.
- **Risk**: shipping checkout without billing delays revenue 4–6 weeks but preserves wedge demo.

### Recommended financial sequencing
1. connect-onboarding (enables txn proof for investor/partner demos)
2. direct-checkout (first measurable GMV signal — operator-side)
3. saas-billing (revenue capture — parallelize planning while 1–2 ship)
4. onboarding-plg (CAC reduction — can trail billing 2 weeks)

### Stripe Connect costs
Express accounts: no monthly Connect fee for platform; Stripe processing on connected account. Budget €0 incremental infra until funnel gate.
