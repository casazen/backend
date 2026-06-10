## Product Architect — Round MVP-1 Response

**Vote**: APPROVE

**Reasoning**:
Dependency order from DA-amended roadmap is technically sound. `spec-tenant-boundary` landed correctly (`Org`, `OrgId`, `StripeConnectedAccountId` field, entitlement). Codebase inspection confirms `PublicOrgController` implements branded-site backend AC1–AC3 on `feature/215-branded-booking-site`. The next hard dependency is Connect onboarding — `StripeConnectedAccountId` exists but no account-creation/KYC flow.

**Details**:

### Dependency validation
| Edge | Verdict |
|------|---------|
| tenant-boundary → connect-onboarding | ✅ `Org.StripeConnectedAccountId` ready |
| connect-onboarding → direct-checkout | ✅ AC5 charge gate required |
| public-booking-readmodel → direct-checkout | ✅ DTOs exist; PR pending merge |
| tenant-boundary → branded-booking-site | ✅ `GetPublicBySlugAsync`, `SearchByOrgAsync` wired |
| connect-onboarding → branded publish | ✅ AC10 gate — site must not go live without `charges_enabled` |

### Branded-booking-site before connect-onboarding?
**Yes for development, no for production publish.** Backend public org endpoints and FE route shell (`/book/:orgSlug`) can proceed in parallel. AC11 (publish action) must check Connect status — implement stub gate now, wire to real `GET /api/connect/status` when connect spec lands.

### RF1–RF3 risks
- **RF1**: New Connect status fields on `Org` must carry `OrgId` (same entity — low risk).
- **RF2**: `WebhooksController` must add Connect route with separate `Stripe:ConnectWebhookSecret` — do not mix with platform billing webhooks.
- **RF3**: Connect migration rebases on post-tenant-boundary snapshot — correct order.

### Codebase gaps vs spec ACs
| Spec | Gap |
|------|-----|
| connect-onboarding | No `ConnectController`, `StripeConnectService`, capability flags on `Org` |
| direct-checkout | No `POST /api/public/bookings`, no Connect `PaymentIntent` |
| branded-booking-site | BE partial (controller + tests); FE public route tree not verified |
| saas-billing | No `StripeBillingService`, no subscription webhooks |
| onboarding-plg | No `EnsureOrgForUser` activation checklist integration |

### Recommendation
Pause branded-site **publish** work; prioritize `spec-connect-onboarding` pipeline issue next. Merge `public-booking-readmodel` PRs through Stage 04–05 in parallel.
