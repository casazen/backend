# Pipeline: SaaS Subscription Billing (US-005)

## Status
- status: running
- current_stage: 03-development
- last_updated: 2026-06-11T21:30:00Z

## Artifacts
- issue: #230 — https://github.com/casazen/backend/issues/230
- branch: feature/230-saas-billing
- design_spec: Sessions/specs/spec-saas-billing.md
- pr_backend: (pending)
- pr_frontend: (pending)

## Stage History
| Stage | Status | Notes |
|---|---|---|
| 01-planning | completed | Issue #230 |
| 02-design | completed | spec at Sessions/specs/spec-saas-billing.md |
| 03-development | running | Build clean, 538/553 tests pass (15 pre-existing failures unrelated to this feature) |
| 04-review | pending | |
| 05-release | pending | |
| 06-operations | pending | |

## Implementation Summary (Stage 03 — completed work)

### Fixed compile errors
- `RentSchedule.cs`: added `Org` + `LedgerEntries` navigation properties
- `RentLedgerEntry.cs`: added `LeaseContract` + `Org` navigation properties
- `LeaseContract.cs`: added `RentSchedule?` navigation property
- `StripeWebhookHandler.cs`: `Invoice.SubscriptionId` → `invoice.Parent?.SubscriptionDetails?.SubscriptionId`
- `BillingController.cs`: added missing `using Casazen.Web.Infrastructure`
- `UsersController.cs`: added missing `using Casazen.Core.Models`
- `FakeStripeBillingService.cs`: replaced `Org` with `OrgEntity` alias (test project global alias)

### New entities
- `Casazen.Core/Entities/Enums/ConsentType.cs` — Tos, Privacy, Dpa, SubprocessorsAck
- `Casazen.Core/Entities/ConsentRecord.cs` — user consent audit trail

### New models
- `ConsentValidationErrorType` + `ConsentValidationError` added to `OnboardingModels.cs`

### New services
- `Casazen.Core/Services/IOnboardingService.cs` — ValidateAndRecordConsentsAsync, GetActivationStatusAsync
- `Casazen.Core/Services/ILegalDocumentService.cs` — GetTos, GetPrivacy, GetDpa, GetSubprocessors
- `Casazen.Infrastructure/Services/OnboardingService.cs` — implementation (reads legal doc versions from config)
- `Casazen.Infrastructure/Services/LegalDocumentService.cs` — implementation (reads from IConfiguration)

### DI registration
- `ILegalDocumentService → LegalDocumentService` (singleton)
- `IOnboardingService → OnboardingService` (scoped)

### Config
- `appsettings.json`: added `Legal:Documents` section with versions + subprocessors list

### Migration
- `AddConsentRecords` migration created for `ConsentRecords` table

### Tests updated
- `TenantBoundaryIntegrationTests.Onboarding_WithProPlan`: added required consents payload

## Remaining (not in scope for #230)
- SDI real transmission (stub — [COUNSEL_REQUIRED])
- VIES real validation (stub — awaiting legal sign-off)
- Frontend AC10-AC13 (frontend repo, separate PR)
- Rent billing (NullRentBillingService — deferred to #269)
