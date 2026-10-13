---
id: US-026
slug: seo-funnel
title: SEO comune pages → signup/booking CTA funnel
phase: 1
type: feature
priority: P0
status: partial
issue: 300
depends_on: [public-site-design-system]
blocks: []
exit_contributes_to: Organic traffic → host acquisition; aligned public visual language
last_reviewed: 2026-10-02
---

# Spec — SEO Funnel (US-026)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Aligns **SEO comune landing pages** (backend #258) with the public design system and adds measurable CTAs: host signup, featured properties, direct booking links. Traffic attribution for growth experiments.

**Phase:** 1 — MVP · **Type:** feature · **Status:** partial (2026-10-02, FN-05: code and L1/L2 tests exist for 8 of 10 ACs, AC7 and AC10 not verified, no L3 run; see "AC Test Map (real state)")

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a traveler searching "affitto vacanze {comune}", I want a useful local guide that leads me to bookable properties so that I find a stay without OTAs only.

As CasaZen growth, we want to measure which comuni convert to host signups.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Reuse `SeoContentPage` entity and public read APIs from #258.

- **AC2**: `GET /api/public/seo/{comuneSlug}` returns `{ title, content, meta, featuredProperties[], cta }` where `featuredProperties` are Active compliance properties in comune.

- **AC3**: `POST /api/public/seo/events` (`[AllowAnonymous]`) logs `{ event: cta_click|signup_start, comuneSlug, utm?, referrer? }` — no PII.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC4**: Route `/destinazioni/{comune}` (or existing path) uses `PublicSiteShell` + shared tokens from `spec-public-site-design-system`.

- **AC5**: Primary CTA: "Pubblica la tua casa" → `/signup?comune={slug}`; secondary: property cards → host public site booking.

- **AC6**: Tourist tax info section when `TouristTaxRate` exists for comune; warning + link when missing (compliance wizard alignment).

- **AC7**: Mobile-first layout; same LCP targets as public site AC4.

### Analytics

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: CTA clicks fire AC3 + optional Plausible/GA event (env-configured).

- **AC9**: Admin dashboard widget: top comuni by CTA clicks (last 30 days) — minimal read API.

### Content

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Pilot: at least **3 comuni** with full content + featured properties for demo.

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



1. Enter the primary route for `seo-funnel`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + L2 + L3 | Reuse `SeoContentPage` entity and public read APIs from #258. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC2 | L1 + L2 + L3 | `GET /api/public/seo/{comuneSlug}` returns `{ title, content, meta, featuredProperties[], cta }` where `featuredProperties` are Active co... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC3 | L1 + L2 + L3 | `POST /api/public/seo/events` (`[AllowAnonymous]`) logs `{ event: cta_click/signup_start, comuneSlug, utm?, referrer? }` — no PII. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC4 | L2 + L3 | Route `/destinazioni/{comune}` (or existing path) uses `PublicSiteShell` + shared tokens from `spec-public-site-design-system`. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC5 | L2 + L3 | Primary CTA: "Pubblica la tua casa" → `/signup?comune={slug}`; secondary: property cards → host public site booking. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L2 + L3 | Tourist tax info section when `TouristTaxRate` exists for comune; warning + link when missing (compliance wizard alignment). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L2 + L3 | Mobile-first layout; same LCP targets as public site AC4. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L2 + L3 | CTA clicks fire AC3 + optional Plausible/GA event (env-configured). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | Admin dashboard widget: top comuni by CTA clicks (last 30 days) — minimal read API. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 | Pilot: at least **3 comuni** with full content + featured properties for demo. | Outcome not met; wrong status; silent no-op |

### AC Test Map (real state, FN-05 2026-10-02)

One row per AC with the tests that exist today. "L3" (real API on staging, titled test per UI AC) has **not** been run for any AC of this spec: the Golden Journey of FN-03 does not cover the SEO funnel.

| AC | State | Tests (backend `Casazen.Tests`, frontend `src`) |
|---|---|---|
| AC1 | done | Existing entity and read APIs; `Integration/PublicSeoIntegrationTests.cs`, `Integration/ComplianceSeoIntegrationTests.cs` |
| AC2 | done (shape differs: separate endpoint `/api/public/seo/{comune}/featured-properties`) | `Integration/SeoFunnelIntegrationTests.cs` (`FeaturedProperties_*`: published only, org disabled, empty list, 404, ISTAT code as key) |
| AC3 | done | `Integration/SeoFunnelIntegrationTests.cs` (`RecordEvent_*`: 204, only non-personal fields stored, 422/400 on bad input), `Unit/Services/SeoEventServiceTests.cs` |
| AC4 | done (different route) | `features/public-seo/public-seo-pages.test.tsx`, `seo-hub-page.test.tsx` |
| AC5 | done | `features/public-seo/components/seo-cta-block.test.tsx`, `lib/__tests__/signup-attribution.test.ts` |
| AC6 | done | `features/public-seo/components/tourist-tax-calculator-widget.test.tsx`; tax rates are not verified at source (RS-7 partial, see `PIANO-RISANAMENTO-2026-09.md`) |
| AC7 | not verified | No Lighthouse or LCP check exists |
| AC8 | done | `lib/__tests__/seo-events.test.ts` (CTA event); the optional Plausible event is configured in `config/analytics.config.ts` (no GA) |
| AC9 | done | `Integration/SeoFunnelIntegrationTests.cs` (`TopComuni_*`), `features/admin/components/seo-top-comuni-widget.test.tsx` |
| AC10 | not verified | `SeoBootstrapHostedService` seeds content; nobody has checked three comuni with content and featured properties on a real environment |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File (real, verified 2026-10-02) | Role |
|---|---|
| `Casazen.Web/Controllers/PublicContentController.cs` | Public read APIs of the SEO pages (`/api/public/content/affitti-brevi/{region}/{comune}`, `/content/tassa-soggiorno/{comune}`) and the public tourist-tax calculator |
| `Casazen.Web/Controllers/PublicSeoController.cs` | Public SEO HTML/JSON (`/api/public/seo/guides/...`, `tourist-tax/...`, `hub`, org pages) |
| `Casazen.Web/Controllers/PublicSeoFunnelController.cs` | AC2/AC3: `GET /api/public/seo/{comune}/featured-properties`, `POST /api/public/seo/events` (SE-04) |
| `Casazen.Web/Controllers/AdminSeoAnalyticsController.cs`, `AdminSeoController.cs` | AC9 (`GET /api/admin/seo/top-comuni`) and the admin SEO content review |
| `frontend/src/features/public-seo/*` (`compliance-guide-page.tsx`, `tourist-tax-calculator-page.tsx`, `seo-hub-page.tsx`, `components/seo-cta-block.tsx`, `components/featured-properties.tsx`) | AC4, AC5, AC6 |
| `frontend/src/lib/seo-events.ts`, `frontend/src/lib/signup-attribution.ts`, `frontend/src/pages/signup-page.tsx` | AC8 and the signup attribution (SE-03) |
| `frontend/src/features/admin/components/seo-top-comuni-widget.tsx` | AC9 widget |

The files named in the first version of this spec (`PublicSeoController.cs` for featured properties and events, `features/seo/ComuneLandingPage.tsx`, `features/seo/useSeoEvent.ts`) did not exist: they were never created. Routes are `/p/affitti-brevi/:region/:comune`, `/p/tassa-soggiorno/:comune` and `/signup?comune=...` (not `/destinazioni/{comune}`). Runbooks: `docs/runbooks/seo-funnel.md` (SE-04), `docs/runbooks/seo-domain.md` (canonical domain, sitemap, robots; SE-02).

**Complexity:** M  
**Migration:** optional — `SeoEvent` table  
**Dependencies:** `spec-public-site-design-system`, SEO backend #258

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

- 1.409 comuni populated
- AI-generated content
- Paid ads integration

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
