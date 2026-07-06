# Design Spec — Issue #293: Micro-Marketplace v0 — ServiceRequest loop

**Issue**: [#293](https://github.com/casazen/backend/issues/293)  
**Feature**: feat(MVP F1): micro-marketplace v0 — ServiceRequest loop (US-021)  
**Status**: complete  
**Date**: 2026-07-06

---

## API Contract

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| New | POST | `/api/service-requests` | `[Authorize]` — host org | `{ propertyId, bookingId?, supplierOrgId, category, urgency?, notes?, chargeToGuest? }` | 201 `ServiceRequestDto` / 400 / 404 / 409 |
| New | GET | `/api/service-requests` | `[Authorize]` | `?status=&propertyId=&page=&pageSize=` | 200 `{ items[], total, page, pageSize }` — host: org scope; supplier: assigned |
| New | GET | `/api/service-requests/{id}` | `[Authorize]` | — | 200 `ServiceRequestDto` / 404 |
| New | POST | `/api/service-requests/{id}/take` | `[Authorize(Policy = "RequireSupplier")]` | — | 200 `ServiceRequestDto` / 409 |
| New | POST | `/api/service-requests/{id}/complete` | `[Authorize(Policy = "RequireSupplier")]` | `{ notes? }` | 200 `ServiceRequestDto` / 409 |
| New | POST | `/api/service-requests/{id}/reject` | `[Authorize(Policy = "RequireSupplier")]` | `{ reason }` | 200 `ServiceRequestDto` / 409 |
| New | POST | `/api/service-requests/{id}/mark-paid` | `[Authorize]` — host | — | 200 `ServiceRequestDto` / 409 |
| Modify | GET | `/api/supplier/inbox` | `[Authorize(Policy = "RequireSupplier")]` | `?status=open&page=&pageSize=` | 200 `{ items: ServiceRequestSummaryDto[], total }` |

**ServiceRequestDto:** `{ id, orgId, bookingId?, propertyId, propertyName?, supplierOrgId, supplierName?, category, urgency, notes?, status, takenAt?, takenByUserId?, completedAt?, paidAt?, chargeToGuest, rejectionReason?, createdAt, updatedAt }`

**State machine:** `Richiesto → PresoInCarico|Rifiutato → InCorso? → Completato → Pagato`; invalid → 409 ProblemDetails (Italian).

**IDOR:** Host endpoints scoped via `OrgId` + `IPropertyAuthorizationService`. Supplier actions scoped via `ISupplierOrgContextResolver` matching `SupplierOrgId`.

---

## Frontend Flow

### Route Changes

No new routes. Modifications to existing authenticated routes:

| Route | Component | Auth | ProtectedRoute |
|---|---|---|---|
| `/app/short-rent/bookings/:id` | `BookingDetailPage` | Host | Yes (AppShell) |
| `/app/supplier/inbox` | `SupplierInboxPage` | Supplier | Yes (ContextRouteGuard) |

### Component Breakdown

1. **`service-request-form.tsx`** — dialog in booking detail: category, supplier picker (`GET /api/suppliers?comune=`), notes, urgency, chargeToGuest
2. **`service-request-timeline.tsx`** — status badges + mark-paid CTA on booking detail
3. **`supplier-inbox-page.tsx`** — real cards with take/complete/reject actions
4. **`service-requests.api.ts`** + **`use-service-requests.ts`** — API + React Query hooks

### i18n

Keys under `serviceRequest.*` in `it.json` and `en.json`.

---

## Security Notes

- All endpoints `[Authorize]` except none (no public surface)
- Host create validates property ownership via `IPropertyAuthorizationService`
- Supplier mutations verify `SupplierOrgId` matches JWT supplier org
- Cross-org list isolation: host sees `OrgId`; supplier sees `SupplierOrgId`
- Email notifications use existing `IEmailService` — no PII beyond property name and category
- No Stripe payment in v0 — `mark-paid` is manual confirmation only

---

## Migration Plan

- Migration `AddServiceRequest`
- Entity `ServiceRequest` with FKs: `OrgId`, `PropertyId`, `SupplierOrgId`, optional `BookingId`
- Indexes on `(OrgId, Status)`, `(SupplierOrgId, Status)`

---

## GDPR Scope

N/A — no new Guest PII fields. Uses existing property/booking references only.

---

## Open Questions

(none)
