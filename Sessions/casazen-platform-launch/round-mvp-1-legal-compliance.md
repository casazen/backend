## Legal & Compliance Advisor — Round MVP-1 Response

**Vote**: APPROVE

**Reasoning**:
MVP execution sequencing respects binding gates from `decision-after-devils-review.md`. No legal objection to parallel development of branded-site backend and connect-onboarding, provided the **public-funnel gate** (revised infra trigger) is enforced before any property becomes publicly bookable.

**Details**:

### Binding gates — execution order
| Gate | When | MVP impact |
|------|------|------------|
| **C2 — P.IVA + SDI** | Before first **CasaZen subscription charge** | Blocks `spec-saas-billing` production, not guest checkout demo |
| **Connect onboarding gate** | Before branded site publish / guest payment | Blocks `spec-direct-checkout` + AC11 publish |
| **Public-funnel gate** | Before property publicly bookable | Alloggiati cron proven OR ~€5/mo tier active |
| **DPA / AI Act** | At `spec-onboarding-plg` signup | Blocks self-serve PLG production, not manual onboarding |

### Demo vs production
| Capability | Demo (design partners) | Production sellable |
|------------|------------------------|---------------------|
| Branded site browse | ✅ After read-model release | ✅ |
| Guest checkout | ✅ With Connect test mode + KYC | ✅ Requires live Connect |
| CasaZen subscription | Manual plan assignment (AC11b) | ❌ Needs P.IVA + SDI + `spec-saas-billing` |
| Alloggiati auto-fire | ⚠️ Test properties only until funnel gate | ✅ Requires cron proof or €5 tier |

### Sequencing counsel
1. **Connect onboarding** before any live guest payment — non-negotiable (MoR, PSD2).
2. **Do not** mark properties `IsActive` for public booking until Alloggiati cron is proven (DA #2).
3. **`spec-onboarding-plg`** can trail checkout for design-partner MVP if onboarding is operator-assisted.
4. **`spec-saas-billing`** requires `[COUNSEL_REQUIRED]` sign-off on IVA/OSS rates before first invoice.

### Objections
None blocking. Flag: if `feature/215` merges publish UI before Connect, add AC11 gate in same PR.
