---
id: US-023
slug: public-site-design-system
title: Marketing-grade public site shell (host + SEO)
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [public-booking-readmodel, branded-booking-site]
blocks: [seo-funnel, custom-domain-booking, golden-journey-e2e]
exit_contributes_to: Holidu-quality guest UX; GJ step 4 booking surface
last_reviewed: 2026-08-13
---

# Spec — Public Site Design System (US-023)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Replace console-looking `/book/{slug}` with a **marketing-grade** `PublicSiteShell`: hero, gallery, editorial typography, mobile-first guest UX. Shared by host booking sites and SEO comune pages. Evolves `spec-branded-booking-site` APIs — UI rewrite only on public routes.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a guest, I want a beautiful property site that feels like the host's brand so that I trust booking directly.

As a host, I want to choose a visual template and see my photos prominently without looking like an admin tool.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Design system

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `PublicSiteShell` layout separate from `AppShell` — no host nav, no admin chrome.

- **AC2**: At least **1 premium template** (MVP); architecture supports 3 themes (Mare/Montagna/Urban) — config `Org.PublicThemeId`.

- **AC3**: Components: `Hero`, `PropertyGallery`, `AmenityGrid`, `BookingWidget` (sticky on mobile), `Footer` (Powered by on Starter).

- **AC4**: LCP target < 2.5s on 4G — optimized images (`next/image` or Vite equivalent), lazy below fold.

- **AC5**: WCAG 2.1 AA contrast on primary CTA; focus states on booking widget.

### Host booking site

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC6**: `/book/{slug}` and property detail use new shell; existing public API unchanged (`spec-public-booking-readmodel`).

- **AC7**: Booking widget calls direct checkout flow (US-002); price breakdown + GDPR consent in Italian.

- **AC8**: Mobile: full-width gallery swipe; booking CTA fixed bottom bar.

### SEO comune pages

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC9**: `SeoContentPage` FE routes use same typography, colors, and CTA button styles as host sites.

- **AC10**: CTA links to host signup or featured properties — funnel tracked (see `spec-seo-funnel`).

### Branding API

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: Consume `GET /api/public/orgs/{slug}` branding: `logoUrl`, `primaryColor`, `heroImageUrl`, `publicThemeId`, `tagline`.

- **AC12**: Host console preview (Should): link "Anteprima sito" opens public URL in new tab.

### Regression

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: Anonymous checkout E2E (golden journey step 4) passes on redesigned UI.

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `PublicSiteShell` layout separate from `AppShell` — no host nav, no admin chrome. | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | At least **1 premium template** (MVP); architecture supports 3 themes (Mare/Montagna/Urban) — config `Org.PublicThemeId`. | Outcome not met; wrong status; silent no-op |
| AC3 | L2 + L3 | Components: `Hero`, `PropertyGallery`, `AmenityGrid`, `BookingWidget` (sticky on mobile), `Footer` (Powered by on Starter). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC4 | L1 + L2 + L3 | LCP target < 2.5s on 4G — optimized images (`next/image` or Vite equivalent), lazy below fold. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC5 | L2 + L3 | WCAG 2.1 AA contrast on primary CTA; focus states on booking widget. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L1 | `/book/{slug}` and property detail use new shell; existing public API unchanged (`spec-public-booking-readmodel`). | Outcome not met; wrong status; silent no-op |
| AC7 | L1 + L2 + L3 | Booking widget calls direct checkout flow (US-002); price breakdown + GDPR consent in Italian. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L2 + L3 | Mobile: full-width gallery swipe; booking CTA fixed bottom bar. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | `SeoContentPage` FE routes use same typography, colors, and CTA button styles as host sites. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | CTA links to host signup or featured properties — funnel tracked (see `spec-seo-funnel`). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L1 | Consume `GET /api/public/orgs/{slug}` branding: `logoUrl`, `primaryColor`, `heroImageUrl`, `publicThemeId`, `tagline`. | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | Host console preview (Should): link "Anteprima sito" opens public URL in new tab. | Outcome not met; wrong status; silent no-op |
| AC13 | L2 + L3 | Anonymous checkout E2E (golden journey step 4) passes on redesigned UI. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `frontend/src/layouts/PublicSiteShell.tsx` | Create |
| `frontend/src/features/public-site/` | Create — templates + components |
| `frontend/src/routes/book/` | Modify — use PublicSiteShell |
| `frontend/src/features/seo/` | Modify — align styles |
| `frontend/src/styles/public-tokens.css` | Create — design tokens |

**Complexity:** L  
**Migration:** no (FE only; optional `Org.PublicThemeId` BE)  
**Dependencies:** `spec-branded-booking-site`, `spec-public-booking-readmodel`

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

- SSG export of static site
- WYSIWYG drag-drop builder (Should — live preview only)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
