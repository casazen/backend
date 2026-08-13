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
last_reviewed: 2026-08-13
---

# Spec — Supplier Public Site (US-027)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Public **vetrina** for active suppliers: services, service area (comuni), photos, reviews placeholder, CTA to contact or request via host console. Same `PublicSiteShell` as host sites. MVP may ship minimal v0 in Fase 1 if time; full spec targets Fase 2.

**Phase:** 2 (Fase 1 min v0 optional) · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host, I want to browse supplier profiles before sending a request so that I choose a trusted partner.

As a supplier, I want a professional public page linked from my console so that hosts find me outside the inbox.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `GET /api/public/suppliers/{slug}` (`[AllowAnonymous]`) returns public DTO: name, bio, categories, comuni, photos, `status` (only `Active` returns 200).

- **AC2**: Slug on `SupplierProfile.PublicSlug` unique globally.

- **AC3**: Optional `PublicHostMode` for supplier org (subdomain `pulizie-roma.casazen.it`) — reuse resolve-host pattern from `spec-custom-domain-booking` (Fase 2).

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC4**: Route `/fornitori/{slug}` with `PublicSiteShell` template variant (supplier hero + service cards).

- **AC5**: CTA "Richiedi servizio" visible only when viewer is authenticated host in overlapping comune; else "Contatta" mailto/form.

- **AC6**: Mobile-first; LCP < 2.5s.

### v0 minimum (if Fase 1)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC7**: Single-page profile without custom domain; linked from supplier console "Anteprima vetrina".

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



1. Enter the primary route for `supplier-public-site`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `GET /api/public/suppliers/{slug}` (`[AllowAnonymous]`) returns public DTO: name, bio, categories, comuni, photos, `status` (only `Active... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | Slug on `SupplierProfile.PublicSlug` unique globally. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | Optional `PublicHostMode` for supplier org (subdomain `pulizie-roma.casazen.it`) — reuse resolve-host pattern from `spec-custom-domain-bo... | Outcome not met; wrong status; silent no-op |
| AC4 | L2 + L3 | Route `/fornitori/{slug}` with `PublicSiteShell` template variant (supplier hero + service cards). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC5 | L2 + L3 | CTA "Richiedi servizio" visible only when viewer is authenticated host in overlapping comune; else "Contatta" mailto/form. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L2 + L3 | Mobile-first; LCP < 2.5s. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L2 + L3 | Single-page profile without custom domain; linked from supplier console "Anteprima vetrina". | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Web/Controllers/PublicSuppliersController.cs` | Create |
| `frontend/src/routes/fornitori/` | Create |
| `SupplierProfile` | Modify — `PublicSlug`, public fields |

**Complexity:** M  
**Migration:** yes — slug column  
**Dependencies:** `spec-supplier-console-web`, `spec-public-site-design-system`

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

- Public directory/search by comune (separate `supplier-directory` idea)
- Review system with moderation
- Supplier custom CNAME (Fase 2 optional)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
