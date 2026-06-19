---
id: US-028
slug: native-supplier-app
title: Expo supplier app — inbox + push
phase: 2
type: feature
priority: P1
status: specced
issue:
depends_on: [supplier-console-web, native-host-app]
blocks: []
exit_contributes_to: Fase 2 ecosystem — supplier on-the-go; extends F1–F2 to native binary
last_reviewed: 2026-06-19
---

# Spec — Native Supplier App (US-028)

## Overview

**Fase 2** React Native app for suppliers: same APIs as `spec-supplier-console-web`, optimized for inbox, presa in carico, push on new requests. Replaces mobile-web-only F1–F2 tests with native Maestro suite when shipped.

**Phase:** 2 · **Type:** feature · **Status:** specced

---

## User Story

As a supplier on site, I want push alerts for new host requests and one-tap accept so that I respond faster than competitors.

---

## Acceptance Criteria

### App

- **AC1**: Expo app with Auth0 `Supplier` role; shared patterns with host app (secure token, API client).

- **AC2**: Screens: Inbox, Request detail, Profile (read-only), Availability quick toggle.

- **AC3**: Actions: presa in carico, reject, complete — same endpoints as web console.

- **AC4**: Push on new `Richiesto`; deep link to request detail.

### Parity

- **AC5**: State after actions matches web console and host app within 5s.

### E2E

- **AC6**: Maestro F1–F2 equivalent on native binary in CI.

---

## Technical Notes

| File | Action |
|---|---|
| `mobile-supplier/` | Create — Expo app or flavor in monorepo |
| Reuse `DevicesController`, `PushNotificationService` | From host app spec |

**Complexity:** M  
**Migration:** no (reuses device table)  
**Dependencies:** `spec-supplier-console-web`, `spec-native-host-app`

---

## Out of Scope

- MVP delivery (MVP uses responsive web for F1–F2)
- Full profile editing (web preferred)
