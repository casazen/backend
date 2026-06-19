---
id: US-026
slug: seo-funnel
title: SEO comune pages → signup/booking CTA funnel
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [public-site-design-system]
blocks: []
exit_contributes_to: Organic traffic → host acquisition; aligned public visual language
last_reviewed: 2026-06-19
---

# Spec — SEO Funnel (US-026)

## Overview

Aligns **SEO comune landing pages** (backend #258) with the public design system and adds measurable CTAs: host signup, featured properties, direct booking links. Traffic attribution for growth experiments.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a traveler searching "affitto vacanze {comune}", I want a useful local guide that leads me to bookable properties so that I find a stay without OTAs only.

As CasaZen growth, we want to measure which comuni convert to host signups.

---

## Acceptance Criteria

### Backend

- **AC1**: Reuse `SeoContentPage` entity and public read APIs from #258.

- **AC2**: `GET /api/public/seo/{comuneSlug}` returns `{ title, content, meta, featuredProperties[], cta }` where `featuredProperties` are Active compliance properties in comune.

- **AC3**: `POST /api/public/seo/events` (`[AllowAnonymous]`) logs `{ event: cta_click|signup_start, comuneSlug, utm?, referrer? }` — no PII.

### Frontend

- **AC4**: Route `/destinazioni/{comune}` (or existing path) uses `PublicSiteShell` + shared tokens from `spec-public-site-design-system`.

- **AC5**: Primary CTA: "Pubblica la tua casa" → `/signup?comune={slug}`; secondary: property cards → host public site booking.

- **AC6**: Tourist tax info section when `TouristTaxRate` exists for comune; warning + link when missing (compliance wizard alignment).

- **AC7**: Mobile-first layout; same LCP targets as public site AC4.

### Analytics

- **AC8**: CTA clicks fire AC3 + optional Plausible/GA event (env-configured).

- **AC9**: Admin dashboard widget: top comuni by CTA clicks (last 30 days) — minimal read API.

### Content

- **AC10**: Pilot: at least **3 comuni** with full content + featured properties for demo.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Web/Controllers/PublicSeoController.cs` | Modify — featured properties + events |
| `frontend/src/features/seo/ComuneLandingPage.tsx` | Rewrite — design system |
| `frontend/src/features/seo/useSeoEvent.ts` | Create |

**Complexity:** M  
**Migration:** optional — `SeoEvent` table  
**Dependencies:** `spec-public-site-design-system`, SEO backend #258

---

## Out of Scope

- 1.409 comuni populated
- AI-generated content
- Paid ads integration
