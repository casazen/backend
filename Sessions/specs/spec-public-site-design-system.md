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
last_reviewed: 2026-06-19
---

# Spec — Public Site Design System (US-023)

## Overview

Replace console-looking `/book/{slug}` with a **marketing-grade** `PublicSiteShell`: hero, gallery, editorial typography, mobile-first guest UX. Shared by host booking sites and SEO comune pages. Evolves `spec-branded-booking-site` APIs — UI rewrite only on public routes.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a guest, I want a beautiful property site that feels like the host's brand so that I trust booking directly.

As a host, I want to choose a visual template and see my photos prominently without looking like an admin tool.

---

## Acceptance Criteria

### Design system

- **AC1**: `PublicSiteShell` layout separate from `AppShell` — no host nav, no admin chrome.

- **AC2**: At least **1 premium template** (MVP); architecture supports 3 themes (Mare/Montagna/Urban) — config `Org.PublicThemeId`.

- **AC3**: Components: `Hero`, `PropertyGallery`, `AmenityGrid`, `BookingWidget` (sticky on mobile), `Footer` (Powered by on Starter).

- **AC4**: LCP target < 2.5s on 4G — optimized images (`next/image` or Vite equivalent), lazy below fold.

- **AC5**: WCAG 2.1 AA contrast on primary CTA; focus states on booking widget.

### Host booking site

- **AC6**: `/book/{slug}` and property detail use new shell; existing public API unchanged (`spec-public-booking-readmodel`).

- **AC7**: Booking widget calls direct checkout flow (US-002); price breakdown + GDPR consent in Italian.

- **AC8**: Mobile: full-width gallery swipe; booking CTA fixed bottom bar.

### SEO comune pages

- **AC9**: `SeoContentPage` FE routes use same typography, colors, and CTA button styles as host sites.

- **AC10**: CTA links to host signup or featured properties — funnel tracked (see `spec-seo-funnel`).

### Branding API

- **AC11**: Consume `GET /api/public/orgs/{slug}` branding: `logoUrl`, `primaryColor`, `heroImageUrl`, `publicThemeId`, `tagline`.

- **AC12**: Host console preview (Should): link "Anteprima sito" opens public URL in new tab.

### Regression

- **AC13**: Anonymous checkout E2E (golden journey step 4) passes on redesigned UI.

---

## Technical Notes

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

## Out of Scope

- SSG export of static site
- WYSIWYG drag-drop builder (Should — live preview only)
