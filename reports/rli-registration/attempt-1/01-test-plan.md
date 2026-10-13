# Test plan — spec-ltr-rli-registration attempt-1

Local/dev only. No production URLs. Openapi.it stays stub.

## L1 (xUnit)

| # | File | Scenario | Pass |
|---|---|---|---|
| T1 | `RliAuthorizationGateTests` | Signed lease, no/false delega | `SubmitRegistrationAsync` never called; throws before provider |
| T2 | `RliAuthorizationGateTests` | Valid tosVersion + attestation | Authorization row saved; `RegistrationAuthorized` then `RegistrationSubmitted`; stub id returned |
| T3 | `RliAuthorizationGateTests` | Wrong TosVersion | 400-equivalent exception; no submit |
| T4 | `LeaseRegistrationStatusPollingJobTests` | Pending registrations | `PollStatusAsync` called; `SubmitRegistrationAsync` never |
| T5 | `CedolareAdvisoryServiceTests` | CedolareSecca vs CanoneConcordato vs Ordinario | Config rates used; disclaimer contains "non consulenza fiscale"; no hardcoded 0.21 in service |
| T6 | `LeaseContractTemplateServiceTests` | Unapproved regime | `GeneratePdfAsync` throws; approved `dev-stub` returns bytes |
| T7 | `RliExportServiceTests` | Owner lease | Body starts `%PDF`; filename has no CF/P.IVA; `RliExported` emitted; does not call registration submit |
| T8 | `RliDeadlineReminderJobTests` | T-15 then T-15 again | One email per milestone; second run no duplicate |
| T9 | `RliDeadlineReminderJobTests` | Extra-EU tenant | Distinct extra-EU reminder; EU-only lease has no extra-EU item |
| T10 | `RliChecklistServiceTests` | Extra-EU vs EU | Checklist includes Questura item iff `HasExtraEUTenant` |

Commands:

```
dotnet test --filter "FullyQualifiedName~RliAuthorizationGateTests|FullyQualifiedName~CedolareAdvisoryServiceTests|FullyQualifiedName~RliDeadlineReminderJobTests|FullyQualifiedName~LeaseRegistrationStatusPollingJobTests|FullyQualifiedName~LeaseContractTemplateServiceTests|FullyQualifiedName~RliExportServiceTests|FullyQualifiedName~RliChecklistServiceTests|FullyQualifiedName~LeaseWorkflowServiceTests"
```

## L2 (frontend unit)

| # | File | Pass |
|---|---|---|
| T11 | `mask-fiscal-code.test.ts` | `RSSMRA80A01H501U` → `************501U` |
| T12 | `delega-capture-dialog.test.tsx` | Submit RLI disabled until attestation checked |

## Actors / seed

- Owner `auth0|owner123`, org-scoped property, signed lease, `FiscalRegime.CedolareSecca`
- Extra-EU variant: tenant citizenship `US`
