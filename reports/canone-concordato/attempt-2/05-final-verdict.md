STATO: GOAL_RAGGIUNTO

Verifier input: `01-test-plan.md`. Discrepancy list: none blocking.

## L1 (xUnit)

```
dotnet test --filter "FullyQualifiedName~LeaseContractTemplateServiceTests|FullyQualifiedName~ComuneImuNotificationServiceTests|FullyQualifiedName~RliExportServiceTests"
```

`Passed! Failed: 0, Passed: 19, Skipped: 0`

| # | Result |
|---|---|
| T1 Unapproved throws | PASS |
| T2 CanoneConcordato `%PDF` | PASS |
| T3 BOZZA / 431/1998 / comune / rent / version | PASS |
| T4 CedolareSecca `%PDF` not UTF-8 stub | PASS |
| T5 Missing property/parties still `%PDF` | PASS |
| IMU / RLI `%PDF` regression | PASS |

## L2 (Vitest)

```
npx vitest run src/features/leases/lib/__tests__/concordato-rent-range.test.ts src/features/leases/components/__tests__/lease-create-form.test.tsx
```

`Test Files  2 passed | Tests  4 passed`

| # | Result |
|---|---|
| T6 Calculator on create when CanoneConcordato | PASS |
| T7 Missing range rejected | PASS (`isRentInConcordatoRange`) |
| T8 Rent inside min/max accepted | PASS (`isRentInConcordatoRange`) |

## Out of scope (not BLOCKED)

- Counsel-final contract wording (PDF stamped BOZZA)
- Persisting zone/sub-fascia on `LeaseContract`
- APE PDF content inspection (parallel WIP, not this spec)
- L3 Auth0 browser
- Commits/PRs
