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
last_reviewed: 2026-08-13
---

# Spec — Micro-Marketplace v0 (US-021)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Minimal **host → supplier** service loop: create `ServiceRequest`, supplier **presa in carico**, complete work, host marks payment. No full marketplace Connect take-rate in MVP — payment tracking + optional Stripe link later.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host, I want to request cleaning/maintenance from an active supplier in my area so that turnover is handled without phone calls.

As a supplier, I want to accept or decline requests and mark jobs complete from my console.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Booking detail → "Richiedi fornitore" flow: category, notes, supplier picker → creates AC2.

- **AC11**: Status timeline visible on booking + service detail; host can mark paid (AC6).

### Frontend — supplier console

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: Inbox lists `Richiesto`; CTA **Presa in carico** (AC3); **Completa** (AC4).

### Golden Journey

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: States match PLANNING.md table: step 7 `Richiesto`, 8 `PresoInCarico`, 9 `Completato`, 10 `Pagato`.

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



1. Enter the primary route for `micro-marketplace-v0`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Entity `ServiceRequest` `{ Id, OrgId (host), BookingId?, PropertyId, SupplierOrgId, Category, Urgency, Notes, Status (Richiesto/PresoInCa... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 + L2 + L3 | `POST /api/service-requests` (host) creates request; only suppliers with `SupplierProfile.Status = Active` in property comune are selecta... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC3 | L1 | `POST /api/service-requests/{id}/take` (supplier) transitions `Richiesto → PresoInCarico`; sets `TakenAt`, `TakenByUserId`; optional auto... | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `POST /api/service-requests/{id}/complete` (supplier) → `Completato`. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `POST /api/service-requests/{id}/reject` (supplier) → `Rifiutato` with reason. | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | `POST /api/service-requests/{id}/mark-paid` (host) → `Pagato`; records `PaidAt` (manual confirmation MVP). | Outcome not met; wrong status; silent no-op |
| AC7 | L1 + L2 + L3 | `GET /api/service-requests` filtered by role: host sees org requests; supplier sees assigned inbox. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L1 | Email to supplier on new `Richiesto` (SendGrid template). | Outcome not met; wrong status; silent no-op |
| AC9 | L2 + L3 | Invalid state transitions return `409` with Italian problem detail. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | Booking detail → "Richiedi fornitore" flow: category, notes, supplier picker → creates AC2. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Status timeline visible on booking + service detail; host can mark paid (AC6). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Inbox lists `Richiesto`; CTA **Presa in carico** (AC3); **Completa** (AC4). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L1 | States match PLANNING.md table: step 7 `Richiesto`, 8 `PresoInCarico`, 9 `Completato`, 10 `Pagato`. | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

- Stripe Connect take-rate (US-014 frozen)
- Guest charge line item (`ChargeToGuest` flag only; payment Nice)
- Supplier ratings/reviews

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
