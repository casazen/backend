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
last_reviewed: 2026-06-19
---

# Spec — Guest Check-In Portal (US-020)

## Overview

When a booking is **Confirmed** (direct or OTA/iCal), the system emails the guest a secure link to complete check-in data (identity, GDPR consent). On completion, **Alloggiati Web** is enqueued automatically. Host receives alerts if data is missing before arrival.

**Phase:** 1 — MVP · **Type:** compliance · **Status:** specced

---

## User Story

As a guest, I want to fill in my details online before arrival so that check-in is fast and I do not need to install an app.

As a host, I want Alloggiati filed automatically when the guest completes the form, and to be notified if they do not.

---

## Acceptance Criteria

### Backend

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

- **AC10**: Mobile-first `/check-in/:token` page: Italian copy, document fields, consent checkboxes, progress indicator.

- **AC11**: Success screen confirms data received; no Auth0.

### Frontend — host web + app

- **AC12**: Booking detail shows check-in session status badge + "Invia sollecito" button.

- **AC13**: App push (AC6) deep-links to booking detail.

### Regression

- **AC14**: Manual `POST /api/bookings/{id}/check-in` still works for host-entered data (fallback).

---

## Technical Notes

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

## Regulatory / Legal Gates

- Guest data minimization (GDPR Art. 5); retention via existing `Guest` fields
- Alloggiati timing: within 24h of arrival — job scheduling must respect check-in date

---

## Out of Scope

- SMS gateway (email only MVP; SMS Should)
- Guest mobile app
