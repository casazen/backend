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
last_reviewed: 2026-08-13
---

# Spec — Supplier Console Web (US-022)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Authenticated **supplier** surface (separate from host console): signup/invite, activation wizard → `Active`, inbox for service requests, availability calendar, profile management. MVP must be **mobile-responsive** for steps 8–9 on phone browser.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a cleaning/maintenance supplier, I want my own console to manage profile and incoming host requests so that I can operate without using the host app.

As CasaZen admin, I want to invite pilot suppliers for a comune.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — identity & org

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Auth0 role `Supplier` (or org type `Supplier`) distinct from `PropertyOwner`.

- **AC2**: `SupplierOrg` linked to `Org` with `OrgType = Supplier`; tenant boundary RF1.

- **AC3**: `POST /api/admin/suppliers/invite` (admin) sends invite email with signup link; `POST /api/suppliers/register` (public) self-serve signup for pilot comune.

### Backend — activation wizard

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC4**: `SupplierProfile` `{ OrgId, Status (Pending|Active|Suspended), LegalName, VatNumber?, Phone, Email, Categories[], Comuni[], Bio, PhotoUrls[], TosAcceptedAt }`.

- **AC5**: `GET /api/supplier/profile/activation` returns wizard steps 1–5; `POST .../complete` sets `Active` when steps 2–5 satisfied + ToS.

- **AC6**: Only `Active` suppliers appear in `GET /api/suppliers?comune={code}` for host picker.

### Backend — operations

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC7**: `GET /api/supplier/inbox` — service requests where `SupplierOrgId` matches and status in open states.

- **AC8**: `PUT /api/supplier/availability` — simple date list or `{ date, available: bool }[]`.

- **AC9**: Supplier actions delegate to `ServiceRequestsController` (see `spec-micro-marketplace-v0`).

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Route group `/supplier/*` with separate `SupplierShell` (not host `AppShell`).

- **AC11**: Activation wizard UI (5 steps) in Italian; progress saved per step.

- **AC12**: Inbox + detail pages; **Presa in carico** and **Completa** CTAs thumb-reachable on 375px viewport.

- **AC13**: Availability calendar (month view, tap toggle).

### Golden Journey

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC14**: Steps 1–2 executable via Playwright; steps 8–9 via mobile viewport suite F1–F2.

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



1. Enter the primary route for `supplier-console-web`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Auth0 role `Supplier` (or org type `Supplier`) distinct from `PropertyOwner`. | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `SupplierOrg` linked to `Org` with `OrgType = Supplier`; tenant boundary RF1. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `POST /api/admin/suppliers/invite` (admin) sends invite email with signup link; `POST /api/suppliers/register` (public) self-serve signup... | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `SupplierProfile` `{ OrgId, Status (Pending/Active/Suspended), LegalName, VatNumber?, Phone, Email, Categories[], Comuni[], Bio, PhotoUrl... | Outcome not met; wrong status; silent no-op |
| AC5 | L1 + L2 + L3 | `GET /api/supplier/profile/activation` returns wizard steps 1–5; `POST .../complete` sets `Active` when steps 2–5 satisfied + ToS. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L1 | Only `Active` suppliers appear in `GET /api/suppliers?comune={code}` for host picker. | Outcome not met; wrong status; silent no-op |
| AC7 | L1 + L2 + L3 | `GET /api/supplier/inbox` — service requests where `SupplierOrgId` matches and status in open states. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L1 | `PUT /api/supplier/availability` — simple date list or `{ date, available: bool }[]`. | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | Supplier actions delegate to `ServiceRequestsController` (see `spec-micro-marketplace-v0`). | Outcome not met; wrong status; silent no-op |
| AC10 | L2 + L3 | Route group `/supplier/*` with separate `SupplierShell` (not host `AppShell`). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Activation wizard UI (5 steps) in Italian; progress saved per step. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Inbox + detail pages; **Presa in carico** and **Completa** CTAs thumb-reachable on 375px viewport. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | Availability calendar (month view, tap toggle). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | Steps 1–2 executable via Playwright; steps 8–9 via mobile viewport suite F1–F2. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- Native supplier app (Fase 2 — `spec-native-supplier-app`)
- Public supplier vetrina (separate spec)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
