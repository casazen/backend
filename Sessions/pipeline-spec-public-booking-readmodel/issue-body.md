## User Story

As a prospective guest browsing CasaZen without an account, I want to search and view published properties (photos, location, capacity, nightly rate, CIN) so that I can decide where to book — **without** the platform exposing the operator's identity or any internal/PII data in the API response.

As the platform, I want every public-facing property payload to be a deliberate field whitelist, so that no future field added to the `Property` entity is silently leaked to anonymous callers.

## Acceptance Criteria

### Backend

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

- **AC9**: `src/types/property.types.ts` adds `PublicPropertyDto` / `PublicPropertyDetailDto` types that **omit** `ownerId` and internal fields; the existing public `SearchPage` consumes these types (no `ownerId` referenced anywhere in the public surface).

- **AC10**: `src/api/properties.api.ts` `searchProperties()` is typed to `PublicPropertyDto[]`; new `getPublicProperty(id)` → `GET /api/properties/{id}/public`. `src/queries/use-properties.ts` exposes `useSearchProperties` (typed) and `usePublicProperty(id)`.

- **AC11**: The public `SearchPage` (`/search`) renders a CIN badge (Valid/Missing/Invalid) per result and shows **no** operator identity; results show name, city, price, capacity, photo.

- **AC12 (Regression)**: The Axios request interceptor still skips auth for `/properties/search` and additionally skips it for `/properties/*/public` (these are anonymous); no `Authorization` header is sent on the public booking read path.

## Technical Notes

### Backend

| File | Action |
|---|---|
| `Casazen.Core/DTOs/PublicPropertyDto.cs` | Create — list read-model (AC1 whitelist) |
| `Casazen.Core/DTOs/PublicPropertyDetailDto.cs` | Create — single-property public read-model (AC4) |
| `Casazen.Core/Services/IPropertyService.cs` | Modify — change `SearchAsync` return to `IEnumerable<PublicPropertyDto>`; add `Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id)` |
| `Casazen.Infrastructure/Services/PropertyService.cs` | Modify — EF `Select` projection to DTOs; `IsActive` filter; 50-row cap; reuse CIN-status calc |
| `Casazen.Web/Controllers/PropertiesController.cs` | Modify — `Search` returns `PublicPropertyDto`; add `[AllowAnonymous] GET {id}/public` (AC2–AC4, AC8) |
| `Casazen.Infrastructure/Repositories/PropertyRepository.cs` | Modify — search query exposes `IQueryable`/projection honoring `IsActive` + ordering |

### Frontend

| File | Action |
|---|---|
| `src/types/property.types.ts` | Modify — add `PublicPropertyDto`, `PublicPropertyDetailDto` (omit `ownerId`) |
| `src/api/properties.api.ts` | Modify — type `searchProperties` to `PublicPropertyDto[]`; add `getPublicProperty(id)` |
| `src/queries/use-properties.ts` | Modify — typed `useSearchProperties`; add `usePublicProperty(id)` |
| `src/features/search/search-page.tsx` | Modify — render CIN badge; drop any owner field from public view |
| `src/lib/axios.ts` | Modify — skip auth for `/properties/*/public` (already skips `/properties/search`) |

### Infrastructure impact

- **EF migration**: None expected — read-model DTOs and projection only; no schema changes.
- **OTA integration**: No impact — does not touch OTA adapters or integrations.
- **Background jobs**: None — no new or modified background jobs.

## Compliance

- **GDPR data minimization (Art. 5(1)(c))**: the public payload is a deliberate whitelist; `OwnerId` (operator personal identifier) and all internal/PII fields are excluded by construction. AC6 regression test guards this.
- **CIN (D.L. 145/2023)**: CIN code + status are the only regulatory fields surfaced publicly (transparency for guests); no other compliance internals exposed.
- **Anti-enumeration**: inactive/draft listings return `404` (not `403`) on the public detail endpoint; search result cap limits catalogue scraping.

## Dependencies

- **Requires**: existing `[AllowAnonymous]` `GET /api/properties/search` (the seed) and `PropertyDetailResponse.CinStatus` logic to reuse.
- **Blocks**: `spec-direct-checkout` (checkout reads `PublicPropertyDetailDto`), `spec-branded-booking-site` (public surface consumes both read-models).
- **Related**: `spec-property-detail` (authenticated `/detail` read-model — distinct, owner-scoped; do not merge the two read-models).
- **Does not touch**: authenticated owner property endpoints, OTA integrations, pricing adapter.
