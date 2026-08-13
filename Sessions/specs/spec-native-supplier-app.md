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
last_reviewed: 2026-08-13
---

# Spec — Native Supplier App (US-028)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

**Fase 2** React Native app for suppliers: same APIs as `spec-supplier-console-web`, optimized for inbox, presa in carico, push on new requests. Replaces mobile-web-only F1–F2 tests with native Maestro suite when shipped.

**Phase:** 2 · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a supplier on site, I want push alerts for new host requests and one-tap accept so that I respond faster than competitors.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### App

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Expo app with Auth0 `Supplier` role; shared patterns with host app (secure token, API client).

- **AC2**: Screens: Inbox, Request detail, Profile (read-only), Availability quick toggle.

- **AC3**: Actions: presa in carico, reject, complete — same endpoints as web console.

- **AC4**: Push on new `Richiesto`; deep link to request detail.

### Parity

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC5**: State after actions matches web console and host app within 5s.

### E2E

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC6**: Maestro F1–F2 equivalent on native binary in CI.

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Expo app with Auth0 `Supplier` role; shared patterns with host app (secure token, API client). | Outcome not met; wrong status; silent no-op |
| AC2 | L2 + L3 | Screens: Inbox, Request detail, Profile (read-only), Availability quick toggle. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC3 | L1 | Actions: presa in carico, reject, complete — same endpoints as web console. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | Push on new `Richiesto`; deep link to request detail. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | State after actions matches web console and host app within 5s. | Outcome not met; wrong status; silent no-op |
| AC6 | L2 + L3 | Maestro F1–F2 equivalent on native binary in CI. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `mobile-supplier/` | Create — Expo app or flavor in monorepo |
| Reuse `DevicesController`, `PushNotificationService` | From host app spec |

**Complexity:** M  
**Migration:** no (reuses device table)  
**Dependencies:** `spec-supplier-console-web`, `spec-native-host-app`

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

- MVP delivery (MVP uses responsive web for F1–F2)
- Full profile editing (web preferred)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
