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
last_reviewed: 2026-06-19
---

# Spec — Native Host App (US-025)

## Overview

**React Native + Expo** iOS/Android app for hosts — **subset** of web console focused on mobility: calendar, booking detail, service requests, push notifications, quick check-out. Setup-heavy flows (full property wizard, Connect KYC, billing) remain web via deep link/WebView.

Replaces deprecated `spec-pwa-host-shell` strategy.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a host on the go, I want to see today's bookings and respond to guest check-in alerts on my phone so that I do not need a laptop at the property.

As CasaZen, we need web/app **data parity** for Golden Journey steps 5–7, 10–12.

---

## Acceptance Criteria

### App scaffold

- **AC1**: Expo project in `casazen/mobile` (or monorepo package) with EAS build profiles `development`, `preview`, `production`.

- **AC2**: Auth0 native login (PKCE); tokens stored in secure storage; refresh handled.

- **AC3**: API client shares OpenAPI types or hand-written client pointing to same backend as web.

### Screens (MVP subset)

- **AC4**: **Calendar** — month/week view; bookings + iCal blocks (same payload as `GET /api/bookings/calendar`).

- **AC5**: **Booking detail** — guest name, dates, compliance badges, check-in status, service request timeline.

- **AC6**: **Richiedi fornitore** — create `ServiceRequest` from booking (step 7).

- **AC7**: **Mark paid** — host confirms supplier payment (step 10).

- **AC8**: **Quick check-out** — triggers checkout wizard start; shows summary (steps 11–12).

- **AC9**: **Property list** — read-only status + share link to public site (step 3).

### Push notifications

- **AC10**: Expo Notifications + FCM/APNs; register device token `POST /api/devices`.

- **AC11**: Push types: guest check-in incomplete (step 6), service request state change (8–9), checkout reminder.

- **AC12**: Tap notification deep-links to correct `bookingId`.

### Parity & resilience

- **AC13**: After any action on web, app refresh shows identical booking/service state (within 5s pull-to-refresh).

- **AC14**: Offline: show cached last fetch + Italian error banner; no white screen on API failure.

- **AC15**: 0 crashes during Maestro suite M1–M7.

### Out of app (web only)

- **AC16**: Full property activation wizard, iCal URL paste, custom domain DNS, Stripe Connect onboarding, SaaS billing — deep link to web routes.

---

## Technical Notes

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

## Out of Scope

- Supplier native app (Fase 2)
- Full feature parity with web (explicitly not required)
- iPad-optimized layouts (responsive phone first)
