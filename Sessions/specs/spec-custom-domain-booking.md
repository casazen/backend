---
id: US-024
slug: custom-domain-booking
title: CasaZen subdomain + custom CNAME booking domains
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [public-site-design-system, tenant-boundary, onboarding-plg]
blocks: [golden-journey-e2e]
exit_contributes_to: Holidu domain model; Starter subdomain vs Pro custom domain
last_reviewed: 2026-08-13
---

# Spec — Custom Domain Booking (US-024)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Hosts publish on **`{slug}.casazen.it`**, **`casazen.it/book/{slug}`**, or **`www.customdomain.it`** (CNAME). Edge resolves `Host` → `OrgId`; booking engine remains CasaZen API. Pro plan gates custom domain.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host on Starter, I want `myvilla.casazen.it` without DNS hassle so that I can launch quickly.

As a host on Pro, I want `www.myvilla.it` to show my brand with no visible CasaZen chrome so that guests perceive it as my own site.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `Org` fields: `PublicHostMode` (`CasazenSubdomain` | `CasazenPath` | `CustomDomain`), `CustomDomain`, `DomainVerificationStatus` (`Pending` | `Verified` | `Failed`), `DomainVerificationToken`.

- **AC2**: `GET /api/public/resolve-host?host=` (`[AllowAnonymous]`) returns `{ orgId, slug, branding, publicHostMode }`; 404 unknown host.

- **AC3**: `POST /api/orgs/{id}/domain` (owner) sets custom domain; returns DNS instructions (CNAME + TXT verification).

- **AC4**: `POST /api/orgs/{id}/domain/verify` checks DNS; updates `DomainVerificationStatus`.

- **AC5**: Plan entitlement: custom domain requires `Plan.Pro` or higher (`403` on Starter).

- **AC6**: Subdomain provisioning: `{slug}.casazen.it` auto-active when `PublicHostMode = CasazenSubdomain` (wildcard DNS on Vercel).

### Frontend — edge

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC7**: Vercel middleware (or Cloudflare Worker): read `Host`, call resolve-host API, inject tenant context (slug/orgId) before React render.

- **AC8**: Custom domain routes serve same `PublicSiteShell` as `/book/{slug}`.

- **AC9**: Onboarding PLG (#271) step: choose host mode (path / subdomain / custom); custom shows DNS panel.

### SSL & infra

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: SSL auto via Vercel Custom Domains; document in `docs/INFRA.md`.

- **AC11**: At least **1 beta host** on custom domain verified in staging + prod.

### Security

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: Host header injection prevented — only registered domains resolve; default domain fallback for unknown hosts.

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `custom-domain-booking`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `Org` fields: `PublicHostMode` (`CasazenSubdomain` / `CasazenPath` / `CustomDomain`), `CustomDomain`, `DomainVerificationStatus` (`Pendin... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GET /api/public/resolve-host?host=` (`[AllowAnonymous]`) returns `{ orgId, slug, branding, publicHostMode }`; 404 unknown host. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `POST /api/orgs/{id}/domain` (owner) sets custom domain; returns DNS instructions (CNAME + TXT verification). | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `POST /api/orgs/{id}/domain/verify` checks DNS; updates `DomainVerificationStatus`. | Outcome not met; wrong status; silent no-op |
| AC5 | L2 + L3 | Plan entitlement: custom domain requires `Plan.Pro` or higher (`403` on Starter). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L1 | Subdomain provisioning: `{slug}.casazen.it` auto-active when `PublicHostMode = CasazenSubdomain` (wildcard DNS on Vercel). | Outcome not met; wrong status; silent no-op |
| AC7 | L1 + L2 + L3 | Vercel middleware (or Cloudflare Worker): read `Host`, call resolve-host API, inject tenant context (slug/orgId) before React render. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L2 + L3 | Custom domain routes serve same `PublicSiteShell` as `/book/{slug}`. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | Onboarding PLG (#271) step: choose host mode (path / subdomain / custom); custom shows DNS panel. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 | SSL auto via Vercel Custom Domains; document in `docs/INFRA.md`. | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | At least **1 beta host** on custom domain verified in staging + prod. | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | Host header injection prevented — only registered domains resolve; default domain fallback for unknown hosts. | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Entities/Org.cs` | Modify — domain fields |
| `Casazen.Web/Controllers/PublicResolveController.cs` | Create |
| `Casazen.Web/Controllers/OrgDomainController.cs` | Create |
| `frontend/middleware.ts` | Create — host resolution |
| `docs/INFRA.md` | Modify — custom domain runbook |

**Complexity:** L  
**Migration:** yes  
**Dependencies:** `spec-public-site-design-system`, `spec-onboarding-plg`

---

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- Multi-domain per org
- Wildcard custom DNS for suppliers (Fase 2)
- Static site export

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
