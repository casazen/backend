# Design — Per-Property Direct Booking (#341)

**Issue:** #341 · **Branch:** `feature/341-per-property-direct-booking`  
**Scope:** Backend + Frontend

---

## Summary

Transform Direct Booking from a single org-level URL into a per-property hub: hosts see all properties in Vetrina, copy/preview individual booking URLs, and share slug-based public links. GUID URLs remain valid for backward compatibility.

---

## Entity Changes

| Entity | Field | Type | Notes |
|---|---|---|---|
| `Property` | `Slug` | `string?` MaxLength(100) | Unique per `(OrgId, Slug)` where not null |

**Migration:** `AddPropertySlug` — backfill from `Name` via slug sanitizer; suffix `-2`, `-3` on collision within org.

**Index:** `UIX_Properties_OrgId_Slug` filtered `Slug IS NOT NULL`.

---

## API Endpoints

| Method | Path | Change |
|---|---|---|
| GET | `/api/public/orgs/{slug}/properties/{propertySlugOrId}` | Resolve GUID or slug within org |
| GET | `/api/public/orgs/{slug}/properties` | `PublicPropertyDto` includes `slug` |
| POST | `/api/properties` | Auto-allocate slug from name on create |
| PUT | `/api/properties/{id}` | Optional slug update with validation |
| POST | `/api/public/bookings` | Reject if `ComplianceStatus != Active` |

### Slug rules

- Pattern: `^[a-z0-9]+(?:-[a-z0-9]+)*$`
- Max 100 chars
- Unique within org
- Reserved: `property`, `checkout`, `my-bookings`, `api`

---

## Frontend Changes

| File | Change |
|---|---|
| `vetrina-page.tsx` | Master-detail: property list + dynamic preview |
| `vetrina-property-list.tsx` | New — `useProperties()` selection |
| `vetrina-property-row.tsx` | New — copy URL, publish badge |
| `vetrina-preview-panel.tsx` | Dynamic `bookingSitePath` |
| `lib/booking-url.ts` | `buildPropertyBookingUrl(orgSlug, { id, slug })` |
| `property-form.tsx` | Slug field (optional edit) |
| `property.types.ts` | `slug` on Property + PublicPropertyDto |
| `org-landing-page.tsx` | Navigate with slug when present |
| `property-detail-page.tsx` | Slug URL for "Vedi sul sito pubblico" |
| `public-property-page.tsx` | Fetch via slugOrId param |
| Routes | Rename param to `:propertySlugOrId` |

### Publishable check

```ts
const isPublishable = property.isActive && property.complianceStatus === 'Active';
```

---

## Test Matrix

| AC | Test |
|---|---|
| AC1–AC4, AC12 | `e2e/direct-booking-per-property.spec.ts` |
| AC8, AC13 | Slug URL checkout in E2E |
| AC14 | GUID URL backward compat in `branded-booking-site.spec.ts` |
| AC9, AC15 | `PublicOrgControllerTests`, `PropertyServiceTests` |
| AC11 | `BookingServiceTests` compliance gate |

---

## Out of Scope

- Custom domain per property
- 301 GUID → slug redirect
- Removing org landing
