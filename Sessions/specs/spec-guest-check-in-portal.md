---
id: US-020
slug: guest-check-in-portal
title: Guest self-service check-in portal + Alloggiati auto
phase: 1
type: compliance
priority: P0
status: specced
issue:
depends_on: [compliance-wizards, direct-checkout]
blocks: [golden-journey-e2e]
exit_contributes_to: GJ step 6 — guest check-in + Alloggiati; works for direct and OTA bookings
last_reviewed: 2026-08-13
---

# Spec — Guest Check-In Portal (US-020)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

When a booking is **Confirmed** (direct or OTA/iCal), the system emails the guest a secure link to complete check-in data (identity, GDPR consent). On completion, **Alloggiati Web** is enqueued automatically. Host receives alerts if data is missing before arrival.

**Phase:** 1 — MVP · **Type:** compliance · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a guest, I want to fill in my details online before arrival so that check-in is fast and I do not need to install an app.

As a host, I want Alloggiati filed automatically when the guest completes the form, and to be notified if they do not.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Entity `GuestCheckInSession` `{ Id, BookingId, OrgId, TokenHash, ExpiresAt, Status (Inviato|InCompilazione|Completo|AlloggiatiInviato|Scaduto), SentAt, CompletedAt }`.

- **AC2**: `GuestCheckInSendJob` (Hangfire): X days before check-in (config default 3), for each `Confirmed` booking without `Completo` session, create session + send email with link `https://{app}/check-in/{token}`.

- **AC3**: `GET /api/public/check-in/{token}` (`[AllowAnonymous]`) returns `{ propertyName, checkIn, checkOut, guestPrefill?, status }`; 404 if expired/invalid.

- **AC4**: `POST /api/public/check-in/{token}` accepts guest identity fields + GDPR consents; validates; updates `Guest` record; sets session `Completo`.

- **AC5**: On `Completo`, enqueue `AlloggiatiWebReportJob` for the booking (same path as manual check-in AC7 US-002) — **never inline** HTTP to Questura.

- **AC6**: `GuestCheckInReminderJob`: 24h before check-in, if session not `Completo`, notify host (email + `DeviceNotification` for app).

- **AC7**: Host `POST /api/bookings/{id}/check-in/resend-link` regenerates token and emails guest.

- **AC8**: Rate-limit public endpoints per token/IP; token single-use for submit (replay returns 409).

- **AC9**: Works identically for `BookingSource.Direct` and OTA-sourced bookings.

### Frontend — guest (public)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Mobile-first `/check-in/:token` page: Italian copy, document fields, consent checkboxes, progress indicator.

- **AC11**: Success screen confirms data received; no Auth0.

### Frontend — host web + app

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: Booking detail shows check-in session status badge + "Invia sollecito" button.

- **AC13**: App push (AC6) deep-links to booking detail.

### Regression

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC14**: Manual `POST /api/bookings/{id}/check-in` still works for host-entered data (fallback).

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



1. Enter the primary route for `guest-check-in-portal`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Entity `GuestCheckInSession` `{ Id, BookingId, OrgId, TokenHash, ExpiresAt, Status (Inviato/InCompilazione/Completo/AlloggiatiInviato/Sca... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GuestCheckInSendJob` (Hangfire): X days before check-in (config default 3), for each `Confirmed` booking without `Completo` session, cre... | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `GET /api/public/check-in/{token}` (`[AllowAnonymous]`) returns `{ propertyName, checkIn, checkOut, guestPrefill?, status }`; 404 if expi... | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `POST /api/public/check-in/{token}` accepts guest identity fields + GDPR consents; validates; updates `Guest` record; sets session `Compl... | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | On `Completo`, enqueue `AlloggiatiWebReportJob` for the booking (same path as manual check-in AC7 US-002) — **never inline** HTTP to Ques... | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | `GuestCheckInReminderJob`: 24h before check-in, if session not `Completo`, notify host (email + `DeviceNotification` for app). | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | Host `POST /api/bookings/{id}/check-in/resend-link` regenerates token and emails guest. | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | Rate-limit public endpoints per token/IP; token single-use for submit (replay returns 409). | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | Works identically for `BookingSource.Direct` and OTA-sourced bookings. | Outcome not met; wrong status; silent no-op |
| AC10 | L2 + L3 | Mobile-first `/check-in/:token` page: Italian copy, document fields, consent checkboxes, progress indicator. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Success screen confirms data received; no Auth0. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L1 | Booking detail shows check-in session status badge + "Invia sollecito" button. | Outcome not met; wrong status; silent no-op |
| AC13 | L1 | App push (AC6) deep-links to booking detail. | Outcome not met; wrong status; silent no-op |
| AC14 | L1 | Manual `POST /api/bookings/{id}/check-in` still works for host-entered data (fallback). | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Entities/GuestCheckInSession.cs` | Create |
| `Casazen.Web/Controllers/PublicCheckInController.cs` | Create |
| `Casazen.Web/BackgroundJobs/GuestCheckInSendJob.cs` | Create |
| `Casazen.Web/BackgroundJobs/GuestCheckInReminderJob.cs` | Create |
| `frontend/src/features/public-check-in/` | Create |

**Complexity:** M  
**Migration:** yes  
**Dependencies:** `spec-compliance-wizards`, existing `AlloggiatiWebReportJob`

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

- Guest data minimization (GDPR Art. 5); retention via existing `Guest` fields
- Alloggiati timing: within 24h of arrival — job scheduling must respect check-in date

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- SMS gateway (email only MVP; SMS Should)
- Guest mobile app

## Open Questions

- None (or list with owner/date before Stage 03)
