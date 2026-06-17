# Spec — Branded Booking Site (Public, Per-Org) (US-003)

## Overview

CasaZen's current React 19 SPA is an **authenticated owner console** — every route except `/login`
and `/search` is wrapped in `<ProtectedRoute>` (Auth0), and the app shell (`AppShell`, sidebar,
`AppLayerProvider`/`LayerSwitcher`) is operator tooling. There is **no public, guest-facing booking
website**.

This spec adds a **new public surface**: a branded, per-`Org` direct-booking site living under
unauthenticated routes (e.g. `/book/:orgSlug`), composed from the public read-model
(`spec-public-booking-readmodel`) and the public checkout (`spec-direct-checkout`). Branding (name,
logo, theme color) is resolved per `Org` (`spec-tenant-boundary`). This is the commission-free
storefront that makes CasaZen *sellable*.

Phase: **1 (MVP Sellable — direct booking)** · User story: **US-003**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

As a prospective guest, I want to visit an operator's branded CasaZen booking site, browse their
listings, open a property, pick dates, and pay — all without logging in — so that I can book directly
with that operator.

As an operator, I want my public booking site to carry **my brand** (name, logo, colors) rather than
generic CasaZen chrome, so that direct booking feels like my own website.

---

## Acceptance Criteria

### Backend

- **AC1**: `GET /api/public/orgs/{slug}` (`[AllowAnonymous]`) returns `PublicOrgDto { slug, displayName, logoUrl, themeColor, contactEmail }` for an active `Org` (branding read-model from `spec-tenant-boundary`); `404` for unknown/inactive slug. **No** internal Org fields (no Stripe ids, no plan tier, no owner data).

- **AC2**: `GET /api/public/orgs/{slug}/properties` (`[AllowAnonymous]`) returns the org's published listings as `IEnumerable<PublicPropertyDto>` (reusing the `spec-public-booking-readmodel` whitelist), filtered to that `Org` and `IsActive == true`, with the same 50-item cap and ordering.

- **AC3**: Property and checkout reads on the branded site reuse the existing public endpoints (`GET /api/properties/{id}/public`, `POST /api/public/bookings`) scoped by `orgId`; the controller validates the property belongs to `{slug}`'s `Org` (`404` on mismatch — no cross-org browsing leakage).

### Frontend

- **AC4**: New **public** route tree mounted **outside** `<ProtectedRoute>` in `src/routes/index.tsx`:
  - `/book/:orgSlug` → branded org landing + listing grid
  - `/book/:orgSlug/property/:id` → public property detail
  - `/book/:orgSlug/property/:id/checkout` → the `spec-direct-checkout` flow
  - Unknown org slug → branded 404; these routes never redirect to `/login`.

- **AC5**: A dedicated **public layout** (`PublicBookingShell`) — **not** `AppShell` — with **no** sidebar, no `LayerSwitcher`, no Auth0 user menu. It applies per-`Org` branding (logo, theme color via CSS variables) fetched from `GET /api/public/orgs/{slug}`.

- **AC6**: The listing grid renders `PublicPropertyDto` cards (photo, name, city, nightly rate, capacity, CIN badge); clicking a card routes to the public property detail. No operator identity shown.

- **AC7**: The public property detail page shows photos, description, amenities, house rules, CIN, a date picker with availability, and a **full price breakdown preview** (nightly rate × nights + cleaning fee + tourist tax + any extra fees = total) before routing into checkout. The breakdown must match the charge at checkout (same source-of-truth as `spec-direct-checkout` quote endpoint) — no price surprise at payment step.

- **AC8**: A **GDPR cookie/consent banner** appears on first visit to any `/book/*` route (accept/reject non-essential cookies; choice persisted); a footer links to **Privacy Policy** and **Terms of Service (ToS)**. End-user strings in Italian.

- **AC9**: If any property content is **AI-generated** (e.g. AI-written descriptions), the page shows an **EU AI Act transparency note** (e.g. "Descrizione generata con AI") near that content.

- **AC10**: The branded site has no dependency on Auth0 being configured; with `VITE_DEMO_MODE=true` it renders against a demo org slug for screenshots/CI without authentication.

- **AC11 (Regression)**: Mounting the public route tree does **not** weaken protection of the authenticated console — existing owner routes remain wrapped in `<ProtectedRoute>`; `/book/*` and `/search` are the only public route trees.

- **AC12 (SEO — JSON-LD)**: Every public property detail page (`/book/:orgSlug/property/:id`) renders a `<script type="application/ld+json">` block with `@type: VacationRental` structured data containing:
  - `name`, `description`, `image` (array, first photo as primary)
  - `address` → `PostalAddress` (street, city, postal code, country)
  - `geo` → `GeoCoordinates` (`latitude`, `longitude`) — required for Google Maps integration and GVR rich results
  - `containsPlace` → `{ "@type": "Accommodation", "name": ..., "amenityFeature": [...] }` nesting per Google VacationRental schema requirements
  - `numberOfRooms`, `occupancy` → `QuantitativeValue`
  - `petsAllowed`, `checkinTime`, `checkoutTime`
  - `url` → canonical absolute URL of the property page
  - `offers` → `Offer` with `price` (nightly rate), `priceCurrency`, `availability`
  - `identifier` → CIN code (where available)
  - `aggregateRating` → `AggregateRating` with `ratingValue`, `reviewCount` (populated once review system is available; omitted if `reviewCount == 0` to avoid invalid markup)
  The JSON-LD is server-rendered or injected at SSR/prerender time so crawlers see it without JS execution.

- **AC13 (SEO — Meta tags)**: Every public property page includes:
  - `<title>` → `{propertyName} · {cityName} — {orgDisplayName}`
  - `<meta name="description">` → first 155 chars of property description
  - **Open Graph**: `og:title`, `og:description`, `og:image` (first property photo, ≥1200×630px), `og:url` (canonical), `og:type: website`
  - **Twitter Card**: `twitter:card: summary_large_image`, `twitter:title`, `twitter:description`, `twitter:image`
  - `<link rel="canonical">` → absolute URL without query params (checkin/checkout/guests params must **not** appear in the canonical — use `<link rel="canonical">` to prevent duplicate indexing of date-parameterised URLs)
  The org landing page (`/book/:orgSlug`) also gets `og:*` tags using the org logo as `og:image`.

- **AC14 (SEO — Sitemap)**: `GET /api/public/orgs/{slug}/sitemap.xml` (`[AllowAnonymous]`) returns a standard XML sitemap listing the org landing page + all published property pages for that org, with `<lastmod>` from the property's `UpdatedAt`. Registered in `robots.txt` served at `/book/:orgSlug/robots.txt`.

- **AC15 (Itinerary-aware deep link)**: The public property detail page reads optional query params `?checkin=YYYY-MM-DD&checkout=YYYY-MM-DD&guests=N` on mount and pre-populates the date picker and guest count accordingly. This is the contract used by GVR deep links (`spec-google-vacation-rentals` AC2) and any external marketing links.

---

## Technical Notes

### Backend

| File | Action |
|---|---|
| `Casazen.Web/Controllers/PublicOrgController.cs` | Create — `[AllowAnonymous]` `GET /api/public/orgs/{slug}` + `/properties` + `/sitemap.xml` (AC1–AC3, AC14) |
| `Casazen.Core/DTOs/PublicOrgDto.cs` | Create — branding read-model (no internal Org fields) |
| `Casazen.Core/DTOs/PublicPropertyDto.cs` | Modify — add `Latitude`, `Longitude`, `CheckinTime`, `CheckoutTime`, `PetsAllowed`, `AverageRating`, `ReviewCount` for JSON-LD (AC12) |
| `Casazen.Core/Services/IOrgService.cs` | Modify — add `GetPublicBySlugAsync(slug)` (from `spec-tenant-boundary`) |
| `Casazen.Infrastructure/Services/PropertyService.cs` | Modify — `SearchAsync`/public list overload accepts `orgId` filter |
| `Casazen.Web/Controllers/PublicBookingsController.cs` | Modify — validate property↔org on the branded path (AC3) |

### Frontend

| File | Action |
|---|---|
| `src/routes/index.tsx` | Modify — add public `/book/:orgSlug/*` tree outside `<ProtectedRoute>` (AC4, AC11) |
| `src/features/public-booking/org-landing-page.tsx` | Create — branded landing + listing grid + OG meta (AC13) |
| `src/features/public-booking/public-property-page.tsx` | Create — public property detail + date picker + price breakdown (AC7) + JSON-LD (AC12) + meta tags (AC13) + deep link params (AC15) |
| `src/features/public-booking/components/price-breakdown.tsx` | Create — full fee breakdown component (nightly × nights + cleaning + tourist tax + extras = total) (AC7) |
| `src/features/public-booking/components/property-json-ld.tsx` | Create — renders `<script type="application/ld+json">` from `PublicPropertyDto`; omits `aggregateRating` if `reviewCount == 0` (AC12) |
| `src/features/public-booking/components/property-meta-tags.tsx` | Create — `<title>`, `<meta description>`, OG + Twitter Card tags via `react-helmet-async` or equivalent (AC13) |
| `src/components/layout/public-booking-shell.tsx` | Create — public layout (no sidebar/auth chrome), applies Org branding |
| `src/components/shared/cookie-consent-banner.tsx` | Create — GDPR cookie/consent banner (AC8) |
| `src/components/shared/ai-content-notice.tsx` | Create — EU AI Act transparency note (AC9) |
| `src/api/public-org.api.ts` | Create — `getPublicOrg(slug)`, `getOrgProperties(slug)` |
| `src/queries/use-public-org.ts` | Create — `usePublicOrg(slug)`, `useOrgProperties(slug)` |
| `src/types/org.types.ts` | Create — `PublicOrgDto` |
| `src/lib/axios.ts` | Modify — skip auth for `/public/orgs/*` |

---

## Compliance

- **GDPR cookie/consent + ToS**: consent banner gates non-essential cookies on the public surface; Privacy Policy + ToS linked in the footer (AC8). No tracking before consent.
- **GDPR data minimization**: branding and listing reads reuse the public whitelists (`PublicOrgDto`, `PublicPropertyDto`) — no operator PII, no Stripe ids, no plan data exposed (AC1–AC2).
- **EU AI Act (transparency, limited-risk)**: any AI-generated guest-facing content carries a visible disclosure (AC9). If no AI content ships in this surface, the component exists but is inert.
- **Tenant isolation**: cross-org browsing is blocked server-side (property↔org validation, AC3).
- **Price consistency**: the breakdown shown pre-checkout (AC7) uses the same pricing source as `spec-direct-checkout`; price surprise at payment step is a compliance and trust failure.
- **SEO canonical**: query params (`checkin`, `checkout`, `guests`) must never appear in the canonical URL to prevent duplicate content indexing (AC13).

---

## Dependencies

- **Requires**: `spec-public-booking-readmodel` (property read-models), `spec-tenant-boundary` (`Org` + slug + branding fields + `orgId` scoping).
- **Requires (for checkout)**: `spec-direct-checkout` (the payment flow embedded at `/book/:orgSlug/property/:id/checkout`).
- **Blocks**: `spec-onboarding-plg` activation ("publish branded site" is an activation milestone).
- **Blocks**: `spec-google-vacation-rentals` (GVR deep link contract requires AC15; GVR eligibility requires the site to be published).
- **Related**: existing public `/search` `SearchPage` (separate generic surface; not replaced).
- **Does not touch**: authenticated owner console routes, `AppShell`, `AppLayerProvider`, `LayerSwitcher`.
