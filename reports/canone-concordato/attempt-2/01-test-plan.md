# Test plan — canone concordato attempt 2 (contract PDF)

Executable against local/dev. No production URLs.

## Scope

Close catalog gaps: real `%PDF` contract for `FiscalRegime.CanoneConcordato` (AC6 feed + RLI AC3 approved path) and calculator on lease create so monthly rent stays in range (AC12 create path).

APE content inspection is **out of scope** (parallel WIP).

## L1 — backend

```
dotnet test Casazen.Tests/Casazen.Tests.csproj --filter "FullyQualifiedName~LeaseContractTemplateServiceTests"
```

| # | Test | Pass |
|---|---|---|
| T1 | Unapproved regime → `InvalidOperationException` (unchanged) | throw before any bytes |
| T2 | Approved `CanoneConcordato` → bytes start with `%PDF` | not UTF-8 placeholder |
| T3 | PDF body contains `BOZZA`, `431/1998`, comune, monthly rent, template version | ASCII after FiscalPdfWriter sanitize |
| T4 | Approved `CedolareSecca` → `%PDF` (e-sign must not receive UTF-8 stub) | header `%PDF` |
| T5 | Missing Property/Parties still returns `%PDF` with BOZZA (no throw) | signing seam |

Also keep existing IMU/RLI `%PDF` tests green (FiscalPdfWriter shared).

## L2 — frontend

```
cd ../frontend
npx vitest run src/features/leases/components/__tests__/lease-create-form.test.tsx src/features/leases/schemas/__tests__/lease.schema.test.tsx
```

| # | Test | Pass |
|---|---|---|
| T6 | `fiscalRegime === CanoneConcordato` shows calculator | Italian title visible |
| T7 | Submit CanoneConcordato without an available range is blocked | no `onSubmit` |
| T8 | Submit with rent inside calculated monthly min/max calls `onSubmit` | one call |

## Out of scope

- Counsel-final contract wording (stamp BOZZA)
- Persisting zone/sub-fascia on `LeaseContract` (PDF uses lease + property fields available at signing)
- L3 Auth0 browser
- APE inspector
