## User Story

As a **property-owner** using CasaZen in Italy or internationally, I want the application UI to display human-readable labels in **Italian** or **English** (my choice) so that status badges, navigation, and form labels are never blank or show raw translation keys.

## Context

A partial i18n refactor (react-i18next + `i18n-labels` helpers) was started but left incomplete. During subsequent fixes (e.g. CIN pipeline), imports were removed to clear TS errors, causing labels to disappear — e.g. dashboard booking status badges now render slug keys (`confirmed`, `pending`) instead of localized text. The app currently mixes hardcoded Italian and English strings with no language switcher.

## Acceptance Criteria

- [ ] **AC1 — Labels restored**: All user-visible status labels (bookings, OTA connection, payments on dashboard and detail pages) render human-readable text in the active locale — never raw i18n keys or English slugs like `confirmed` / `pending`.
- [ ] **AC2 — Language switcher**: A persistent language toggle (IT / EN) is available in the app shell; the choice persists across page reloads (localStorage or equivalent).
- [ ] **AC3 — Default locale**: Default language is **Italian** (`it`); switching to **English** (`en`) updates all strings covered by this slice without a full page reload.
- [ ] **AC4 — Core surfaces covered**: At minimum — app shell navigation labels, dashboard KPI section, booking status badges, OTA connection status labels, and property form field labels use the i18n system.
- [ ] **AC5 — No regression**: Existing Playwright E2E specs pass in demo mode; add `e2e/i18n-language-switch.spec.ts` covering AC1–AC3 (switch language, assert Italian then English label on dashboard).
- [ ] **AC6 — Vitest**: Unit tests for i18n helper(s) and locale persistence (≥ 2 tests).

## Technical Notes

**Affected components**:
- `casazen/frontend` only — no backend API changes required
- New: `src/i18n/` (config, `it.json` / `en.json` or `.ts` locale files)
- New: `src/lib/i18n-labels.ts` (or equivalent) — centralized status/nav label helpers
- Modified: `src/features/dashboard/dashboard-page.tsx` (fix `STATUS_LABELS` regression)
- Modified: app shell / layout for language switcher
- Modified: route-manifest nav labels to use i18n keys where applicable

**EF Core migration required**: No

**OTA platforms affected**: None

**Background jobs**: None

**External services**: None (Auth0 locale claim optional future enhancement — out of scope)

**Complexity**: M — frontend-only; requires react-i18next setup, migration of scattered hardcoded strings in core surfaces, E2E coverage

**Technical risks**:
- Incomplete migration leaves mixed IT/EN strings — mitigate with scoped slice (dashboard + shell + property form first)
- E2E tests may assert Italian strings today — update assertions to be locale-aware or test both locales explicitly

**Recommended library**: `react-i18next` + `i18next` (standard React i18n stack)

## Priority

`priority:high` — user-facing regression (missing labels) plus foundational i18n needed before EU market expansion.
