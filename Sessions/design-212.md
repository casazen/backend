# Design — Issue #212 Public Booking Read-Model (US-001)

> **Stage 02 — Design** · Spec: `Sessions/specs/spec-public-booking-readmodel.md` (US-001) · Phase 1 (MVP Sellable — direct booking)
> **Architecture**: AD-3 (anonymous discovery surface), GDPR Art. 5(1)(c) data minimization by construction
> **Stack**: .NET 10 · EF Core · PostgreSQL (Supabase) · layered `Casazen.Core` / `Casazen.Infrastructure` / `Casazen.Web` · React 19 SPA (`casazen/frontend`)
> **Specialist synthesis**: `api-designer` (API Contract + Migration Plan) · `frontend-designer` (Frontend Flow + ProtectedRoute) · `security-by-design` (Security Notes + GDPR Scope).

This spec replaces the raw `Property` entity leak on the anonymous search endpoint with deliberate **public read-model DTOs** (`PublicPropertyDto`, `PublicPropertyDetailDto`). Today `GET /api/properties/search` is `[AllowAnonymous]` and returns `ActionResult<IEnumerable<Property>>`, exposing `OwnerId` (operator Auth0 `sub`), `IsActive`, timestamps, and other internal fields. US-001 hardens both the list and a new single-property public endpoint so anonymous callers receive a field whitelist only; mapping and EF `Select` projection live in the service layer so `OwnerId` is never materialized on the public path.

**Grounding note (verified against source):** `PropertiesController.Search` (line 200–209) returns raw `Property` entities. `PropertyRepository.SearchAsync` already filters `IsActive` but has no row cap or deterministic ordering. `PropertyService.ResolveCinStatus` (private, `IT-\d{5}-\d{10}` regex) powers `PropertyDetailResponse.CinStatus` and will be reused/extracted for public DTOs. Frontend `/search` is a **public route** (no `<ProtectedRoute>`); `axios` already skips auth for `/properties/search` but not `/properties/*/public`. `PropertySearchCard` currently types `Property` (includes `ownerId`) and shows `address` — both must change to match the whitelist.

**Branch for Stage 03:** `feature/212-public-booking-readmodel`

---

## API Contract

**Conventions** — JSON camelCase; enums serialized as strings via `JsonStringEnumConverter` (`Program.cs`). Public endpoints bypass the tenant global query filter (no principal); they filter `IsActive == true` explicitly. Authenticated owner endpoints are **unchanged** in contract and auth.

### A. Changed / new endpoints (full detail)

| # | Method | Path | Request schema | Response schema | Auth requirement (decision) |
|---|---|---|---|---|---|
| 1 | `GET` | `/api/properties/search` | **Query** (unchanged): `city?: string`, `bedrooms?: int` (minimum bedrooms, inclusive `>=`), `maxPrice?: decimal` | `200 PublicPropertyDto[]` (max **50** items). Empty array if no matches. | **`[AllowAnonymous]` — explicit public justification:** anonymous property discovery for prospective guests without an account (US-001 seed of direct-booking funnel). No `Authorization` header required or expected. |
| 2 | `GET` | `/api/properties/{id}/public` | **Path:** `id: Guid` | `200 PublicPropertyDetailDto` when property exists **and** `IsActive == true`; **`404`** when not found or inactive (same response — anti-enumeration). | **`[AllowAnonymous]` — explicit public justification:** single-property read-model for branded booking site and direct checkout (downstream specs). No operator identity or internal fields. |

### B. `PublicPropertyDto` (list item — AC1 whitelist)

```json
{
  "id": "uuid",
  "name": "string",
  "description": "string",
  "city": "string",
  "postalCode": "string",
  "latitude": 0.0,
  "longitude": 0.0,
  "bedrooms": 0,
  "bathrooms": 0,
  "maxGuests": 0,
  "nightlyRate": 0.00,
  "cleaningFee": 0.00,
  "amenities": ["Wifi", "Parking"],
  "photoUrls": ["https://..."],
  "cinCode": "IT-12345-1234567890",
  "cinStatus": "Valid",
  "timezone": "Europe/Rome"
}
```

| Field | Type | Notes |
|---|---|---|
| `id` | `Guid` | Public catalogue identifier |
| `name` | `string` | Display name |
| `description` | `string` | Marketing copy |
| `city` | `string` | Location (city only — **`address` excluded** from whitelist) |
| `postalCode` | `string` | CAP |
| `latitude` / `longitude` | `decimal` | Map pin |
| `bedrooms` / `bathrooms` / `maxGuests` | `int` | Capacity |
| `nightlyRate` / `cleaningFee` | `decimal` | Pricing (EUR context; no `damageDeposit`) |
| `amenities` | `string[]` | Enum names (`PropertyAmenity.ToString()`) |
| `photoUrls` | `string[]` | Public image URLs |
| `cinCode` | `string?` | Regulatory code (D.L. 145/2023) |
| `cinStatus` | `"Valid" \| "Missing" \| "Invalid"` | Derived via shared `ResolveCinStatus(cinCode)` |
| `timezone` | `string` | IANA ID (default `Europe/Rome`) |

**MUST NOT appear:** `ownerId`, `orgId`, `address`, `houseRules`, `damageDeposit`, `isActive`, `createdAt`, `updatedAt`, `cancellationPolicyId`, OTA fields, `apiKey`, documents, navigation collections.

### C. `PublicPropertyDetailDto` (single property — AC4)

Extends the AC1 field set **plus** booking-relevant fields:

```json
{
  "...all PublicPropertyDto fields...",
  "houseRules": "string",
  "cancellationPolicySummary": "string",
  "minNights": null,
  "currency": "EUR"
}
```

| Additional field | Type | Source / rule |
|---|---|---|
| `houseRules` | `string` | `Property.HouseRules` (guest-facing rules; distinct from internal ops data) |
| `cancellationPolicySummary` | `string` | `CancellationPolicy.Description` when `CancellationPolicyId` is set; else `""` (never expose policy `Id` or refund-hour internals on the public path) |
| `minNights` | `int?` | **`null` in US-001** — `Property` has no `MinNights` column today; reserved for `spec-direct-checkout` / future booking rules |
| `currency` | `string` | Constant **`"EUR"`** in US-001 (`Property` has no `Currency` column; Italian MVP default) |

### D. Search behaviour (server-enforced — AC3, AC7)

| Rule | Implementation |
|---|---|
| Active only | `WHERE IsActive == true` (already in repository; retained in projection query) |
| Row cap | `.Take(50)` after filters |
| Ordering | `.OrderBy(p => p.City).ThenBy(p => p.NightlyRate)` (deterministic, scrape-resistant) |
| Projection | EF Core `.Select(...)` → `PublicPropertyDto` in repository or service — **`OwnerId` never loaded** |

### E. Service / controller changes

| Layer | Change |
|---|---|
| `IPropertyService.SearchAsync` | Return type → `Task<IEnumerable<PublicPropertyDto>>` |
| `IPropertyService` (new) | `Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id)` |
| `PropertyService` | EF `Select` projections; extract/share `ResolveCinStatus`; detail includes `.Include(p => p.CancellationPolicy)` for summary only |
| `PropertiesController.Search` | `ActionResult<IEnumerable<PublicPropertyDto>>` |
| `PropertiesController` (new) | `[HttpGet("{id}/public")] [AllowAnonymous] GetPublic(Guid id)` → `404` if null |

### F. Endpoints explicitly unchanged (auth restated — AC8)

| Endpoint | Auth requirement (decision) |
|---|---|
| `GET /api/properties` | `[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:property.read` — returns owner-scoped `Property[]` |
| `GET /api/properties/{id}` | Same — raw `Property` for owner |
| `GET /api/properties/{id}/detail` | Same — `PropertyDetailResponse` (includes `ownerId`, documents, OTA summary **without** API keys) |
| `GET /api/properties/health` | `[AllowAnonymous]` — liveness, no tenant data |
| All write / image / document verbs | Unchanged `[Authorize]` + `RequireContext:short-rent:property.write` |

### G. Regression contract (AC6)

Integration tests assert serialized JSON of endpoints **#1** and **#2** contains neither `ownerId` nor `apiKey` (case-insensitive) and no guest-PII field names (`email`, `phoneNumber`, `documentNumber`, etc.).

---

## Frontend Flow

Repo `casazen/frontend` (React 19, feature-slice, TanStack Query, Auth0). US-001 modifies the **existing public search surface** and adds API/query plumbing for the public detail read-model; it does **not** introduce a public property-detail page route (owned by `spec-branded-booking-site`).

### Route changes & guard status

| Route | Status in US-001 | Guard |
|---|---|---|
| `/search` | **Modified** — consumes `PublicPropertyDto`; renders CIN badge per result | **Public** — intentionally **not** wrapped in `<ProtectedRoute>` (anonymous discovery) |
| `/login` | Unchanged | Public |
| `/app/*` (owner console) | Unchanged | **`<ProtectedRoute>`** (existing) — not modified by this US |

> **Gate G5:** No new authenticated routes are introduced. The only route touched (`/search`) is public by design. All owner-console routes remain behind existing `<ProtectedRoute>`.

### Component breakdown

| Component / file | Type | Responsibility |
|---|---|---|
| `src/types/property.types.ts` | modify | Add `PublicPropertyDto` (AC1 fields + `cinStatus`) and `PublicPropertyDetailDto` (extends list DTO). Types **omit** `ownerId`, `isActive`, timestamps, internal IDs. Reuse existing `CinStatus` union. |
| `src/api/properties.api.ts` | modify | `search()` return type → `PublicPropertyDto[]`; map FE filters → API params (`minBedrooms` → `bedrooms`, forward only `city`/`bedrooms`/`maxPrice`). New `getPublicProperty(id)` → `GET /properties/{id}/public`. |
| `src/queries/use-properties.ts` | modify | `useSearchProperties` typed to `PublicPropertyDto[]`; new `usePublicProperty(id)` hook (enabled when `id` truthy; for downstream checkout/branded site — **not wired to SearchPage in US-001**). |
| `src/lib/axios.ts` | modify | Extend `publicEndpoints` to match `/properties/*/public` (regex or `includes('/public')` guard alongside existing `/properties/search`). |
| `src/features/search/search-page.tsx` | modify | Import `PublicPropertyDto` instead of `Property`; remove any `ownerId` usage. |
| `src/features/search/components/search-results.tsx` | modify | Props typed to `PublicPropertyDto[]`. |
| `src/features/search/components/property-search-card.tsx` | modify | Accept `PublicPropertyDto`; show **city** (not `address`); first `photoUrls[0]` as hero image with placeholder fallback; render `<PropertyCinBadge cinStatus={...} cinCode={...} />`; display name, city, price, capacity, photo. **No operator identity.** |
| `src/features/properties/components/property-cin-badge.tsx` | reuse | Import into search card (no move required; optional future extraction to `src/components/shared/`). |

### Search UX (AC11)

- Each result card shows Italian CIN badge (`CIN valido` / `CIN mancante` / `CIN non valido`) via existing `PropertyCinBadge`.
- Price: `formatCurrency(property.nightlyRate)` (EUR).
- Capacity badges: bedrooms, bathrooms, maxGuests (unchanged layout).
- `handleViewDetails` remains a stub / `console.log` in US-001 — navigation to a public detail page is deferred to `spec-branded-booking-site`.

### Data flow

```
Anonymous visitor → /search (no auth)
  → useSearchProperties(filters)
  → propertiesApi.search({ city, bedrooms, maxPrice })
  → GET /api/properties/search [AllowAnonymous]
  → PublicPropertyDto[] rendered in SearchResults

(Future) branded site / checkout
  → usePublicProperty(id)
  → GET /api/properties/{id}/public [AllowAnonymous]
  → PublicPropertyDetailDto
```

### Axios auth skip (AC12)

Request interceptor public-path allowlist:

| Pattern | Auth header |
|---|---|
| `/health`, `/auth/*` | Skipped (existing) |
| `/properties/search` | Skipped (existing) |
| `/properties/{uuid}/public` | **Skipped (new)** |

---

## Security Notes

### Anonymous vs authenticated paths

| Surface | Endpoints | Principal | Data exposed |
|---|---|---|---|
| **Public booking read** | `GET /search`, `GET /{id}/public` | None (`[AllowAnonymous]`) | `PublicPropertyDto` / `PublicPropertyDetailDto` whitelist only |
| **Owner console** | `GET /properties`, `/{id}`, `/{id}/detail`, writes | Auth0 JWT + `PropertyOwner` + `RequireContext:*` | Full owner-scoped models (incl. `ownerId`, documents, OTA summary sans keys) |

The two public endpoints are the **only** `[AllowAnonymous]` property reads (AC8). Controller-level `[Authorize]` on `PropertiesController` is overridden per-action by `[AllowAnonymous]` on `Search` and the new `GetPublic`.

### PII / identity data flow

- **`OwnerId`** is the operator's Auth0 `sub` — personal identifier under GDPR. It is **excluded by construction** from public DTOs via EF `Select` projection (never loaded into the response object graph).
- **`OrgId`** is likewise excluded (tenant key, not guest-facing).
- **`address`** is excluded from the list DTO to reduce precise geolocation of the operator's asset before booking commitment; city + postal code + lat/long remain for discovery maps.
- **Guest PII** (`Guest` entity: name, email, phone, document) does not appear on these endpoints and is out of scope for the public read-model.
- **`houseRules`** on the detail DTO are operator-authored guest-facing text (intentionally public for booking transparency).

### OTA keys / secrets hygiene

- OTA API keys remain in **configuration** (`OTA:{platform}:ApiKey`), never in API responses. Public endpoints do not touch OTA adapters. Authenticated `/detail` continues to return `OtaIntegrationSummaryDto` without `ApiKey` (unchanged).
- Stripe keys unchanged in config/env. No new secrets introduced.

### Anti-enumeration & abuse controls

| Control | Mechanism |
|---|---|
| Draft/inactive listings | `GET /{id}/public` returns **`404`** for missing or `IsActive == false` (indistinguishable) |
| Catalogue scraping | Search capped at **50** results per request; deterministic ordering |
| IDOR on public detail | Only active properties returned; no cross-tenant filter needed (public catalogue is cross-org by design) |

### Threat summary (STRIDE)

| Threat | Vector | Mitigation |
|---|---|---|
| **Information disclosure** | Anonymous search returns raw `Property` with `ownerId` | Whitelist DTOs + EF projection; AC6 JSON regression guard |
| **Information disclosure** | Future entity fields auto-serialize on public path | Service-layer mapping only — new `Property` columns do not appear unless explicitly added to DTO |
| **Information disclosure** | Guess UUID of draft property | `404` for inactive (no `403` distinction) |
| **Repudiation / scraping** | Bulk harvest of catalogue | 50-row server cap |
| **Tampering** | Client adds `Authorization` on public path | Harmless — endpoint ignores token; interceptor skip prevents accidental token leak to logs |
| **Elevation** | Anonymous caller hits owner endpoints | `[Authorize]` + ownership checks unchanged on all non-public verbs |

---

## Migration Plan

**N/A — no schema changes.**

US-001 introduces read-model DTOs and EF `Select` projections only. No new tables, columns, or indexes. `PublicPropertyDetailDto.MinNights` is `null` until a future migration adds a booking-rules column (tracked by `spec-direct-checkout`).

---

## GDPR Scope

**Regulatory driver:** GDPR Art. 5(1)(c) — data minimisation. Issue label `compliance:gdpr`.

**Guest PII:** Not involved on these endpoints. No `Guest` fields are read or returned. Guest-data GDPR scope is **N/A** for this US.

**Operator identity (relevant PII):**

| Data element | Classification | US-001 treatment |
|---|---|---|
| `OwnerId` (Auth0 `sub`) | Operator personal identifier | **Excluded** from `PublicPropertyDto` and `PublicPropertyDetailDto` by whitelist + EF projection |
| `OrgId` | Tenant business key | **Excluded** from public payloads |
| `address` (full street) | Location data tied to operator asset | **Excluded** from list DTO; detail DTO follows same whitelist (address not in AC4 extension set) |
| `cinCode` / `cinStatus` | Regulatory transparency (D.L. 145/2023) | **Included** — legally required guest-facing disclosure, not operator identity |
| `houseRules`, `cancellationPolicySummary` | Operator-authored guest-facing content | **Included** on detail DTO only — necessary for informed booking decision |

**Lawful basis for anonymous processing:** Legitimate interest (platform catalogue display to prospective guests) with minimisation safeguards. No new consent flow required — no personal data of the **caller** is collected on these read endpoints.

**Regression guard (AC6):** Automated test ensures serialised public responses never contain `ownerId`, `apiKey`, or guest-PII field names — continuous compliance check against scope creep.

---

## Open Questions

All resolved.

1. **`cancellationPolicySummary` shape — full policy object vs summary string?**
   **Resolved:** single `string` from `CancellationPolicy.Description` (or `""`). Do not expose policy `Id`, `FullRefundHours`, or `PartialRefundPercent` on the anonymous path.

2. **`minNights` and `currency` — no DB columns today?**
   **Resolved:** `minNights: null` (optional, reserved). `currency: "EUR"` hardcoded constant in service mapping until a `Property.Currency` column is introduced by a future spec.

3. **`address` in list cards — spec whitelist omits it?**
   **Resolved:** remove `address` from public search cards; show `city` (+ optional `postalCode`). Aligns with AC1 whitelist and reduces precise geolocation exposure pre-booking.

4. **FE search filter mismatch (`minBedrooms` vs API `bedrooms`)?**
   **Resolved:** `propertiesApi.search` maps `minBedrooms` → `bedrooms` and drops unsupported filter fields (`minPrice`, `maxBedrooms`, etc.) until a future search-spec expands the API. US-001 does not widen backend query params (AC2: same as today).

5. **Extract `ResolveCinStatus` for reuse?**
   **Resolved:** promote to `internal static` on `PropertyService` (or small `CinStatusResolver` helper in `Casazen.Core`) shared by `GetPropertyDetailAsync`, `SearchAsync`, and `GetPublicPropertyAsync` — single regex source of truth.

6. **Public detail page route in US-001?**
   **Resolved:** no. `usePublicProperty` + `getPublicProperty` are added as plumbing; UI route deferred to `spec-branded-booking-site`. SearchPage `handleViewDetails` stays stubbed.

7. **Repository vs service projection?**
   **Resolved:** projection in **service layer** (AC5) using `IQueryable` from repository if needed; repository may expose `IQueryable<Property>` for composable `Select`, but DTO mapping stays in `PropertyService` so controllers cannot accidentally return entities.
