---
id: US-025
slug: native-host-app
title: Expo host app — calendar, bookings, push (complement to web)
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [micro-marketplace-v0, guest-check-in-portal, ical-calendar-sync]
blocks: [golden-journey-e2e]
exit_contributes_to: GJ app suite M1–M7; on-the-go host operations
last_reviewed: 2026-08-13
---

# Spec — Native Host App (US-025)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

**React Native + Expo** iOS/Android app for hosts — **subset** of web console focused on mobility: calendar, booking detail, service requests, push notifications, quick check-out. Setup-heavy flows (full property wizard, Connect KYC, billing) remain web via deep link/WebView.

Replaces deprecated `spec-pwa-host-shell` strategy.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host on the go, I want to see today's bookings and respond to guest check-in alerts on my phone so that I do not need a laptop at the property.

As CasaZen, we need web/app **data parity** for Golden Journey steps 5–7, 10–12.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### App scaffold

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Expo project in `casazen/mobile` (or monorepo package) with EAS build profiles `development`, `preview`, `production`.

- **AC2**: Auth0 native login (PKCE); tokens stored in secure storage; refresh handled.

- **AC3**: API client shares OpenAPI types or hand-written client pointing to same backend as web.

### Screens (MVP subset)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC4**: **Calendar** — month/week view; bookings + iCal blocks (same payload as `GET /api/bookings/calendar`).

- **AC5**: **Booking detail** — guest name, dates, compliance badges, check-in status, service request timeline.

- **AC6**: **Richiedi fornitore** — create `ServiceRequest` from booking (step 7).

- **AC7**: **Mark paid** — host confirms supplier payment (step 10).

- **AC8**: **Quick check-out** — triggers checkout wizard start; shows summary (steps 11–12).

- **AC9**: **Property list** — read-only status + share link to public site (step 3).

### Push notifications

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Expo Notifications + FCM/APNs; register device token `POST /api/devices`.

- **AC11**: Push types: guest check-in incomplete (step 6), service request state change (8–9), checkout reminder.

- **AC12**: Tap notification deep-links to correct `bookingId`.

### Parity & resilience

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: After any action on web, app refresh shows identical booking/service state (within 5s pull-to-refresh).

- **AC14**: Offline: show cached last fetch + Italian error banner; no white screen on API failure.

- **AC15**: 0 crashes during Maestro suite M1–M7.

### Out of app (web only)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: Full property activation wizard, iCal URL paste, custom domain DNS, Stripe Connect onboarding, SaaS billing — deep link to web routes.

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
| AC1 | L2 + L3 | Expo project in `casazen/mobile` (or monorepo package) with EAS build profiles `development`, `preview`, `production`. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC2 | L1 | Auth0 native login (PKCE); tokens stored in secure storage; refresh handled. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | API client shares OpenAPI types or hand-written client pointing to same backend as web. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | **Calendar** — month/week view; bookings + iCal blocks (same payload as `GET /api/bookings/calendar`). | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | **Booking detail** — guest name, dates, compliance badges, check-in status, service request timeline. | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | **Richiedi fornitore** — create `ServiceRequest` from booking (step 7). | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | **Mark paid** — host confirms supplier payment (step 10). | Outcome not met; wrong status; silent no-op |
| AC8 | L2 + L3 | **Quick check-out** — triggers checkout wizard start; shows summary (steps 11–12). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L1 | **Property list** — read-only status + share link to public site (step 3). | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | Expo Notifications + FCM/APNs; register device token `POST /api/devices`. | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | Push types: guest check-in incomplete (step 6), service request state change (8–9), checkout reminder. | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | Tap notification deep-links to correct `bookingId`. | Outcome not met; wrong status; silent no-op |
| AC13 | L1 | After any action on web, app refresh shows identical booking/service state (within 5s pull-to-refresh). | Outcome not met; wrong status; silent no-op |
| AC14 | L2 + L3 | Offline: show cached last fetch + Italian error banner; no white screen on API failure. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | 0 crashes during Maestro suite M1–M7. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | Full property activation wizard, iCal URL paste, custom domain DNS, Stripe Connect onboarding, SaaS billing — deep link to web routes. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `mobile/` (new repo or package) | Create — Expo app |
| `Casazen.Web/Controllers/DevicesController.cs` | Create — push tokens |
| `Casazen.Infrastructure/Services/PushNotificationService.cs` | Create |
| `mobile/e2e/` | Create — Maestro flows M1–M7 |

**Complexity:** L  
**Migration:** yes — `DeviceRegistration` table  
**Dependencies:** `spec-ical-calendar-sync`, `spec-micro-marketplace-v0`, `spec-guest-check-in-portal`

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

- Supplier native app (Fase 2)
- Full feature parity with web (explicitly not required)
- iPad-optimized layouts (responsive phone first)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
