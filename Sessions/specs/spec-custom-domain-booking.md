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
last_reviewed: 2026-06-19
---

# Spec — Custom Domain Booking (US-024)

## Overview

Hosts publish on **`{slug}.casazen.it`**, **`casazen.it/book/{slug}`**, or **`www.customdomain.it`** (CNAME). Edge resolves `Host` → `OrgId`; booking engine remains CasaZen API. Pro plan gates custom domain.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a host on Starter, I want `myvilla.casazen.it` without DNS hassle so that I can launch quickly.

As a host on Pro, I want `www.myvilla.it` to show my brand with no visible CasaZen chrome so that guests perceive it as my own site.

---

## Acceptance Criteria

### Backend

- **AC1**: `Org` fields: `PublicHostMode` (`CasazenSubdomain` | `CasazenPath` | `CustomDomain`), `CustomDomain`, `DomainVerificationStatus` (`Pending` | `Verified` | `Failed`), `DomainVerificationToken`.

- **AC2**: `GET /api/public/resolve-host?host=` (`[AllowAnonymous]`) returns `{ orgId, slug, branding, publicHostMode }`; 404 unknown host.

- **AC3**: `POST /api/orgs/{id}/domain` (owner) sets custom domain; returns DNS instructions (CNAME + TXT verification).

- **AC4**: `POST /api/orgs/{id}/domain/verify` checks DNS; updates `DomainVerificationStatus`.

- **AC5**: Plan entitlement: custom domain requires `Plan.Pro` or higher (`403` on Starter).

- **AC6**: Subdomain provisioning: `{slug}.casazen.it` auto-active when `PublicHostMode = CasazenSubdomain` (wildcard DNS on Vercel).

### Frontend — edge

- **AC7**: Vercel middleware (or Cloudflare Worker): read `Host`, call resolve-host API, inject tenant context (slug/orgId) before React render.

- **AC8**: Custom domain routes serve same `PublicSiteShell` as `/book/{slug}`.

- **AC9**: Onboarding PLG (#271) step: choose host mode (path / subdomain / custom); custom shows DNS panel.

### SSL & infra

- **AC10**: SSL auto via Vercel Custom Domains; document in `docs/INFRA.md`.

- **AC11**: At least **1 beta host** on custom domain verified in staging + prod.

### Security

- **AC12**: Host header injection prevented — only registered domains resolve; default domain fallback for unknown hosts.

---

## Technical Notes

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

## Out of Scope

- Multi-domain per org
- Wildcard custom DNS for suppliers (Fase 2)
- Static site export
