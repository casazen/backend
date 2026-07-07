## User story

As a short-rent host, I want to see all my properties in Direct Booking and share a dedicated direct-booking URL for each property (with a readable slug), so I can promote individual listings without sending guests to my full org vetrina.

## Acceptance criteria

### Host UX (Fase 1)

- **AC1:** `/app/short-rent/vetrina` shows a list of all org properties (name, city, photo, publish status).
- **AC2:** Selecting a property updates the iframe preview to that property's public booking page.
- **AC3:** Each **publishable** property (`isActive && complianceStatus === 'Active'`) has a copyable direct booking URL.
- **AC4:** Non-publishable properties show disabled copy + reason (e.g. "Completa conformità").
- **AC5:** Org-level vetrina URL (`/book/{orgSlug}`) remains available as secondary/collapsible section.

### Property slugs (Fase 2)

- **AC6:** `Property.Slug` added (nullable, unique per `(OrgId, Slug)`, max 100 chars).
- **AC7:** Slug auto-generated from `Name` on property create; host can edit in property form.
- **AC8:** Public URLs prefer slug: `/book/{orgSlug}/property/{slug}`; GUID URLs remain valid.
- **AC9:** `GET /api/public/orgs/{slug}/properties/{slugOrId}` resolves slug or GUID within org scope.
- **AC10:** `PublicPropertyDto` exposes `slug` for client link building.

### Security / booking hardening

- **AC11:** `CreateDirectBookingAsync` rejects properties where `ComplianceStatus != Active` (in addition to `IsActive`).

### Tests

- **AC12:** Playwright E2E: host sees property list in Vetrina, copies URL, preview loads property page.
- **AC13:** Playwright E2E: slug URL resolves and completes checkout flow (demo mode).
- **AC14:** Playwright E2E: GUID URL still works (backward compat).
- **AC15:** Backend unit/integration tests for slug resolution and compliance booking gate.

## Technical scope

- **Backend:** `Property.Slug` migration + backfill, public API slug resolution, compliance gate on direct booking, DTO updates.
- **Frontend:** Vetrina master-detail (property list + per-property preview/copy), property form slug field, `buildPropertyBookingUrl` helper, public route param updates.

## Out of scope

- Custom domain / subdomain per property
- Removing org landing `/book/{orgSlug}`
- SEO 301 redirect GUID → slug
