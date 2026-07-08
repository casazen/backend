# Design — Compliance Wizards (#295 / US-019)

**Issue:** [#295](https://github.com/casazen/backend/issues/295) · **Spec:** `Sessions/specs/spec-compliance-wizards.md`  
**Branch:** `feature/295-compliance-wizards` · **Scope:** Backend only (no #296 guest portal, no #341 slug, no AI supplier)

---

## Summary

Contextual compliance wizards for property activation and check-out turnover, plus an org-scoped summary cockpit. `Property.ComplianceStatus` gates public listing until activation blockers are resolved.

---

## Data model

| Entity / column | Type | Notes |
|---|---|---|
| `Property.ComplianceStatus` | `PropertyComplianceStatus` enum | `Pending` (default), `Active`, `Suspended` |
| `Property.SafetyChecklistJson` | jsonb nullable | `{ smokeDetector, fireExtinguisher, gasCompliance, acknowledgedAt }` |
| `Booking.CheckoutReminderJobId` | string nullable | Hangfire scheduled job id for checkout reminder |

**Migration:** `AddPropertyComplianceStatus` — add columns + CIN backfill SQL (`Active` where valid CIN regex).

---

## API

| Method | Path | Auth | Response |
|---|---|---|---|
| GET | `/api/properties/{id}/compliance/activation` | PropertyOwner | `{ complianceStatus, steps[] }` |
| POST | `/api/properties/{id}/compliance/activation/complete` | PropertyOwner | `{ complianceStatus, blockers[]? }` |
| GET | `/api/compliance/summary` | PropertyOwner (org) | `{ propertiesPending, guestCheckInsIncomplete, checkoutsDue, alloggiatiFailures }` |
| POST | `/api/bookings/{id}/checkout-wizard/start` | PropertyOwner | `{ steps[] }` / 409 |
| POST | `/api/bookings/{id}/checkout-wizard/complete` | PropertyOwner | `{ propertyReady, bookingStatus }` |
| GET | `/api/public/orgs/{slug}/properties` | Public | excludes `ComplianceStatus != Active` |

### Activation steps (blockers unless noted)

1. `base-data` — name, address, city, bedrooms, maxGuests  
2. `cin` — `[CinCode]` validator; guidance URL from config  
3. `documents` — required types per region (`Compliance:RequiredDocuments`)  
4. `safety-checklist` — all three safety booleans + `acknowledgedAt`  
5. `tourist-tax-comune` — active `TouristTaxRate` for property city  
6. `ical` — optional (warning only); import URL or export feed  

---

## Services & jobs

- `IComplianceWizardService` / `ComplianceWizardService` — wizard state, summary counts, checkout flow  
- `CheckoutReminderJob.SendCheckoutReminderAsync` — email + push payload via `INotificationService`  
- On checkout-wizard **start**: schedule reminder at checkout day + `Compliance:CheckoutReminderHourLocal` (property TZ)  
- On checkout-wizard **complete**: cancel scheduled job; set `CheckedOut`; extend guest `DataRetentionUntil`  

---

## Config (`appsettings` → `Compliance`)

- `CinGuidanceUrl`, `CheckoutReminderHourLocal`, `GdprRetentionYears`, `RequiredDocuments` (region → doc type list)

---

## Tests

| Suite | Count | Focus |
|---|---|---|
| `ComplianceWizardServiceTests` | 5 | activation steps, complete gate, summary |
| `ComplianceWizardIntegrationTests` | 4 | HTTP activation, summary, checkout wizard, public filter |

---

## Out of scope (#295)

Guest check-in portal (#296), public slug (#341), AI supplier discovery, frontend wizards.
