---
id: US-029
slug: public-marketing-site
title: CasaZen public marketing site (static vetrina on casazen.space)
phase: 1
type: feature
priority: P0
status: partial
issue:
depends_on: [public-site-design-system, seo-funnel, custom-domain-booking]
blocks: []
exit_contributes_to: Organic discovery of CasaZen on the canonical public domain; host/supplier acquisition surface
last_reviewed: 2026-10-13
---

# Spec — Public Marketing Site (US-029 / SEO-VETRINA)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

CasaZen needs a **crawlable marketing site** on the product's own public host (`VITE_PUBLIC_SITE_URL`, today `casazen.space` / `www.casazen.space`) so a search for "CasaZen" finds what the product is. Before SEO-VETRINA, `/` on that host was the console entry (redirect toward login).

The site is **four static HTML pages** rendered at frontend build time and served only on the web app's own host. Org booking hosts (subdomains and custom domains, BK-16) keep their own `/` booking surface. Product owner decision of **2026-10-13 (private invitation)**: no signup CTA, no prices/plans page; CTA is "Richiedi un invito" via `mailto:` to `VITE_SUPPORT_EMAIL`.

**Phase:** 1 — MVP · **Type:** feature · **Status:** partial (frontend code and L2 tests merged as fe PR #238; production gate / Search Console / L3 deploy checks not verified here) · **Issue:** none yet

Runbook (operational truth): `docs/runbooks/seo-domain.md` §11.

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host or supplier discovering CasaZen from search, I want a clear Italian (and English) marketing site that explains the product and how to request access, so that I understand the offer before I log in.

As CasaZen growth, we want Google to index our own pages (not only SEO guides) with correct canonical, Organization JSON-LD and legal identification.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Pages and content

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Exactly these public paths exist on the web app's own host: `/` (home), `/funzionalita`, `/per-i-fornitori`, `/chi-siamo`. There is **no** `/prezzi` (or any plans/pricing page) while private invitation is in force.

- **AC2**: Every claim on those pages matches a feature that exists in the product today (console, compliance, booking, supplier flows). No unverifiable marketing numbers, rankings or promises.

- **AC3**: Primary CTA is **"Richiedi un invito"** → `mailto:{VITE_SUPPORT_EMAIL}` with a fixed Italian subject. When the e-mail is unset, the site states that access is by invitation and shows **no** invented address and **no** `/signup` link. "Accedi" links to `/login` only.

- **AC4**: Home links into the SEO content surface (hub and/or direct guide URLs for at least Como, Milano, Varese and Monza under the existing `/p/affitti-brevi/...` routes of `spec-seo-funnel`).

### Rendering and hosting

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC5**: Pages are **static HTML at build time** inside `casazen/frontend` (`site.html` + `vite build --ssr` + `scripts/prerender-site.mjs` → `dist/_site/*.html`). The browser hydrates the same React tree (i18n IT/EN).

- **AC6**: Vercel Routing Middleware serves `dist/_site/{page}.html` at the public path **only** on the web app's own host (host of `VITE_PUBLIC_SITE_URL`, `*.vercel.app`, `localhost`). On a verified org host (custom domain / org subdomain), the same paths stay the org booking site (`spec-custom-domain-booking`).

- **AC7**: `/` with Auth0 login callback query (`code` or `error` + `state`) is **not** replaced by the marketing HTML: the SPA completes login there. Signed-in users who open `/` still see the public home; the console stays under `/app`.

### SEO and legal

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: Each prerendered page has Italian default `<title>`, meta description, canonical URL on the public origin, `hreflang` and Open Graph tags. Home includes JSON-LD `@type=Organization` when a legal entity (name + partita IVA) is configured.

- **AC9**: Production gate (art. 35 DPR 633/1972): production serves the site only when (1) `VITE_LEGAL_NAME` + valid 11-digit `VITE_LEGAL_VAT_ID`, or (2) explicit `VITE_PUBLIC_SITE_WITHOUT_VAT_ID=true` with name + `VITE_SUPPORT_EMAIL` ([COUNSEL_REQUIRED] before enabling (2)). Otherwise production keeps `/` as the console SPA and does not emit site sitemap/robots lines. Preview/test/local always build the site for review.

- **AC10**: `robots.txt` declares `Sitemap: {public origin}/sitemap-site.xml` when the site is served, and disallows `/_site/`. `sitemap-site.xml` lists exactly the four pages.

### Private invitation alignment

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: While `Features__SeoSignupCta` is off, SEO guides expose no "Pubblica la tua casa" / signup CTA (API `cta: null` and crawler HTML without `/signup`). Closing registration itself remains Auth0 "Disable Sign Ups" + supplier pilot gates (runbook), not this spec.

---

## Verifiable Outcomes

**Required.** One row per AC.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + L2 | `SITE_PAGES` paths are exactly `/`, `/funzionalita`, `/per-i-fornitori`, `/chi-siamo`; `/prezzi` is not a site page | Extra pricing route; missing path |
| AC2 | L2 | Copy review: no claim without a shipped feature behind it | Invented metrics; fake integrations |
| AC3 | L1 + L2 | CTA is mailto invite or invitation notice; zero `href="/signup"` on site pages | Signup button; invented e-mail |
| AC4 | L2 + L3 | Home has a working link into the SEO hub or the four pilot guides | Dead link; English-only hub label |
| AC5 | L1 + L2 | Build writes `dist/_site/*.html` with title/canonical; hydration smoke | SPA-only shell with empty body for bots |
| AC6 | L1 + L2 | Middleware path list matches `SITE_PAGES`; org-host fixture still gets booking `/` | Marketing HTML on custom domain |
| AC7 | L2 + L3 | Login callback on `/` reaches Auth0 handler; Accedi → `/login` | Middleware swallows `code`/`state` |
| AC8 | L1 + L2 | Prerender head has title, canonical, hreflang, OG; Organization JSON-LD when VAT set | Missing canonical; JSON-LD without name |
| AC9 | L1 + L2 | Gate unit tests: valid VAT / without-VAT flag / fail closed | Production site without identification |
| AC10 | L1 + L2 | Sitemap locs = four pages; robots mentions sitemap-site and disallows `/_site/` | Sitemap lists `/signup` or `/prezzi` |
| AC11 | L1 + L2 | With flag off, public content API `cta` is null; guide HTML has no `/signup` | Signup CTA still rendered |

---

## UX / UI Quality

**Required** (Frontend ACs present).

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Visitor understands product + how to request invite | L3: home → features → invite mailto |
| Language | Italian default; English via app locale after hydrate | L2: Italian labels on primary CTA |
| Empty state | Missing support e-mail → invitation notice, not blank CTA | L2: legal.email null fixture |
| Error state | N/A for static pages (no API mutations) | — |
| Destructive / legal copy | P.IVA (or holder identity in without-VAT mode) visible in footer/home | L2/L3 assert documented phrases |

**Happy-path script:**

1. Open `https://{public origin}/` (no Auth0 query).
2. Read home; follow Funzionalità; open "Richiedi un invito" (mailto) or see invitation notice.
3. Follow Accedi → `/login` → land in console after auth.
4. Done when Verifiable Outcomes for AC1, AC3, AC7 hold.

---

## Technical Notes

| File | Action |
|---|---|
| `frontend/src/site/*` | Create/maintain — pages, gate, head, i18n, layout |
| `frontend/site.html`, `frontend/scripts/prerender-site.mjs` | Create/maintain — build-time prerender |
| `frontend/middleware.ts` | Modify — serve `_site` HTML only on own host |
| `frontend/src/config/robots-txt.ts`, `vercel-build-env.ts` | Modify — sitemap line + production env gate |
| `backend/docs/runbooks/seo-domain.md` §11 | Maintain — deploy checks and private-invitation rules |

**Architecture decision (resolved 2026-10):** static pages **inside** `casazen/frontend` with middleware host gating (recommended). Alternative deferred: separate marketing site with console on `app.casazen.space`.

**Complexity:** M  
**Migration:** no  
**Dependencies:** `spec-public-site-design-system`, `spec-seo-funnel`, `spec-custom-domain-booking`  
**Repos:** FE (primary); BE runbook / feature flag `SeoSignupCta` only

---

## Test expectations

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | Vitest on `site-pages`, `site-gate`, head/sitemap helpers; middleware path parity | "Build succeeds" |
| L2 | Component/render tests (`site-render`, `site-pages-content`); titled `test('ACn: …')` where UI | One smoke for all ACs |
| L3 | curl checks from runbook §11 on a real deploy; Auth0 login round-trip | Mocking middleware away |

### AC Test Map (real state, 2026-10-13)

| AC | State | Tests / evidence |
|---|---|---|
| AC1 | done (L1/L2) | `frontend/src/site/__tests__/site-pages.test.ts` (paths; `/prezzi` not a page) |
| AC2 | manual | Copy owned with product; re-check when features change |
| AC3 | done (L1/L2) | `site-pages.test.ts` (`inviteHref_*`, no signup paths); content uses `InviteCta` |
| AC4 | partial | Home links SEO hub (`SEO_HUB_PATH`); direct Como/Milano/Varese/Monza links not asserted yet |
| AC5 | done (code) | `prerender-site.mjs`, `site-render` / `site-head` tests; L3 deploy curl pending |
| AC6 | done (L1/L2) | `src/test/site-middleware.test.ts` (path list parity) |
| AC7 | partial | Runbook documents callback bypass; L3 login round-trip not recorded here |
| AC8 | done (L1/L2) | `site-head.test.ts` |
| AC9 | done (L1/L2) | `site-pages.test.ts` / gate tests (`isPublicSiteEnabled`, VAT normalize) |
| AC10 | done (L1/L2) | sitemap builder + robots tests |
| AC11 | done (BE/FE) | `Features__SeoSignupCta` off; seo-domain §11 curl examples |

Evidence: frontend PR [#238](https://github.com/casazen/frontend/pull/238); backend runbook commit `49826635` and private-invitation follow-ups on `develop`.

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] Enabling `VITE_PUBLIC_SITE_WITHOUT_VAT_ID` (publish without partita IVA): identification of the service provider and privacy notice — accountant/lawyer before production.
- Partita IVA on the home page when VAT-registered (art. 35 DPR 633/1972) — enforced by the production build gate.

---

## Out of Scope

- Indexing supplier vetrine (`/fornitori/{slug}`) — product + legal decision (consent in supplier Terms); stays `noindex` per `spec-supplier-public-site`.
- Provincial SEO hub and supplier CTAs inside compliance guides.
- `/prezzi` and public self-serve signup (blocked while private invitation holds).
- CasaZen logo / real favicon assets (called out in runbook as follow-up).
- Separate `app.` subdomain split (open alternative, not required for v1).

---

## Demo seed

N/A — no host, guest, supplier or staff entity is written. Pages are static HTML from the frontend build. The demo showcase ("Reimposta la vetrina") is unrelated; do not invent `DemoShowcaseCatalog` rows for this surface.

---

## Open Questions

| Question | Owner | Notes |
|---|---|---|
| When to re-introduce `/prezzi` with amounts equal to `Billing:Display:*:PriceMonthly` (29 / 79 / 199 €) and Stripe Price ids | PO | Blocked until public self-serve and live prices are decided |
| Direct home links to Como / Milano / Varese / Monza guides vs hub-only | Growth | AC4 partial until chosen |
| Search Console: submit `sitemap-site.xml` on the production property | Ops | After production gate env vars are set (A6 in SEO plan) |
