---
id: GJ-001
slug: golden-journey-e2e
title: Golden Journey E2E — web 12-step + app host + fornitore mobile
phase: 1
type: ops
priority: P0
status: specced
issue:
depends_on: [ical-calendar-sync, compliance-wizards, guest-check-in-portal, micro-marketplace-v0, supplier-console-web, public-site-design-system, native-host-app]
blocks: []
exit_contributes_to: MVP exit — product acceptance when all GJ gates pass
last_reviewed: 2026-08-13
---

# Spec — Golden Journey E2E (GJ-001)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Defines the **product acceptance harness** for CasaZen MVP: a 12-step end-to-end journey (supplier → host → guest → service → checkout) verified on **web (Playwright)**, **host native app (Maestro/Detox)**, and **supplier mobile web**. Supersedes `spec-production-e2e-flow-verification.md` as the canonical gate.

**Phase:** 1 — MVP · **Type:** ops · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As the product team, we want an automated and repeatable test harness for the full MVP journey so that we never ship when core flows are broken in production.

As a developer, I want CI to fail when any Golden Journey step returns 500 or when web/app state diverges.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Harness — Web (Playwright)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `e2e/golden-journey-web.spec.ts` executes steps **1–12** in order on staging (or demo mode with seed) without manual DB edits.
- **AC2**: Step mapping matches `Sessions/PLANNING.md` § Golden Journey (supplier create → activate → host property → guest book → calendar → guest check-in → service loop → checkout).
- **AC3**: Each step asserts HTTP status ≠ 500 on API calls triggered by the UI.
- **AC4**: User-visible error messages are Italian where applicable.
- **AC5**: Run is idempotent-safe via unique test emails/slugs per CI run.

### Harness — App host (Maestro / Detox)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC6**: `e2e/golden-journey-host-app.e2e.ts` (or `.yaml` Maestro) executes suite **M1–M7** against the same backend seed as AC1.
- **AC7**: M1–M2: calendar shows booking + iCal blocks consistent with web run.
- **AC8**: M3: push notification (or injected deep link) opens correct booking when guest check-in incomplete.
- **AC9**: M4: create `ServiceRequest` from booking detail → state `Richiesto`.
- **AC10**: M5–M6: after supplier actions on web, app shows `PresoInCarico` → `Completato` → `Pagato` matching web API.
- **AC11**: M7: quick check-out entry + compliance summary shows no critical red badges.
- **AC12**: 0 app crashes during suite.

### Harness — Supplier mobile web

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: `e2e/golden-journey-supplier-mobile.spec.ts` (Playwright mobile viewport) runs **F1–F2**: presa in carico + completato from phone browser; host web/app reflect update within 30s.

### Parity gates

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC14**: After steps 7–10, `GET` booking + service request from web and app return identical `status` fields.
- **AC15**: CI workflow runs web suite on every PR; app suite on `main` + nightly (or PR with label `e2e-app`).

### Documentation

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: `Sessions/golden-journey-runbook.md` documents manual fallback run (video checklist) when CI infra unavailable.

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
| AC1 | L2 + L3 | `e2e/golden-journey-web.spec.ts` executes steps **1–12** in order on staging (or demo mode with seed) without manual DB edits. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC2 | L2 + L3 | Step mapping matches `Sessions/PLANNING.md` § Golden Journey (supplier create → activate → host property → guest book → calendar → guest ... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC3 | L2 + L3 | Each step asserts HTTP status ≠ 500 on API calls triggered by the UI. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC4 | L2 + L3 | User-visible error messages are Italian where applicable. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC5 | L1 | Run is idempotent-safe via unique test emails/slugs per CI run. | Outcome not met; wrong status; silent no-op |
| AC6 | L2 + L3 | `e2e/golden-journey-host-app.e2e.ts` (or `.yaml` Maestro) executes suite **M1–M7** against the same backend seed as AC1. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L1 | M1–M2: calendar shows booking + iCal blocks consistent with web run. | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | M3: push notification (or injected deep link) opens correct booking when guest check-in incomplete. | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | M4: create `ServiceRequest` from booking detail → state `Richiesto`. | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | M5–M6: after supplier actions on web, app shows `PresoInCarico` → `Completato` → `Pagato` matching web API. | Outcome not met; wrong status; silent no-op |
| AC11 | L2 + L3 | M7: quick check-out entry + compliance summary shows no critical red badges. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | 0 app crashes during suite. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | `e2e/golden-journey-supplier-mobile.spec.ts` (Playwright mobile viewport) runs **F1–F2**: presa in carico + completato from phone browser... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L1 | After steps 7–10, `GET` booking + service request from web and app return identical `status` fields. | Outcome not met; wrong status; silent no-op |
| AC15 | L2 + L3 | CI workflow runs web suite on every PR; app suite on `main` + nightly (or PR with label `e2e-app`). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | `Sessions/golden-journey-runbook.md` documents manual fallback run (video checklist) when CI infra unavailable. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `frontend/e2e/golden-journey-web.spec.ts` | Create — 12-step Playwright |
| `frontend/e2e/golden-journey-supplier-mobile.spec.ts` | Create — F1–F2 mobile viewport |
| `mobile/e2e/golden-journey-host-app.e2e.ts` | Create — M1–M7 (Expo repo) |
| `.github/workflows/e2e-golden-journey.yml` | Create — orchestrate suites |
| `Sessions/golden-journey-runbook.md` | Create — manual checklist |

**Complexity:** L  
**Migration:** no  
**Dependencies:** all Phase 1 MVP feature specs

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

- GJ step 6 must not assert Alloggiati `Inviato` unless test Questura credentials configured (skip or mock in CI).

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- Load/performance testing
- Full OTA API flows (frozen — iCal only)

## Open Questions

- None (or list with owner/date before Stage 03)
