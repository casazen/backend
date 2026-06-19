---
id: US-021
slug: micro-marketplace-v0
title: Service request host→supplier (v0 + payment tracking)
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [supplier-console-web, tenant-boundary]
blocks: [golden-journey-e2e, compliance-wizards]
exit_contributes_to: GJ steps 7–10; ecosystem supply loop
last_reviewed: 2026-06-19
---

# Spec — Micro-Marketplace v0 (US-021)

## Overview

Minimal **host → supplier** service loop: create `ServiceRequest`, supplier **presa in carico**, complete work, host marks payment. No full marketplace Connect take-rate in MVP — payment tracking + optional Stripe link later.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a host, I want to request cleaning/maintenance from an active supplier in my area so that turnover is handled without phone calls.

As a supplier, I want to accept or decline requests and mark jobs complete from my console.

---

## Acceptance Criteria

### Backend

- **AC1**: Entity `ServiceRequest` `{ Id, OrgId (host), BookingId?, PropertyId, SupplierOrgId, Category, Urgency, Notes, Status (Richiesto|PresoInCarico|InCorso|Completato|Pagato|Rifiutato), TakenAt, TakenByUserId, CompletedAt, PaidAt, ChargeToGuest }`.

- **AC2**: `POST /api/service-requests` (host) creates request; only suppliers with `SupplierProfile.Status = Active` in property comune are selectable.

- **AC3**: `POST /api/service-requests/{id}/take` (supplier) transitions `Richiesto → PresoInCarico`; sets `TakenAt`, `TakenByUserId`; optional auto `InCorso`.

- **AC4**: `POST /api/service-requests/{id}/complete` (supplier) → `Completato`.

- **AC5**: `POST /api/service-requests/{id}/reject` (supplier) → `Rifiutato` with reason.

- **AC6**: `POST /api/service-requests/{id}/mark-paid` (host) → `Pagato`; records `PaidAt` (manual confirmation MVP).

- **AC7**: `GET /api/service-requests` filtered by role: host sees org requests; supplier sees assigned inbox.

- **AC8**: Email to supplier on new `Richiesto` (SendGrid template).

- **AC9**: Invalid state transitions return `409` with Italian problem detail.

### Frontend — host web + app

- **AC10**: Booking detail → "Richiedi fornitore" flow: category, notes, supplier picker → creates AC2.

- **AC11**: Status timeline visible on booking + service detail; host can mark paid (AC6).

### Frontend — supplier console

- **AC12**: Inbox lists `Richiesto`; CTA **Presa in carico** (AC3); **Completa** (AC4).

### Golden Journey

- **AC13**: States match PLANNING.md table: step 7 `Richiesto`, 8 `PresoInCarico`, 9 `Completato`, 10 `Pagato`.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/ServiceRequest.cs` | Create |
| `Casazen.Core/Entities/SupplierProfile.cs` | Create — `Status`, `ServiceCategories`, `Comuni` |
| `Casazen.Web/Controllers/ServiceRequestsController.cs` | Create |
| `frontend/src/features/service-requests/` | Create |
| `frontend/src/features/supplier-console/` | Modify — inbox |

**Complexity:** M  
**Migration:** yes  
**Dependencies:** `spec-supplier-console-web`

---

## Out of Scope

- Stripe Connect take-rate (US-014 frozen)
- Guest charge line item (`ChargeToGuest` flag only; payment Nice)
- Supplier ratings/reviews
