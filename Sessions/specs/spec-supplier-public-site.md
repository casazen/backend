---
id: US-027
slug: supplier-public-site
title: Supplier public vetrina (marketing page)
phase: 2
type: feature
priority: P1
status: specced
issue:
depends_on: [supplier-console-web, public-site-design-system]
blocks: []
exit_contributes_to: Supplier discovery; ecosystem public surfaces
last_reviewed: 2026-06-19
---

# Spec — Supplier Public Site (US-027)

## Overview

Public **vetrina** for active suppliers: services, service area (comuni), photos, reviews placeholder, CTA to contact or request via host console. Same `PublicSiteShell` as host sites. MVP may ship minimal v0 in Fase 1 if time; full spec targets Fase 2.

**Phase:** 2 (Fase 1 min v0 optional) · **Type:** feature · **Status:** specced

---

## User Story

As a host, I want to browse supplier profiles before sending a request so that I choose a trusted partner.

As a supplier, I want a professional public page linked from my console so that hosts find me outside the inbox.

---

## Acceptance Criteria

### Backend

- **AC1**: `GET /api/public/suppliers/{slug}` (`[AllowAnonymous]`) returns public DTO: name, bio, categories, comuni, photos, `status` (only `Active` returns 200).

- **AC2**: Slug on `SupplierProfile.PublicSlug` unique globally.

- **AC3**: Optional `PublicHostMode` for supplier org (subdomain `pulizie-roma.casazen.it`) — reuse resolve-host pattern from `spec-custom-domain-booking` (Fase 2).

### Frontend

- **AC4**: Route `/fornitori/{slug}` with `PublicSiteShell` template variant (supplier hero + service cards).

- **AC5**: CTA "Richiedi servizio" visible only when viewer is authenticated host in overlapping comune; else "Contatta" mailto/form.

- **AC6**: Mobile-first; LCP < 2.5s.

### v0 minimum (if Fase 1)

- **AC7**: Single-page profile without custom domain; linked from supplier console "Anteprima vetrina".

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Web/Controllers/PublicSuppliersController.cs` | Create |
| `frontend/src/routes/fornitori/` | Create |
| `SupplierProfile` | Modify — `PublicSlug`, public fields |

**Complexity:** M  
**Migration:** yes — slug column  
**Dependencies:** `spec-supplier-console-web`, `spec-public-site-design-system`

---

## Out of Scope

- Public directory/search by comune (separate `supplier-directory` idea)
- Review system with moderation
- Supplier custom CNAME (Fase 2 optional)
