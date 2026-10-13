# Spec — Public Booking Read-Model (US-001)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

The anonymous property search is the **seed** of the direct-booking engine, but today it leaks
the raw persistence entity. `GET /api/properties/search` is `[AllowAnonymous]` and returns
`ActionResult<IEnumerable<Property>>` — the full `Property` entity including `OwnerId` (the
operator's Auth0 `sub`), internal timestamps, and the `IsActive` flag, with no field whitelist.

This spec introduces a **public read-model DTO** and hardens the anonymous endpoint so that the
public payload is data-minimized by construction (GDPR Art. 5(1)(c)). It also adds a single-property
public endpoint (`GET /api/properties/{id}/public`) that the branded booking site and direct
checkout consume, so neither downstream surface ever touches the raw entity.

Phase: **1 (MVP Sellable — direct booking)** · User story: **US-001**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a prospective guest browsing CasaZen without an account, I want to search and view published
properties (photos, location, capacity, nightly rate, CIN) so that I can decide where to book —
**without** the platform exposing the operator's identity or any internal/PII data in the API
response.

As the platform, I want every public-facing property payload to be a deliberate field whitelist,
so that no future field added to the `Property` entity is silently leaked to anonymous callers.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New read-model `PublicPropertyDto` (list item) exposes **only** a whitelist of safe fields:
  `{ id, name, description, city, postalCode, latitude, longitude, bedrooms, bathrooms, maxGuests, nightlyRate, cleaningFee, amenities, photoUrls, cinCode, cinStatus, timezone }`.
  - It **MUST NOT** contain: `ownerId`, `houseRules` (internal), `damageDeposit`, `isActive`, `createdAt`, `updatedAt`, `cancellationPolicyId`, OTA integrations, documents, or any navigation collection.
  - `cinStatus`: `"Valid" | "Missing" | "Invalid"` derived from the `IT-XXXXX-XXXXXXXXXX` format (reuse the logic behind `PropertyDetailResponse.CinStatus`).

- **AC2**: `GET /api/properties/search` (`[AllowAnonymous]`) returns `IEnumerable<PublicPropertyDto>` — **never** the raw `Property` entity. Same query parameters as today: `city`, `bedrooms`, `maxPrice`.

- **AC3**: Search returns **only** properties with `IsActive == true`. Inactive/soft-deleted properties are never visible to anonymous callers.

- **AC4**: New `GET /api/properties/{id}/public` (`[AllowAnonymous]`) returns a `PublicPropertyDetailDto` for a single **active** property — the read-model used by the branded site and checkout.
  - `PublicPropertyDetailDto` extends the AC1 whitelist with booking-relevant fields only: `{ houseRules, cancellationPolicySummary, minNights?, currency }` (still **no** `ownerId`/PII).
  - `404` if the property does not exist **or** `IsActive == false` (do not distinguish, to avoid enumeration of draft listings).

- **AC5**: Mapping lives in the service layer, not the controller. `IPropertyService.SearchAsync` (and a new `GetPublicPropertyAsync(Guid id)`) return DTOs / a DTO, so the raw entity cannot escape the public path. EF projection (`Select`) is used so `OwnerId` is never materialized into the response object graph.

- **AC6 (Regression)**: The serialized body of `GET /api/properties/search` and `GET /api/properties/{id}/public` **never** contains the substring `ownerId` or `apiKey` (assert case-insensitive on the serialized JSON), and contains no guest-PII field names.

- **AC7**: Result-set safety cap — anonymous search returns at most `50` items per call (server-enforced) to limit scraping of the catalogue; ordering deterministic (`city`, then `nightlyRate ASC`).

- **AC8**: The two public endpoints remain the **only** `[AllowAnonymous]` property reads. The authenticated owner endpoints (`GET /api/properties`, `GET /api/properties/{id}`, `/detail`) are unchanged and still return owner-scoped data behind `PropertyOwner` + `RequireContext:short-rent:property.read`.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC9**: `src/types/property.types.ts` adds `PublicPropertyDto` / `PublicPropertyDetailDto` types that **omit** `ownerId` and internal fields; the existing public `SearchPage` consumes these types (no `ownerId` referenced anywhere in the public surface).

- **AC10**: `src/api/properties.api.ts` `searchProperties()` is typed to `PublicPropertyDto[]`; new `getPublicProperty(id)` → `GET /api/properties/{id}/public`. `src/queries/use-properties.ts` exposes `useSearchProperties` (typed) and `usePublicProperty(id)`.

- **AC11**: The public `SearchPage` (`/search`) renders a CIN badge (Valid/Missing/Invalid) per result and shows **no** operator identity; results show name, city, price, capacity, photo.

- **AC12 (Regression)**: The Axios request interceptor still skips auth for `/properties/search` and additionally skips it for `/properties/*/public` (these are anonymous); no `Authorization` header is sent on the public booking read path.

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



1. Enter the primary route for `public-booking-readmodel`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New read-model `PublicPropertyDto` (list item) exposes **only** a whitelist of safe fields: | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GET /api/properties/search` (`[AllowAnonymous]`) returns `IEnumerable<PublicPropertyDto>` — **never** the raw `Property` entity. Same qu... | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | Search returns **only** properties with `IsActive == true`. Inactive/soft-deleted properties are never visible to anonymous callers. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | New `GET /api/properties/{id}/public` (`[AllowAnonymous]`) returns a `PublicPropertyDetailDto` for a single **active** property — the rea... | Outcome not met; wrong status; silent no-op |
| AC5 | L2 + L3 | Mapping lives in the service layer, not the controller. `IPropertyService.SearchAsync` (and a new `GetPublicPropertyAsync(Guid id)`) retu... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | Result-set safety cap — anonymous search returns at most `50` items per call (server-enforced) to limit scraping of the catalogue; orderi... | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | The two public endpoints remain the **only** `[AllowAnonymous]` property reads. The authenticated owner endpoints (`GET /api/properties`,... | Outcome not met; wrong status; silent no-op |
| AC9 | L2 + L3 | `src/types/property.types.ts` adds `PublicPropertyDto` / `PublicPropertyDetailDto` types that **omit** `ownerId` and internal fields; the... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 + L2 + L3 | `src/api/properties.api.ts` `searchProperties()` is typed to `PublicPropertyDto[]`; new `getPublicProperty(id)` → `GET /api/properties/{i... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L1 + L2 + L3 | The public `SearchPage` (`/search`) renders a CIN badge (Valid/Missing/Invalid) per result and shows **no** operator identity; results sh... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/DTOs/PublicPropertyDto.cs` | Create — list read-model (AC1 whitelist) |
| `Casazen.Core/DTOs/PublicPropertyDetailDto.cs` | Create — single-property public read-model (AC4) |
| `Casazen.Core/Services/IPropertyService.cs` | Modify — change `SearchAsync` return to `IEnumerable<PublicPropertyDto>`; add `Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id)` |
| `Casazen.Infrastructure/Services/PropertyService.cs` | Modify — EF `Select` projection to DTOs; `IsActive` filter; 50-row cap; reuse CIN-status calc |
| `Casazen.Web/Controllers/PropertiesController.cs` | Modify — `Search` returns `PublicPropertyDto`; add `[AllowAnonymous] GET {id}/public` (AC2–AC4, AC8) |
| `Casazen.Infrastructure/Repositories/PropertyRepository.cs` | Modify — search query exposes `IQueryable`/projection honoring `IsActive` + ordering |

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/types/property.types.ts` | Modify — add `PublicPropertyDto`, `PublicPropertyDetailDto` (omit `ownerId`) |
| `src/api/properties.api.ts` | Modify — type `searchProperties` to `PublicPropertyDto[]`; add `getPublicProperty(id)` |
| `src/queries/use-properties.ts` | Modify — typed `useSearchProperties`; add `usePublicProperty(id)` |
| `src/features/search/search-page.tsx` | Modify — render CIN badge; drop any owner field from public view |
| `src/lib/axios.ts` | Modify — skip auth for `/properties/*/public` (already skips `/properties/search`) |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **GDPR data minimization (Art. 5(1)(c))**: the public payload is a deliberate whitelist; `OwnerId` (operator personal identifier) and all internal/PII fields are excluded by construction. AC6 regression test guards this.
- **CIN (D.L. 145/2023)**: CIN code + status are the only regulatory fields surfaced publicly (transparency for guests); no other compliance internals exposed.
- **Anti-enumeration**: inactive/draft listings return `404` (not `403`) on the public detail endpoint; search result cap limits catalogue scraping.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: existing `[AllowAnonymous]` `GET /api/properties/search` (the seed) and `PropertyDetailResponse.CinStatus` logic to reuse.
- **Blocks**: `spec-direct-checkout` (checkout reads `PublicPropertyDetailDto`), `spec-branded-booking-site` (public surface consumes both read-models).
- **Related**: `spec-property-detail` (authenticated `/detail` read-model — distinct, owner-scoped; do not merge the two read-models).
- **Does not touch**: authenticated owner property endpoints, OTA integrations, pricing adapter.

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
