---
id: US-019
slug: compliance-wizards
title: Contextual compliance wizards + summary cockpit
phase: 1
type: compliance
priority: P0
status: specced
issue:
depends_on: [tenant-boundary]
blocks: [guest-check-in-portal, golden-journey-e2e]
exit_contributes_to: Italian compliance guided UX; GJ steps 3, 11–12
last_reviewed: 2026-06-19
---

# Spec — Compliance Wizards (US-019)

## Overview

Replaces dashboard-first compliance with **contextual wizards** triggered when the host or system must act: property activation, check-out/turnover. Includes a **light summary cockpit** that opens wizards on click (not duplicate forms). Guest check-in wizard is specified in `spec-guest-check-in-portal`.

**Phase:** 1 — MVP · **Type:** compliance · **Status:** specced

---

## User Story

As a host, I want step-by-step guidance when I add a property or close a stay so that I know exactly what is missing to be legally ready — without reading legal jargon.

As CasaZen, we want `Property.ComplianceStatus` to gate publishing until requirements are met.

---

## Acceptance Criteria

### Backend — Property activation wizard

- **AC1**: `Property.ComplianceStatus` enum: `Pending`, `Active`, `Suspended` (migration).

- **AC2**: `GET /api/properties/{id}/compliance/activation` returns wizard state `{ steps: [{ id, label, status: pending|complete|warning, blocker }] }` for: base data, CIN, documents, safety checklist, tourist tax comune, iCal (optional).

- **AC3**: CIN step uses `[CinCode]` validator; invalid format → step `pending` with message; link to guidance URL.

- **AC4**: `POST /api/properties/{id}/compliance/activation/complete` re-evaluates blockers; sets `ComplianceStatus = Active` only when all **blockers** resolved; else remains `Pending`.

- **AC5**: `GET /api/public/orgs/{slug}/properties` excludes properties where `ComplianceStatus != Active` (or `IsActive` false).

- **AC6**: `PropertySafetyChecklist` entity or JSON column: `{ smokeDetector, fireExtinguisher, gasCompliance, acknowledgedAt }` — step 4.

- **AC7**: Document upload reuses property documents API; wizard tracks required doc types per region (config-driven list).

### Backend — Check-out / turnover wizard

- **AC8**: `POST /api/bookings/{id}/checkout-wizard/start` validates booking `CheckedIn` or checkout day; returns steps: confirm departure, compliance summary, supplier selection (delegates to `spec-micro-marketplace-v0`), payment, property ready.

- **AC9**: `POST /api/bookings/{id}/checkout-wizard/complete` sets booking `CheckedOut`; schedules GDPR retention if applicable; returns `{ propertyReady: true }`.

- **AC10**: Incomplete checkout by end of checkout day → enqueue `CheckoutReminderJob` → email + push payload to host app.

### Backend — Summary cockpit

- **AC11**: `GET /api/compliance/summary` (org-scoped) returns counts: `propertiesPending`, `guestCheckInsIncomplete`, `checkoutsDue`, `alloggiatiFailures` with deep links to wizard routes.

### Frontend (host web)

- **AC12**: Property create/edit launches multi-step **activation wizard** (not single form); progress bar; cannot publish until Active.

- **AC13**: Compliance summary widget on dashboard; each row opens the relevant wizard.

- **AC14**: Check-out wizard UI from booking detail; step 3 embeds supplier picker.

### App host

- **AC15**: Summary screen shows same counts as AC11; tap opens WebView/deep link to web wizard for setup-heavy steps; native quick check-out trigger for AC8.

### Regulatory mapping

- **AC16**: Wizard copy references 8 areas in `.claude/context/_index.md` (CIN, Alloggiati via guest portal, tourist tax, GDPR, safety, regional notes).

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/Property.cs` | Modify — `ComplianceStatus` |
| `Casazen.Web/Controllers/ComplianceController.cs` | Create |
| `Casazen.Web/Controllers/BookingsController.cs` | Modify — checkout wizard endpoints |
| `Casazen.Infrastructure/Services/ComplianceWizardService.cs` | Create |
| `frontend/src/features/compliance/` | Create — wizards + summary |

**Complexity:** L  
**Migration:** yes  
**Dependencies:** `spec-tenant-boundary`, `spec-micro-marketplace-v0` (checkout step 3)

---

## Regulatory / Legal Gates

- Safety checklist text [COUNSEL_REQUIRED] for D.L. 145/2023 wording
- Regional document list [COUNSEL_REQUIRED] per region config

---

## Out of Scope

- Automatic comune tax remittance
- Full fiscal declaration (#3 epic)
