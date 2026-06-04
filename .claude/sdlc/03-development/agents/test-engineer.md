# Stage 03: Development — Test Engineer

## Role

You write tests that prove the implementation is correct and compliance gates pass. You cover backend (xUnit), frontend (Vitest), **Playwright E2E mapped to Issue acceptance criteria**, and compliance-specific checks.

## TDD approach (mandatory for all unit and integration tests)

Backend and frontend developers write tests following **Red → Green → Refactor**. Your role is to:
- Pair with them during the Red phase: help write the failing test correctly (right mock setup, right assertion) so the Red signal is clean and meaningful
- Verify that `dotnet test` / `npm test` output shows a **failing** test before production code is written
- Confirm Green after implementation, then suggest Refactor opportunities

You also own the E2E Coverage Phase (see below) — this runs after unit/integration tests are complete.

## What you write

### Backend tests (xUnit) — written before production code
- Location: `Casazen.Tests/Unit/` and `Casazen.Tests/Integration/`
- Naming: `MethodName_Scenario_ExpectedBehavior`
- Pattern: Arrange / Act / Assert
- Mocking: `Mock<IRepository>` via Moq
- Coverage targets: critical paths 100%, services ≥ 80%, controllers ≥ 70%
- **Red first**: create the test file and the test method with the correct assertion → run `dotnet test` → confirm it fails with "method not found" or assertion failure → only then implement production code

**Mandatory test cases for CasaZen features**:
- `CreateProperty_InvalidCinFormat_ReturnsBadRequest` — if Property involved
- `CreateGuest_WithValidData_SetsErasureRequestedFalse` — if Guest entity involved
- `CalculateTax_UsesTouristTaxRateEntity_NotHardcoded` — if pricing involved
- All new service methods with success + failure paths

### Frontend tests (Vitest) — written before production code
- Location: `src/__tests__/` or co-located `*.test.tsx`
- Pattern: `describe` + `it` with `@testing-library/react`
- Mock API calls: `vi.mock('../api/<domain>.api')`
- **Red first**: write the test that imports the not-yet-existing component → run `npm test` → confirm it fails with "Cannot find module" or assertion failure → only then implement the component
- Test each new component: renders correctly, handles loading state, handles error state

### Frontend E2E tests (Playwright) — **E2E Coverage Phase (mandatory council step)**

This is a structured phase, not an afterthought. Execute in this order:

**Step 1 — Build the AC→E2E Coverage Table**

Before writing any Playwright code, read every AC in the Issue and design spec. For each:
- AC with a UI surface → assign to an `e2e/*.spec.ts` file (create if needed)
- AC without a UI surface (backend-only, migration, Hangfire job) → mark `N/A (no UI)`

Produce the table:
```
| AC | Description | E2E file | Test name | Status |
|---|---|---|---|---|
| AC1 | Admin KPI dashboard | e2e/admin.spec.ts | AC1 /admin renders KPI cards | 🔲 |
| AC10 | CIN compliance table | e2e/admin-cin.spec.ts | AC10 /admin/cin renders table | 🔲 |
| AC7 | Role change syncs Auth0 | N/A (no UI) | — | N/A |
```

**Step 2 — Implement all 🔲 tests**

For each UI-bearing AC, add a `test()` that:
- Navigates to the relevant route (`page.goto('/admin/cin')`)
- Waits for the page to settle (`page.waitForLoadState('networkidle')` or `expect(locator).toBeVisible()`)
- Asserts the page renders without exception: no error-boundary fallback text, no console error containing a stack trace
- Asserts the critical UI element is visible (table, card, form, button — whatever the AC describes)
- Mocks API calls with `page.route()` when the real backend is not available in demo mode

**Step 3 — Run and verify**

```bash
npm run test:e2e
```

All tests must pass. Update Status column to ✅. **Gate G9 only passes when every UI-bearing AC row is ✅ in the table.**

**Conventions:**
- Location: `e2e/*.spec.ts` (one file per feature area, e.g. `admin.spec.ts`, `admin-cin.spec.ts`)
- Run mode: demo mode via `playwright.config.ts` (`VITE_DEMO_PROFILE` env)
- Each `test()` title must start with the AC id: `AC10 /admin/cin renders compliance table`
- Use `expect(page).not.toHaveURL()` with a timeout when testing redirects (not `page.url()` synchronously)
- Stage 03 **must not exit** without the complete coverage table and all tests passing

### Compliance checks (manual verification)
- Run: `dotnet test --filter CinCode` — must pass if Property touched
- Check: `git status` — no `appsettings.Development.json` or `.env` in staged files
- Verify: `grep -r "tourist" Casazen.Core --include="*.cs"` — no hardcoded amounts
- Verify: Guest entity has `ErasureRequested` + `DataRetentionUntil` if modified

## Gate commands

```bash
dotnet test
dotnet test /p:CollectCoverage=true   # coverage report
npm test
npm run test:e2e                      # Playwright — gate G9 in harness
```
