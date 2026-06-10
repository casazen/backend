# Spec — Enterprise Scale: Multi-Brand, SSO, SLA, Portfolio AI (US-016)

## Overview

Make CasaZen run a 200+-unit, multi-brand agency on the **Scale** tier: multiple **brands**
under one `Org` (each with its own branded direct-booking surface), **SSO** via Auth0
enterprise connections (SAML/OIDC) for staff, an **enterprise SLA** (uptime/support
commitments with monitoring), and **portfolio-level AI pricing rules** that apply pricing
policy across many properties at once (extending the shipped `PricingAdapterConfig` /
`DynamicPricingJob`).

This is a later-phase, breadth-oriented spec: it composes capabilities already introduced
in earlier phases (tenant boundary, org seats, SaaS billing, AI copilot) rather than
inventing new compliance surfaces. Portfolio AI rules **inherit** the EU AI-Act
transparency + hard fair-use constraints defined in `spec-ai-copilot-messaging`.

Reference: **US-016** (Phase 4 — Scale + EU expansion; draft-v3 §B Phase 4 + §C row `spec-enterprise-scale`)
Stage of entry: **Stage 01 Planning** (epic-level macro-spec; splits into issues at Stage 02)

---

## User Story

As an **enterprise agency administrator** running 200+ units across several brands, I want
to organize properties into brands, let my staff sign in with our corporate SSO, rely on a
contractual SLA, and define pricing rules at the portfolio level, so that I can operate at
scale under one CasaZen `Org`.

---

## Acceptance Criteria

### Backend

- **AC1**: New entity `Brand` (carrying `OrgId` per RF1) groups properties under a single
  `Org`; `Property` gains an optional `BrandId`. Multiple brands per `Org`; a property
  belongs to at most one brand. Brand access respects tenant isolation (no cross-`Org` reads).

- **AC2**: Each brand can drive its own branded direct-booking surface (brand name, domain,
  theme), composed over `spec-branded-booking-site`.

- **AC3**: **SSO via Auth0 enterprise connections** — admin can configure a SAML/OIDC
  enterprise connection for the `Org` through the Auth0 Management API; users from that
  connection are provisioned/mapped to `Org` membership with seat-scoped RBAC
  (`spec-org-seats-collaboration`). Connection secrets are never returned to the client.

- **AC4**: SSO is gated to the **Scale** plan entitlement; non-entitled `Org`s receive 403
  on SSO configuration endpoints.

- **AC5**: **Portfolio-level AI pricing rules** — an admin defines a rule set (e.g. floor/
  ceiling, weekend uplift, min-margin, event multipliers) at the `Org`/`Brand` scope; the
  `DynamicPricingJob` applies the resolved rule to every property in scope, and per-property
  overrides still win. Each adjustment logs the rule + confidence (reuses the existing
  pricing audit trail).

- **AC6**: **Enterprise SLA monitoring** — health/uptime metrics are recorded and an SLA
  summary endpoint (`GET /api/enterprise/sla`) reports uptime %, incident count, and the
  current support tier for the `Org` (extends `HealthController`).

- **AC7 (Regression — AI guardrails)**: Portfolio AI rules inherit the hard fair-use caps
  and AI-Act transparency from `spec-ai-copilot-messaging` — a test asserts portfolio pricing
  cannot bypass confidence gating or the cost/disclosure controls.

### Frontend

- **AC8**: Brand management page (`/enterprise/brands`) — create/edit brands, assign
  properties, set brand theme/domain; reflects multi-brand grouping.

- **AC9**: SSO configuration UI (`/enterprise/sso`) — set up the enterprise connection,
  show connection status and the test-login result; **no secrets rendered**.

- **AC10**: SLA dashboard (`/enterprise/sla`) — uptime %, incidents, support tier, reading AC6.

- **AC11**: Portfolio pricing-rules UI (`/enterprise/pricing-rules`) — define `Org`/brand-scoped
  rules, preview affected properties, with an AI-Act/fair-use disclosure surfaced (AC7).

- **AC12**: All `/enterprise/*` routes wrapped in `<ProtectedRoute>` and gated to admins of a
  Scale-entitled `Org`; non-entitled users see an upgrade prompt rather than the tools.

---

## Technical Notes

### Backend — Files to create/modify

| File | Action |
|---|---|
| `Casazen.Core/Entities/Brand.cs` | Create (new module) — `OrgId` FK, name, domain, theme |
| `Casazen.Core/Entities/Property.cs` | Modify — add optional `BrandId` FK |
| `Casazen.Core/Entities/SlaMetric.cs` | Create (new module) — uptime/incident records; `OrgId` |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — DbSets + relationships + `OrgId` filters |
| `Casazen.Infrastructure/Migrations/` | Create — migration `AddEnterpriseScale` (rebase on `AppDbContextModelSnapshot.cs`) |
| `Casazen.Core/Services/IAuth0ManagementService.cs` | Modify — add enterprise-connection (SAML/OIDC) operations |
| `Casazen.Infrastructure/Services/Auth0ManagementService.cs` | Modify — implement enterprise-connection provisioning/mapping |
| `Casazen.Core/Entities/PricingAdapterConfig.cs` | Modify — support `Org`/`Brand`-scoped rule sets |
| `Casazen.Infrastructure/Services/PricingAdapterService.cs` | Modify — resolve portfolio rule → per-property (overrides win) |
| `Casazen.Web/BackgroundJobs/DynamicPricingJob.cs` | Modify — apply portfolio rules in scope; keep confidence logging |
| `Casazen.Web/Controllers/EnterpriseController.cs` | Create (new module) — brands, SSO config, SLA, pricing rules |
| `Casazen.Web/Controllers/HealthController.cs` | Modify — feed SLA uptime/incident metrics |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — Scale-tier entitlement policy for `/enterprise/*` |

### Frontend — Files to create/modify

| File | Action |
|---|---|
| `src/features/enterprise/brand-management-page.tsx` | Create (new module) |
| `src/features/enterprise/sso-config-page.tsx` | Create (new module) — no secrets rendered |
| `src/features/enterprise/sla-dashboard-page.tsx` | Create (new module) |
| `src/features/enterprise/portfolio-pricing-page.tsx` | Create (new module) — AI-Act/fair-use disclosure |
| `src/features/enterprise/components/brand-form.tsx` | Create (new module) |
| `src/features/enterprise/components/sla-kpi-card.tsx` | Create (new module) |
| `src/api/enterprise.api.ts` | Create (new module) |
| `src/queries/use-enterprise.ts` | Create (new module) |
| `src/types/enterprise.types.ts` | Create (new module) |
| `src/routes/index.tsx` | Modify — add `/enterprise/*` under `<ProtectedRoute>` + Scale gate |

---

## Compliance

- **DPA / SLA**: enterprise data-processing agreement and SLA terms (uptime, support response,
  incident handling) accompany the Scale tier; SLA monitoring evidences the commitment. **[COUNSEL_REQUIRED]** on DPA/SLA contractual wording.
- **SSO security**: SAML/OIDC enterprise connections configured via Auth0 Management API;
  least-privilege seat RBAC on provisioned users; connection secrets never leave the server
  (no secret in API responses or UI — AC3/AC9).
- **AI portfolio rules inherit AI-Act + fair-use**: portfolio-level pricing rules are bound by
  the EU AI-Act transparency disclosure and the hard fair-use caps defined in
  `spec-ai-copilot-messaging` (confidence gating, cost caps, logged decisions) — AC7 presides.
- **Tenant isolation across brands**: brands never cross the `OrgId` boundary (RF1); no
  cross-`Org` data access.

---

## Dependencies

- **Requires**: **all prior phases** — `spec-tenant-boundary` (`OrgId`/entitlement),
  `spec-org-seats-collaboration` (seat RBAC for SSO users), `spec-saas-billing` (Scale-tier
  entitlement), `spec-branded-booking-site` (per-brand surface), `spec-ai-copilot-messaging`
  (AI-Act + fair-use constraints inherited by portfolio rules).
- **Blocks**: the Phase 4 exit criterion "a 200+-unit multi-brand agency runs on Scale".
- **Related**: `spec-eu-compliance-es-fr` (sibling Phase 4 spec); the AI pricing audit trail
  (`PricingHistory`, confidence scoring) reused for portfolio rule logging.
