## GTM Strategist — Round MVP-1 Response

**Vote**: PROPOSE

**Reasoning**:
Phase 1 deliberation (Rounds 1–3 + DA review) is complete; macro-specs exist. MVP execution is now a **pipeline problem**, not a strategy problem. Two of seven Phase 1 specs have meaningful progress (`tenant-boundary` shipped v1.1.6; `public-booking-readmodel` dev-complete pending review/release). The critical path to the sellable exit — *self-onboard → publish branded site → take commission-free booking → pay CasaZen subscription* — is blocked at **Stripe Connect onboarding**, not at GTM positioning.

**Details**:

### MVP exit criteria (recap)
External PM self-onboards, gets an `Org`, publishes a branded direct-booking site, takes a commission-free booking (operator = MoR via Connect), pays CasaZen subscription (IVA/OSS + SDI), compliance auto-fires.

### Progress vs exit (~22% engineering complete)
| Spec | Status | Weight |
|------|--------|--------|
| `spec-tenant-boundary` | ✅ Released v1.1.6 | 15% |
| `spec-public-booking-readmodel` | 🟡 PR #213/#112, review pending | 10% |
| `spec-connect-onboarding` | ❌ Not started — **critical blocker** | 15% |
| `spec-direct-checkout` | ❌ Not started | 20% |
| `spec-branded-booking-site` | 🟡 WIP (`PublicOrgController`, integration tests) | 15% |
| `spec-saas-billing` | ❌ Not started | 15% |
| `spec-onboarding-plg` | ❌ Not started | 10% |

### Recommended next 3 pipeline specs (strict order)
1. **`spec-connect-onboarding`** — unblocks checkout and publish gate (DA #1/#16). No guest payment without `charges_enabled`.
2. **`spec-public-booking-readmodel`** — finish review/release of PR #213/#112 (already built; low marginal cost).
3. **`spec-direct-checkout`** — revenue moment; depends on connect + read-model.

`spec-branded-booking-site` continues in parallel for **backend AC1–AC3** and **FE shell/routes**, but **publish/live gate** waits on connect-onboarding AC10.

### Critical path
`tenant-boundary` → `connect-onboarding` → (`public-booking-readmodel` release) → `direct-checkout` → `branded-booking-site` publish → `saas-billing` + `onboarding-plg` (sellable gate).

### GTM risks (partial MVP)
- **Demo without billing**: acceptable for design partners; not sellable (F6 zero-commission promise needs working checkout).
- **Site live without Connect**: guest hits 409 at checkout — brand damage. Gate publish on `charges_enabled`.
- **Competitor catch-up**: 12–24 month lead-time (AD-9); delay on Connect erodes wedge.
