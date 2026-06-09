## User Story

As a prospective guest, I want to visit an operator's branded CasaZen booking site, browse listings, and view property details without logging in.

As an operator, I want my public booking site to carry my brand (name, logo, colors).

## Acceptance Criteria

See `Sessions/specs/spec-branded-booking-site.md` (AC1–AC11).

Checkout payment flow deferred to `spec-direct-checkout`; route shell included.

## Technical Notes

- Backend: `PublicOrgController`, `PublicOrgDto`, org-scoped property list/detail
- Frontend: `/book/:orgSlug` public route tree, `PublicBookingShell`, GDPR cookie banner
- No EF migration required
- Depends on: spec-public-booking-readmodel (#212), spec-tenant-boundary (#202)
