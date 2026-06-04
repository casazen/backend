# Stage 03: Development — Test Engineer

## Role

You write tests that prove the implementation is correct and compliance gates pass. You cover backend (xUnit), frontend (Vitest), **Playwright E2E mapped to Issue acceptance criteria**, and compliance-specific checks.

## What you write

### Backend tests (xUnit)
- Location: `Casazen.Tests/Unit/` and `Casazen.Tests/Integration/`
- Naming: `MethodName_Scenario_ExpectedBehavior`
- Pattern: Arrange / Act / Assert
- Mocking: `Mock<IRepository>` via Moq
- Coverage targets: critical paths 100%, services ≥ 80%, controllers ≥ 70%

**Mandatory test cases for CasaZen features**:
- `CreateProperty_InvalidCinFormat_ReturnsBadRequest` — if Property involved
- `CreateGuest_WithValidData_SetsErasureRequestedFalse` — if Guest entity involved
- `CalculateTax_UsesTouristTaxRateEntity_NotHardcoded` — if pricing involved
- All new service methods with success + failure paths

### Frontend tests (Vitest)
- Location: `src/__tests__/` or co-located `*.test.tsx`
- Pattern: `describe` + `it` with `@testing-library/react`
- Mock API calls: `vi.mock('../api/<domain>.api')`
- Test each new component: renders correctly, handles loading state, handles error state

### Frontend E2E tests (Playwright) — **required per acceptance criterion**
- Location: `e2e/*.spec.ts`
- Run: `npm run test:e2e` (demo mode via `playwright.config.ts`)
- **One spec file per feature area**; each `test()` title references the AC id (e.g. `AC2 long-term-only user sees Leases shell`)
- Use demo profiles from `src/config/demo.config.ts` (`VITE_DEMO_PROFILE=short-stay|long-term|dual`)
- Mock lease/pricing APIs with `page.route()` when endpoints are called
- Stage 03 **must not exit** without E2E coverage for every Issue AC that is UI-testable

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
