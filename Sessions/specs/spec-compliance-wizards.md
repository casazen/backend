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
last_reviewed: 2026-08-13
---

# Spec — Compliance Wizards (US-019)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Replaces dashboard-first compliance with **contextual wizards** triggered when the host or system must act: property activation, check-out/turnover. Includes a **light summary cockpit** that opens wizards on click (not duplicate forms). Guest check-in wizard is specified in `spec-guest-check-in-portal`.

**Phase:** 1 — MVP · **Type:** compliance · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host, I want step-by-step guidance when I add a property or close a stay so that I know exactly what is missing to be legally ready — without reading legal jargon.

As CasaZen, we want `Property.ComplianceStatus` to gate publishing until requirements are met.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Property activation wizard

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `Property.ComplianceStatus` enum: `Pending`, `Active`, `Suspended` (migration).

- **AC2**: `GET /api/properties/{id}/compliance/activation` returns wizard state `{ steps: [{ id, label, status: pending|complete|warning, blocker }] }` for: base data, CIN, documents, safety checklist, tourist tax comune, iCal (optional).

- **AC3**: CIN step uses `[CinCode]` validator; invalid format → step `pending` with message; link to guidance URL.

- **AC4**: `POST /api/properties/{id}/compliance/activation/complete` re-evaluates blockers; sets `ComplianceStatus = Active` only when all **blockers** resolved; else remains `Pending`.

- **AC5**: `GET /api/public/orgs/{slug}/properties` excludes properties where `ComplianceStatus != Active` (or `IsActive` false).

- **AC6**: `PropertySafetyChecklist` entity or JSON column: `{ smokeDetector, fireExtinguisher, gasCompliance, acknowledgedAt }` — step 4.

- **AC7**: Document upload reuses property documents API; wizard tracks required doc types per region (config-driven list).

### Backend — Check-out / turnover wizard

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: `POST /api/bookings/{id}/checkout-wizard/start` validates booking `CheckedIn` or checkout day; returns steps: confirm departure, compliance summary, supplier selection (delegates to `spec-micro-marketplace-v0`), payment, property ready.

- **AC9**: `POST /api/bookings/{id}/checkout-wizard/complete` sets booking `CheckedOut`; schedules GDPR retention if applicable; returns `{ propertyReady: true }`.

- **AC10**: Incomplete checkout by end of checkout day → enqueue `CheckoutReminderJob` → email + push payload to host app.

### Backend — Summary cockpit

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: `GET /api/compliance/summary` (org-scoped) returns counts: `propertiesPending`, `guestCheckInsIncomplete`, `checkoutsDue`, `alloggiatiFailures` with deep links to wizard routes.

### Frontend (host web)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: Property create/edit launches multi-step **activation wizard** (not single form); progress bar; cannot publish until Active.

- **AC13**: Compliance summary widget on dashboard; each row opens the relevant wizard.

- **AC14**: Check-out wizard UI from booking detail; step 3 embeds supplier picker.

### App host

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC15**: Summary screen shows same counts as AC11; tap opens WebView/deep link to web wizard for setup-heavy steps; native quick check-out trigger for AC8.

### Regulatory mapping

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: Wizard copy references 8 areas in `.claude/context/_index.md` (CIN, Alloggiati via guest portal, tourist tax, GDPR, safety, regional notes).

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



1. Enter the primary route for `compliance-wizards`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Export / Report Criteria



**Required** (export / feed / report ACs present).



### Feed / file



| Requirement | Required |

|---|---|

| Declared Content-Type matches payload (e.g. text/calendar, text/csv, application/pdf) | yes |

| Non-empty body when seed data exists | yes |

| No CF / P.IVA / secrets in filename or URL | yes |

| Documented columns/fields or VEVENT shape in AC / design | yes |



### PDF (when applicable)



| Requirement | Required |

|---|---|

| Real PDF bytes (%PDF) - not empty stub | yes |

| Readable labeled content for the intended audience | yes |

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `Property.ComplianceStatus` enum: `Pending`, `Active`, `Suspended` (migration). | Outcome not met; wrong status; silent no-op |
| AC2 | L1 + L2 + L3 | `GET /api/properties/{id}/compliance/activation` returns wizard state `{ steps: [{ id, label, status: pending/complete/warning, blocker }... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC3 | L2 + L3 | CIN step uses `[CinCode]` validator; invalid format → step `pending` with message; link to guidance URL. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC4 | L1 | `POST /api/properties/{id}/compliance/activation/complete` re-evaluates blockers; sets `ComplianceStatus = Active` only when all **blocke... | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `GET /api/public/orgs/{slug}/properties` excludes properties where `ComplianceStatus != Active` (or `IsActive` false). | Outcome not met; wrong status; silent no-op |
| AC6 | L1 + L2 + L3 | `PropertySafetyChecklist` entity or JSON column: `{ smokeDetector, fireExtinguisher, gasCompliance, acknowledgedAt }` — step 4. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L2 + L3 | Document upload reuses property documents API; wizard tracks required doc types per region (config-driven list). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC8 | L1 + L2 + L3 | `POST /api/bookings/{id}/checkout-wizard/start` validates booking `CheckedIn` or checkout day; returns steps: confirm departure, complian... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L1 + L2 + L3 | `POST /api/bookings/{id}/checkout-wizard/complete` sets booking `CheckedOut`; schedules GDPR retention if applicable; returns `{ property... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 | Incomplete checkout by end of checkout day → enqueue `CheckoutReminderJob` → email + push payload to host app. | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | `GET /api/compliance/summary` (org-scoped) returns counts: `propertiesPending`, `guestCheckInsIncomplete`, `checkoutsDue`, `alloggiatiFai... | Outcome not met; wrong status; silent no-op |
| AC12 | L2 + L3 | Property create/edit launches multi-step **activation wizard** (not single form); progress bar; cannot publish until Active. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L1 + L2 + L3 | Compliance summary widget on dashboard; each row opens the relevant wizard. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | Check-out wizard UI from booking detail; step 3 embeds supplier picker. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | Summary screen shows same counts as AC11; tap opens WebView/deep link to web wizard for setup-heavy steps; native quick check-out trigger... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | Wizard copy references 8 areas in `.claude/context/_index.md` (CIN, Alloggiati via guest portal, tourist tax, GDPR, safety, regional notes). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

---

## Regulatory / Legal Gates

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- Safety checklist text [COUNSEL_REQUIRED] for D.L. 145/2023 wording
- Regional document list [COUNSEL_REQUIRED] per region config

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- Automatic comune tax remittance
- Full fiscal declaration (#3 epic)

## Open Questions

- None (or list with owner/date before Stage 03)
