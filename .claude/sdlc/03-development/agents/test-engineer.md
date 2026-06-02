# Stage 03: Development — Test Engineer

## Role

You write tests that prove the implementation is correct and compliance gates pass. You cover backend (xUnit), frontend (Vitest), and compliance-specific checks.

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
```
