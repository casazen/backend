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
last_reviewed: 2026-06-19
---

# Spec — Golden Journey E2E (GJ-001)

## Overview

Defines the **product acceptance harness** for CasaZen MVP: a 12-step end-to-end journey (supplier → host → guest → service → checkout) verified on **web (Playwright)**, **host native app (Maestro/Detox)**, and **supplier mobile web**. Supersedes `spec-production-e2e-flow-verification.md` as the canonical gate.

**Phase:** 1 — MVP · **Type:** ops · **Status:** specced

---

## User Story

As the product team, we want an automated and repeatable test harness for the full MVP journey so that we never ship when core flows are broken in production.

As a developer, I want CI to fail when any Golden Journey step returns 500 or when web/app state diverges.

---

## Acceptance Criteria

### Harness — Web (Playwright)

- **AC1**: `e2e/golden-journey-web.spec.ts` executes steps **1–12** in order on staging (or demo mode with seed) without manual DB edits.
- **AC2**: Step mapping matches `Sessions/PLANNING.md` § Golden Journey (supplier create → activate → host property → guest book → calendar → guest check-in → service loop → checkout).
- **AC3**: Each step asserts HTTP status ≠ 500 on API calls triggered by the UI.
- **AC4**: User-visible error messages are Italian where applicable.
- **AC5**: Run is idempotent-safe via unique test emails/slugs per CI run.

### Harness — App host (Maestro / Detox)

- **AC6**: `e2e/golden-journey-host-app.e2e.ts` (or `.yaml` Maestro) executes suite **M1–M7** against the same backend seed as AC1.
- **AC7**: M1–M2: calendar shows booking + iCal blocks consistent with web run.
- **AC8**: M3: push notification (or injected deep link) opens correct booking when guest check-in incomplete.
- **AC9**: M4: create `ServiceRequest` from booking detail → state `Richiesto`.
- **AC10**: M5–M6: after supplier actions on web, app shows `PresoInCarico` → `Completato` → `Pagato` matching web API.
- **AC11**: M7: quick check-out entry + compliance summary shows no critical red badges.
- **AC12**: 0 app crashes during suite.

### Harness — Supplier mobile web

- **AC13**: `e2e/golden-journey-supplier-mobile.spec.ts` (Playwright mobile viewport) runs **F1–F2**: presa in carico + completato from phone browser; host web/app reflect update within 30s.

### Parity gates

- **AC14**: After steps 7–10, `GET` booking + service request from web and app return identical `status` fields.
- **AC15**: CI workflow runs web suite on every PR; app suite on `main` + nightly (or PR with label `e2e-app`).

### Documentation

- **AC16**: `Sessions/golden-journey-runbook.md` documents manual fallback run (video checklist) when CI infra unavailable.

---

## Technical Notes

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

## Regulatory / Legal Gates

- GJ step 6 must not assert Alloggiati `Inviato` unless test Questura credentials configured (skip or mock in CI).

---

## Out of Scope

- Load/performance testing
- Full OTA API flows (frozen — iCal only)
