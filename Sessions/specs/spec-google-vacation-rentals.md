# Spec — Google Vacation Rentals Integration (US-015)

## Overview

Widen commission-free direct distribution by publishing CasaZen properties to **Google
Vacation Rentals (GVR)**, with booking links pointing back to the operator's direct
booking surface (`spec-branded-booking-site` / `spec-direct-checkout`). GVR becomes a
**zero-commission demand surface**: Google sends the traveller to the operator's direct
checkout, so no OTA commission is paid (F3/F6).

The integration produces a GVR-compliant **property + pricing + availability feed** built
strictly from the public read-model (`spec-public-booking-readmodel`, no PII), and honors
Google's **price-accuracy** policy (the price shown in GVR must match the price the
traveller is charged at checkout).

Reference: **US-015** (Phase 3 — Distribution + Marketplace; draft-v3 §B Phase 3 + §C row `spec-google-vacation-rentals`)
Stage of entry: **Stage 01 Planning** (epic-level macro-spec; splits into issues at Stage 02)

---

## User Story

As an **operator**, I want my properties discoverable and bookable through Google Vacation
Rentals — with the booking link going to my own direct checkout — so that I capture Google
demand without paying OTA commission, while CasaZen keeps my GVR feed accurate and policy-compliant.

---

## Acceptance Criteria

### Backend

- **AC1**: `GET /api/distribution/gvr/feed/{orgId}` produces a GVR-compliant feed
  (property listings + nightly pricing + availability) built **only** from the public
  read-model; the payload contains no `OwnerId`, guest PII, or internal identifiers
  (reuses `spec-public-booking-readmodel` minimization — regression-asserted).

- **AC2**: Feed entries include a per-property **deep link** to the direct booking surface
  (`spec-direct-checkout`) carrying date/occupancy params in the format
  `?checkin=YYYY-MM-DD&checkout=YYYY-MM-DD&guests=N`, matching the itinerary-aware URL
  contract defined in `spec-branded-booking-site` AC15 — so the traveller lands on the
  exact quoted stay with dates and guest count pre-populated.

- **AC3**: **Price-accuracy guarantee** — the price emitted in the feed for a given
  date range equals the price returned by the direct-checkout quote for the same range
  (computed from the same pricing source). A diagnostic endpoint
  `GET /api/distribution/gvr/price-accuracy/{propertyId}` reports any drift > tolerance.

- **AC4**: New tenant-scoped entity `GvrListingConfig` (carrying `OrgId` per RF1) holds
  per-property enable/disable, connection status, last-sync timestamp, and feed health.

- **AC5**: `GvrFeedSyncJob` (Hangfire) regenerates/refreshes the feed and availability on a
  schedule and on relevant booking/price changes; registered in `Program.cs`
  `ConfigureRecurringJobs`.

- **AC6**: Only properties with a **valid CIN** and a published direct booking site are
  eligible for the feed; ineligible properties are excluded with a reason surfaced to the FE.

- **AC7 (Regression)**: A serialization test asserts the feed body never contains
  `OwnerId`/`apiKey`/guest fields (case-insensitive), mirroring the read-model contract.

- **AC8 (JSON-LD schema compliance)**: Each property entry in the GVR feed and the
  corresponding public property page (`spec-branded-booking-site` AC12) must emit a
  `VacationRental` JSON-LD block that includes:
  - `containsPlace` → `{ "@type": "Accommodation", "name": ..., "amenityFeature": [...] }`
    nesting — required by Google's VacationRental schema; omitting it prevents the rich
    result from being shown.
  - `geo` → `GeoCoordinates` with `latitude` and `longitude` — required for Maps
    integration and GVR eligibility; properties missing coordinates are excluded from the
    feed with reason `"coordinate_mancanti"` surfaced in the FE (same pattern as AC6).
  - `identifier` → CIN code (where available, as `PropertyValue`).
  A serialization test asserts both `containsPlace` and `geo` are present on every
  feed-eligible property entry.

- **AC9 (Price-accuracy alert)**: When `GvrFeedSyncJob` detects price drift > tolerance
  on a property (AC3), it:
  1. Disables the GVR listing for that property (sets `GvrListingConfig.IsEnabled = false`)
     to avoid shipping a non-compliant price to Google.
  2. Creates an in-app notification (or email, per `spec-onboarding-plg` notification
     channel) to the operator with property name, affected date range, feed price vs.
     checkout price, and a direct link to the distribution settings page to re-enable
     after fixing.
  The existing `GET /api/distribution/gvr/price-accuracy/{propertyId}` diagnostic
  endpoint is retained for manual inspection.

### Frontend

- **AC10**: Distribution settings page (`/distribution/gvr`) lists eligible properties with a
  per-property enable/disable toggle, connection status, last-sync time, and the feed URL.

- **AC11**: Per-property GVR panel shows eligibility (CIN valid + site published +
  **coordinates present**), and a **price-accuracy diagnostic** indicator
  (OK / drift detected) reading AC3/AC9.

- **AC12**: Ineligible properties show an actionable reason — e.g. `"CIN mancante"`,
  `"sito diretto non pubblicato"`, `"coordinate geografiche mancanti"` — rather than a
  silent exclusion, with a direct link to the relevant settings screen to resolve each
  reason.

- **AC13**: All `/distribution/*` routes wrapped in `<ProtectedRoute>`; operator-scoped.

- **AC14 (Price-accuracy notification)**: When the operator receives a price-drift
  notification (AC9), clicking through lands on the per-property GVR panel with the
  drift details expanded and a one-click "Re-enable after fix" CTA (only enabled once
  drift is resolved, i.e. AC3 returns OK).

---

## Technical Notes

### Backend — Files to create/modify

| File | Action |
|---|---|
| `Casazen.Web/Controllers/DistributionController.cs` | Create (new module) — GVR feed + price-accuracy endpoints |
| `Casazen.Core/Services/IGvrFeedService.cs` + `Casazen.Infrastructure/Services/GvrFeedService.cs` | Create (new module) — build feed from public read-model; price-accuracy check; `containsPlace` + `geo` assertion (AC8) |
| `Casazen.Core/Entities/GvrListingConfig.cs` | Create (new module) — per-property config; `OrgId` FK; add `DisabledReason` field for drift/eligibility (AC9) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — add `GvrListingConfig` DbSet + `OrgId` filter |
| `Casazen.Infrastructure/Migrations/` | Create — migration `AddGvrListingConfig` (rebase on `AppDbContextModelSnapshot.cs`) |
| `Casazen.Web/BackgroundJobs/GvrFeedSyncJob.cs` | Create (new module) — feed/availability refresh + drift detection + auto-disable + operator notification (AC9) |
| `Casazen.Web/Program.cs` | Modify — register `GvrFeedSyncJob` in `ConfigureRecurringJobs` |
| Public read-model (`spec-public-booking-readmodel`) | Reuse — pricing/availability/CIN/coordinates as feed source (no PII) |
| Direct-checkout quote (`spec-direct-checkout`) | Reuse — single source of truth for price-accuracy + deep link |
| `Casazen.Core/Validation/CinCodeAttribute.cs` | Reuse — CIN-valid eligibility gate |

### Frontend — Files to create/modify

| File | Action |
|---|---|
| `src/features/distribution/gvr-settings-page.tsx` | Create (new module) — listing + toggles |
| `src/features/distribution/components/gvr-property-row.tsx` | Create (new module) — status + last sync + eligibility reasons (AC12) |
| `src/features/distribution/components/gvr-price-accuracy-badge.tsx` | Create (new module) — AC3/AC9 diagnostic + re-enable CTA (AC14) |
| `src/features/distribution/components/gvr-eligibility-notice.tsx` | Create (new module) — actionable reasons with deep links to fix screens (AC12) |
| `src/api/distribution.api.ts` | Create (new module) — feed/config/diagnostic calls |
| `src/queries/use-distribution.ts` | Create (new module) — TanStack Query hooks |
| `src/types/distribution.types.ts` | Create (new module) — DTOs |
| `src/routes/index.tsx` | Modify — add `/distribution/*` under `<ProtectedRoute>` |

---

## Compliance

- **GVR price-accuracy policy**: the feed price MUST match the direct-checkout charge for the
  same stay; AC3 + auto-disable (AC9) enforce this. Persistent drift disables the listing
  rather than shipping a non-compliant price.
- **Google VacationRental schema**: `containsPlace` and `geo` are required for rich results
  and Maps eligibility; AC8 enforces their presence and excludes non-compliant properties
  from the feed.
- **Data-feed compliance / GDPR data minimization**: the feed is built from the public
  read-model only — no `OwnerId`, no guest PII, no internal keys (AC1/AC7 regression).
- **CIN (D.L. 145/2023)**: only CIN-valid properties are eligible for distribution; CIN is
  displayed where the surface requires it.
- **No OTA commission**: GVR routes the traveller to the operator's own direct checkout —
  this is a distribution surface, not an OTA channel; no booking commission is introduced.

---

## Dependencies

- **Requires**: `spec-direct-checkout` (booking deep link + price source-of-truth for
  price-accuracy); `spec-public-booking-readmodel` (PII-safe feed source).
- **Requires**: `spec-branded-booking-site` AC15 (itinerary-aware URL contract for deep links)
  and AC12 (JSON-LD with `containsPlace` + `geo` on property pages).
- **Blocks**: the Phase 3 exit criterion "a property is discoverable + bookable via GVR".
- **Related**: `spec-branded-booking-site` (the surface GVR links into); `spec-tenant-boundary`
  (`OrgId` scoping, RF1); `spec-supplier-marketplace` (sibling Phase 3 spec).
