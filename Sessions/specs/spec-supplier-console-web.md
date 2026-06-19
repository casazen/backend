---
id: US-022
slug: supplier-console-web
title: Supplier web console — onboarding, inbox, incarichi
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [tenant-boundary, role-onboarding]
blocks: [micro-marketplace-v0, golden-journey-e2e]
exit_contributes_to: GJ steps 1–2, 8–9; supplier ecosystem surface
last_reviewed: 2026-06-19
---

# Spec — Supplier Console Web (US-022)

## Overview

Authenticated **supplier** surface (separate from host console): signup/invite, activation wizard → `Active`, inbox for service requests, availability calendar, profile management. MVP must be **mobile-responsive** for steps 8–9 on phone browser.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a cleaning/maintenance supplier, I want my own console to manage profile and incoming host requests so that I can operate without using the host app.

As CasaZen admin, I want to invite pilot suppliers for a comune.

---

## Acceptance Criteria

### Backend — identity & org

- **AC1**: Auth0 role `Supplier` (or org type `Supplier`) distinct from `PropertyOwner`.

- **AC2**: `SupplierOrg` linked to `Org` with `OrgType = Supplier`; tenant boundary RF1.

- **AC3**: `POST /api/admin/suppliers/invite` (admin) sends invite email with signup link; `POST /api/suppliers/register` (public) self-serve signup for pilot comune.

### Backend — activation wizard

- **AC4**: `SupplierProfile` `{ OrgId, Status (Pending|Active|Suspended), LegalName, VatNumber?, Phone, Email, Categories[], Comuni[], Bio, PhotoUrls[], TosAcceptedAt }`.

- **AC5**: `GET /api/supplier/profile/activation` returns wizard steps 1–5; `POST .../complete` sets `Active` when steps 2–5 satisfied + ToS.

- **AC6**: Only `Active` suppliers appear in `GET /api/suppliers?comune={code}` for host picker.

### Backend — operations

- **AC7**: `GET /api/supplier/inbox` — service requests where `SupplierOrgId` matches and status in open states.

- **AC8**: `PUT /api/supplier/availability` — simple date list or `{ date, available: bool }[]`.

- **AC9**: Supplier actions delegate to `ServiceRequestsController` (see `spec-micro-marketplace-v0`).

### Frontend

- **AC10**: Route group `/supplier/*` with separate `SupplierShell` (not host `AppShell`).

- **AC11**: Activation wizard UI (5 steps) in Italian; progress saved per step.

- **AC12**: Inbox + detail pages; **Presa in carico** and **Completa** CTAs thumb-reachable on 375px viewport.

- **AC13**: Availability calendar (month view, tap toggle).

### Golden Journey

- **AC14**: Steps 1–2 executable via Playwright; steps 8–9 via mobile viewport suite F1–F2.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Enums/OrgType.cs` | Modify — add `Supplier` |
| `Casazen.Web/Controllers/SupplierProfileController.cs` | Create |
| `Casazen.Web/Controllers/AdminSuppliersController.cs` | Create |
| `frontend/src/routes/supplier/` | Create — shell + pages |
| `docs/AUTH0_SETUP.md` | Modify — Supplier role |

**Complexity:** L  
**Migration:** yes  
**Dependencies:** `spec-tenant-boundary`, `spec-role-onboarding`

---

## Out of Scope

- Native supplier app (Fase 2 — `spec-native-supplier-app`)
- Public supplier vetrina (separate spec)
