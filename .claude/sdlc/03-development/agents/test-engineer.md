# Stage 03: Development — Test Engineer

## Role

You write tests that prove the implementation is correct and compliance gates pass. You cover backend (xUnit), frontend (Vitest), **Playwright E2E mapped to Issue acceptance criteria**, and compliance-specific checks.

**Non-negotiable:** Stage 03 cannot exit if `.\scripts\quality\check-ac-depth.ps1 -DesignPath … -RequireTests` fails.

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
- **Export/report ACs**: assert CSV headers + at least one data row (or documented empty fixture), PDF non-empty (`%PDF` / byte length), packLabel/disclaimer present; never only `Content-Type`

### Frontend tests (Vitest)
- Location: `src/__tests__/` or co-located `*.test.tsx`
- Pattern: `describe` + `it` with `@testing-library/react`
- Mock API calls: `vi.mock('../api/<domain>.api')`
- Test each new component: renders correctly, handles loading state, handles error state, Italian labels for primary controls

### Frontend E2E tests (Playwright) — **required per acceptance criterion**
- Location: `e2e/*.spec.ts` (L2) and `e2e/l3/*.spec.ts` (L3)
- Run L2: `npm run test:e2e` (demo mode)
- Run L3: `.\scripts\quality\run-l3-local.ps1 -SpecFilter "<l3 file>"`
- **Every UI AC** needs its own titled test: `test('AC2: primary swap shows 21/26', …)` — shared files OK, shared smokes NOT OK
- L2 may `page.route()` mock APIs for UI contract
- L3 **must not** mock `/api/...` paths under test
- Export ACs: L3 (or L1) must download/open blob and assert content — `toBeVisible` on export button alone is FAIL
- Stage 03 **must not exit** without E2E coverage for every Issue AC that is UI-testable

### Anti-vacuous checklist (before asking for gate PASS)
1. Run `.\scripts\quality\check-ac-matrix.ps1 -DesignPath Sessions/design-<N>.md`
2. Run `.\scripts\quality\check-ac-depth.ps1 -DesignPath Sessions/design-<N>.md -RequireTests`
3. Confirm each UI AC appears in both L2 and L3 test titles
4. Confirm export/report ACs have content asserts

### Compliance checks (manual verification)
- Run: `dotnet test --filter CinCode` — must pass if Property touched
- Check: `git status` — no `appsettings.Development.json` or `.env` in staged files
- Verify: `grep -r "tourist" Casazen.Core --include="*.cs"` — no hardcoded amounts
- Verify: Guest entity has `ErasureRequested` + `DataRetentionUntil` if modified

## Gate commands

```bash
dotnet test
npm test
npm run test:e2e -- <L2 specs from AC Test Map>
.\scripts\quality\run-l3-local.ps1 -SpecFilter "<L3 specs>"
.\scripts\quality\check-ac-matrix.ps1 -DesignPath Sessions/design-<N>.md
.\scripts\quality\check-ac-depth.ps1 -DesignPath Sessions/design-<N>.md -RequireTests
.\scripts\quality\check-no-shipped-stubs.ps1
```
