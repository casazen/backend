# CasaZen — Implementation Roadmap (current state → sellable production)

> **Superseded for day-to-day planning** — use [`../PLANNING.md`](../PLANNING.md) + [`../specs/README.md`](../specs/README.md).  
> This file remains the historical council consensus (Round 3, 2026-06-05).

> **Council**: Platform Launch · **Status**: consensus (Round 3) · **Date**: 2026-06-05
> **Source**: `draft-v3.md` §B
> Each macro-spec lives at `Sessions/specs/spec-{slug}.md` in the `spec-property-detail.md` shape. Infra tiers use the **$0 → ~€5/mo** trigger from `decision-hosting-zero-budget.md`, **keyed to first real guest check-in**.
> **[FACT]** = Coordinator-verified codebase / project doc. **[EST]** = estimate.

---

## Phase 0 — Now (verified baseline) **[FACT]**

**Scale:** **22 entities · 16 controllers · 10 Hangfire jobs · Supabase PostgreSQL** (not SQL Server). React 19 SPA (feature-slice, TanStack Query, Auth0), Vercel FE. Layered `Core`/`Infrastructure`/`Web`.

**Shipped subsystems:**
- **OTA sync, 6 channels** — `IChannelAdapter` (+ `ChannelFactory`), Polly resilience; `OtaIntegration`, `OtaSyncLog`; jobs `OtaSyncJob`, `BookingPullJob`.
- **Italian STR compliance** — CIN (`CinCodeAttribute`), Alloggiati Web (`AlloggiatiWebReport` + `AlloggiatiWebReportJob`, 24h), tourist tax (`TouristTaxRate`/`TaxRate`), GDPR (`GdprController` + `GdprDataRetentionJob`).
- **LTR lease subsystem (substantially built)** — `LeaseContract` (incl. `MonthlyRent`, `FiscalRegime`, `RegistrationDeadline`, `Parties`, `Registration`, `Events`, `HasExtraEUTenant`), `LeaseRegistration` (**RLI via Openapi.it Docuengine**), `Party`, `LeaseEvent`; `LeasesController` (**create → e-sign → RLI-register → receipt**); `LeaseWorkflowService`; jobs `LeaseSignStatusPollingJob`, `LeaseRegistrationStatusPollingJob`, `ESignWebhookJob`.
- **Context-scoped RBAC** — `AppContext`, `UserContextMembership`, `Role`, `RolePermission`, `ContextAuthorizationService`; policy convention **`RequireContext:{context}:{permission}`** (e.g. `RequireContext:short-rent:property.write`); short-rent / long-rent layers.
- **Payments** — Stripe processing + signature-verified webhooks (`WebhooksController`, `StripeWebhookJob`), partial refunds.
- **AI pricing** — `PricingAdapterConfig`/`PricingHistory`, confidence scoring; `DynamicPricingJob`.
- **Admin/users** — `AdminController`, `UsersController`, `MeController`, `AuthController`; Auth0 Management API; `User.RentalType`.

**Phase 0 reality flags:**
1. **Docs drift [FACT]** — `PROJECT.md`/`TECHNICAL.md` say SQL Server / 14 entities / 12 controllers; stale. Docs-update = Phase 0 hygiene item.
2. **No SaaS self-billing [FACT]** — Stripe charges *guests*; no subscription billing for CasaZen's own customers → `spec-saas-billing` is the hard "sellable" gate.
3. **No tenant boundary [FACT]** — context-RBAC exists, but data is per-`OwnerId` + context-scoped, **not Org-isolated** → `spec-tenant-boundary`.
4. **Direct booking = seed, not shortcut [FACT]** — `GET /api/properties/search` is `[AllowAnonymous]` **but returns the raw `Property` incl. `OwnerId`**; a public read-model is required before any public surface.
5. **LTR recurring-rent gap [FACT]** — `LeaseContract.MonthlyRent` is static; **no recurring-rent ledger/job** — the true LTR gap.

**Infra tier:** **$0** (Render/Railway Free + GH Actions cron); the cron firing Alloggiati + GDPR endpoints must be **proven** while at $0 (R5).

---

## Phase 1 — MVP Sellable: Direct Booking + Tenant Boundary + Billing

- **Goals:** make CasaZen *buyable and sellable* — a public commission-free direct-booking engine, an `Org` tenant boundary, and subscription billing for CasaZen itself.
- **Specs:** `spec-public-booking-readmodel`, `spec-direct-checkout`, `spec-branded-booking-site`, `spec-tenant-boundary`, `spec-saas-billing`, `spec-onboarding-plg`.
- **Dependencies:** anonymous search (seed) → public DTO; Stripe → **Stripe Connect (operator MoR)**; `spec-admin-backend`/`spec-role-onboarding`; context-RBAC.
- **Entry gate (Legal C2):** **P.IVA + SDI e-invoicing live before the first charge**.
- **Internal ordering:** `spec-public-booking-readmodel` → `spec-direct-checkout`; `spec-tenant-boundary` → `spec-branded-booking-site` (per-Org branding) & → `spec-saas-billing` (entitlement).
- **Exit criteria:** an external PM self-onboards, gets an `Org`, publishes a branded direct-booking site, takes a **commission-free** booking + guest payment (**operator = MoR via Stripe Connect**), and **pays CasaZen a subscription** (correct IVA/OSS + SDI invoice); compliance (CIN/Alloggiati/tax) auto-fires.
- **Infra:** **$0 demo/test → ~€5/mo Railway Hobby at first real guest check-in** (R5).

---

## Phase 1.5 — LTR Complete + Verify (PARALLEL to Phase 1)

> Depends on **neither billing nor multi-tenancy** → runs in parallel. The LTR engine exists; this phase closes the one real gap, adds the FE, verifies the existing flow, and reframes RLI as assisted.

- **Goals:** ship recurring-rent billing; build the LTR frontend over `LeasesController`; verify create→e-sign→RLI→receipt E2E; frame RLI as operator-attended.
- **Specs:** `spec-ltr-recurring-rent`, `spec-ltr-frontend`, `spec-ltr-verification`, `spec-ltr-rli-registration` (rescoped/assisted).
- **Dependencies:** existing `LeaseContract`/`LeaseRegistration`/`LeaseWorkflowService`/lease jobs; Stripe **Connect** (recurring); context `long-rent` RBAC.
- **Exit criteria:** a `long-rent` landlord generates a (counsel-reviewed) contract, e-signs, gets **assisted** RLI registration + cedolare decision support + 30-day deadline checklist, and the platform **automatically bills recurring monthly rent** via a new ledger + Hangfire job, with the **landlord as merchant of record (Connect)**.
- **Infra:** **~€5/mo** (jobs reuse Hangfire).

---

## Phase 2 — Operations AI Copilot: Unified Inbox + AI Messaging + Org Seats

- **Goals:** attack booking-window compression (F4) — unified inbox (OTA + direct), AI messaging copilot with hard fair-use caps, org seats/collaboration.
- **Specs:** `spec-unified-inbox`, `spec-ai-copilot-messaging`, `spec-org-seats-collaboration`.
- **Dependencies:** OTA adapters (inbound — *per-adapter, varies*), direct-booking guests (Phase 1), AI provider, `spec-tenant-boundary` + existing `UserContextMembership`/`RequireContext`.
- **Exit criteria:** all guest comms in one inbox; AI drafts replies (cheap-model default, confidence-gated frontier, cached, metered) keeping **AI ≤10–15% ARPU / GM ≥80%**; team members invited with seat-scoped RBAC.
- **Infra:** **~€5/mo** (always-on inbox webhooks) + metered, capped AI cost.

---

## Phase 3 — Distribution + Marketplace

- **Goals:** open the second revenue stream (F10) and widen direct distribution — supplier marketplace (cleaning/maintenance/photography) with a take-rate; Google Vacation Rentals.
- **Specs:** `spec-supplier-marketplace`, `spec-google-vacation-rentals`.
- **Dependencies:** `spec-tenant-boundary`, Stripe Connect payouts, `spec-direct-checkout` (GVR feeds direct).
- **Exit criteria:** a marketplace transaction completes with platform take-rate; a property is discoverable + bookable via GVR.
- **Infra:** **~€5/mo +** (Connect payouts/escrow).

---

## Phase 4 — Scale + EU expansion

- **Goals:** multi-brand, enterprise SLA/SSO, portfolio AI, **ES/FR compliance modules** (replicate IT compliance + lease pattern per market).
- **Specs:** `spec-enterprise-scale`, `spec-eu-compliance-es-fr`.
- **Dependencies:** all prior; localized regulatory research (Legal).
- **Exit criteria:** a 200+-unit multi-brand agency runs on Scale; first non-Italian market live with native compliance.
- **Infra:** scale-up beyond Hobby as load dictates.

---

## Gap mapping — current vs vision (verified baseline)

| Vision capability (F5/F7) | Verified current state **[FACT]** | Real gap | Phase | Spec(s) |
|---------------------------|-----------------------------------|----------|:---:|---------|
| Direct-booking public surface | Anonymous `/search` returns raw `Property` (incl `OwnerId`) | Public DTO/read-model | 1 | `spec-public-booking-readmodel` |
| Direct guest checkout | Stripe (guest charges) exists | Guest booking + Stripe **Connect** checkout + compliance auto-fire | 1 | `spec-direct-checkout` |
| Branded booking website | None | Public FE booking surface | 1 | `spec-branded-booking-site` |
| Sell to PM teams (tenancy) | per-`OwnerId` + context-RBAC, no Org | `Org` tenant key + `OrgId` FK + entitlement | 1 | `spec-tenant-boundary` |
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

---

## Cross-cutting engineering invariants (Round 3 refinements)

- **(RF1) Tenant-scoped invariant** — `spec-tenant-boundary` mandates that **every new tenant-scoped table carries `OrgId` and honors plan entitlement**; the Phase 1.5 rent ledger is explicitly bound by this.
- **(RF2) Stripe webhook routing** — `spec-saas-billing` (platform-account Billing) vs `spec-direct-checkout` / `spec-ltr-recurring-rent` (connected-account Connect) share `WebhooksController`/`StripeWebhookHandler`; each spec must separate **platform-account vs connected-account** event routing (per-source signature verification, async `StripeWebhookJob`).
- **(RF3) Migration sequencing** — land `spec-tenant-boundary`'s `OrgId` migration first (nullable → backfill one default `Org` per `OwnerId` → NOT NULL + FK). Phase 1.5 migrations **rebase** onto the updated `AppDbContextModelSnapshot.cs` (never hand-merge); new Phase 1.5 tables carry `OrgId` from creation. Phase 1 and Phase 1.5 stay parallel in code/design — only migration order is serialized.

---

## Macro-spec index (17 specs)

| # | Spec | Phase |
|---|------|:---:|
| 1 | `spec-public-booking-readmodel` | 1 |
| 2 | `spec-direct-checkout` | 1 |
| 3 | `spec-branded-booking-site` | 1 |
| 4 | `spec-tenant-boundary` | 1 |
| 5 | `spec-saas-billing` | 1 |
| 6 | `spec-onboarding-plg` | 1 |
| 7 | `spec-ltr-recurring-rent` | 1.5 |
| 8 | `spec-ltr-frontend` | 1.5 |
| 9 | `spec-ltr-verification` | 1.5 |
| 10 | `spec-ltr-rli-registration` | 1.5 |
| 11 | `spec-unified-inbox` | 2 |
| 12 | `spec-ai-copilot-messaging` | 2 |
| 13 | `spec-org-seats-collaboration` | 2 |
| 14 | `spec-supplier-marketplace` | 3 |
| 15 | `spec-google-vacation-rentals` | 3 |
| 16 | `spec-enterprise-scale` | 4 |
| 17 | `spec-eu-compliance-es-fr` | 4 |
