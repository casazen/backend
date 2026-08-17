# Catalog — spec-ltr-rli-registration (US-010)

Date: 2026-08-17  
Repos: `casazen/backend`, `casazen/frontend`

## 1. Spec list

| Id | Slug | AC ids |
|---|---|---|
| US-010 | `spec-ltr-rli-registration` | AC1–AC12 |
| US-008 (UI contract) | `spec-ltr-frontend` AC10 | assisted RLI panels on lease detail |

## 2. Implemented features

### Backend

| Item | Path |
|---|---|
| `LeaseRegistration` | `Casazen.Core/Entities/LeaseRegistration.cs` |
| `LeaseContract.RegistrationDeadline`, `FiscalRegime`, `HasExtraEUTenant`, `OrgId` | `Casazen.Core/Entities/LeaseContract.cs` |
| `TriggerRegistrationAsync` (no delega) | `Casazen.Infrastructure/Services/LeaseWorkflowService.cs` |
| `POST/GET /api/leases/{id}/registration`, receipt | `Casazen.Web/Controllers/LeasesController.cs` |
| `OpenapiLeaseRegistrationProvider` stub | `Casazen.Infrastructure/External/OpenapiLeaseRegistrationProvider.cs` |
| `LeaseContractTemplateService` stub (no regime variants) | `Casazen.Infrastructure/External/LeaseContractTemplateService.cs` |
| `LeaseRegistrationStatusPollingJob` (poll only) | `Casazen.Web/BackgroundJobs/LeaseRegistrationStatusPollingJob.cs` |
| `FiscalPdfWriter` | `Casazen.Infrastructure/Services/FiscalPdfWriter.cs` |
| `lease.register` RBAC | `LeasesController` |
| Workflow unit tests | `Casazen.Tests/Unit/Services/LeaseWorkflowServiceTests.cs` |

### Frontend

| Item | Path |
|---|---|
| Lease detail | `src/features/leases/lease-detail-page.tsx` |
| `RegistrationStatusPanel` (English, ungated submit) | `src/features/leases/components/registration-status-panel.tsx` |
| `ExtraEUWarningBanner` (English) | `src/features/leases/components/extra-eu-warning-banner.tsx` |
| `leasesApi.triggerRegistration` (no body) | `src/api/leases.api.ts` |
| `useTriggerRegistration` | `src/queries/use-leases.ts` |

## 3. 1:1 mapping

| AC | Mapping |
|---|---|
| AC2 (poll read-only) | Polling job exists; no test that it never submits |
| Partial AC8 | Stub provider exists; no `FilingEnabled` flag |
| Partial AC11 | Registration panel + extra-EU banner exist; no delega/advisory/checklist/export |

## 4. Gaps (no mapping)

| AC | Gap |
|---|---|
| AC1 / AC9 | No `LeaseRegistrationAuthorization`; POST has no body; submit is ungated |
| AC3 | No per-`FiscalRegime` version/approved gate |
| AC4 | No `ICedolareAdvisoryService` / `/rli/advisory` |
| AC5 | No `IRliExportService` / `/rli/export` |
| AC6 | No `RliDeadlineReminderJob`; no `/rli/checklist` |
| AC7 | Extra-EU banner only (FE English); no checklist item / distinct reminder |
| AC10 | Missing `RegistrationAuthorized`, `RliExported`, `DeadlineReminderSent` |
| AC11 | Missing delega dialog, cedolare panel, countdown, checklist, export button |
| AC12 | Parties show email; CF not shown/masked; FE copy not Italian on RLI panels |
| Tests | Missing `CedolareAdvisoryServiceTests`, `RliAuthorizationGateTests`, `RliDeadlineReminderJobTests` |
