# Canone Concordato Calculator — Feature Catalog

Cataloger only. No severity, no fix advice.

## 1. Spec list

| Field | Value |
|---|---|
| **id** | — (frontmatter placeholder) |
| **slug** | `spec-ltr-canone-concordato-calculator` |
| **path** | `Sessions/specs/spec-ltr-canone-concordato-calculator.md` |
| **AC ids** | AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC9, AC10, AC11, AC12, AC13 |

## 2. Implemented feature list

### Backend — entities & enums

| Artifact | Path |
|---|---|
| `TerritorialRentAgreement` | `Casazen.Core/Entities/TerritorialRentAgreement.cs` |
| `ConcordatoRentBand` | `Casazen.Core/Entities/ConcordatoRentBand.cs` |
| `TerritorialAgreementSignatory` | `Casazen.Core/Entities/TerritorialAgreementSignatory.cs` |
| `HighTensionAreaComune` | `Casazen.Core/Entities/HighTensionAreaComune.cs` |
| `DataCompleteness` | `Casazen.Core/Entities/Enums/DataCompleteness.cs` |
| `SignatoryRole` | `Casazen.Core/Entities/Enums/SignatoryRole.cs` |
| `LeaseEventType` (+ IMU values) | `Casazen.Core/Entities/Enums/LeaseEventType.cs` |
| `FiscalRegime.CanoneConcordato` (pre-existing) | `Casazen.Core/Entities/Enums/FiscalRegime.cs` |

### Backend — repositories

| Artifact | Path |
|---|---|
| `ITerritorialRentAgreementRepository` | `Casazen.Core/Repositories/ITerritorialRentAgreementRepository.cs` |
| `IHighTensionAreaComuneRepository` | same file |
| `TerritorialRentAgreementRepository` | `Casazen.Infrastructure/Repositories/TerritorialRentAgreementRepository.cs` |
| `HighTensionAreaComuneRepository` | same file |

### Backend — services & DTOs

| Artifact | Path |
|---|---|
| `ICanoneConcordatoEligibilityService`, DTOs, copy | `Casazen.Core/Services/ICanoneConcordatoEligibilityService.cs` |
| `CanoneConcordatoEligibilityService` | `Casazen.Infrastructure/Services/CanoneConcordatoEligibilityService.cs` |
| `IAttestationGuidanceService` | `Casazen.Core/Services/IAttestationGuidanceService.cs` |
| `AttestationGuidanceService` | `Casazen.Infrastructure/Services/AttestationGuidanceService.cs` |
| `IComuneImuNotificationService` | `Casazen.Core/Services/IComuneImuNotificationService.cs` |
| `ComuneImuNotificationService` | `Casazen.Infrastructure/Services/ComuneImuNotificationService.cs` |
| `FiscalPdfWriter` | `Casazen.Infrastructure/Services/FiscalPdfWriter.cs` |

### Backend — data & migration

| Artifact | Path |
|---|---|
| `CanoneConcordatoMbSeed` | `Casazen.Infrastructure/Data/Seeds/CanoneConcordatoMbSeed.cs` |
| `AppDbContext` | `Casazen.Infrastructure/Data/AppDbContext.cs` |
| Migration `AddTerritorialRentAgreements` | `Casazen.Infrastructure/Migrations/20260816203709_AddTerritorialRentAgreements.cs` |

### Backend — endpoints

| Method | Route | Controller |
|---|---|---|
| `GET` | `/api/properties/{propertyId}/canone-concordato/eligibility` | `Casazen.Web/Controllers/CanoneConcordatoController.cs` |
| `GET` | `/api/properties/{propertyId}/canone-concordato/attestation-guidance` | same |

Not found: `GET /api/leases/{id}/canone-concordato/imu-notification/export`, `POST .../mark-sent`.

### Backend — DI

Registered: territorial + ATA repos, eligibility, attestation.

Not registered: `IComuneImuNotificationService`.

### Backend — tests

| Test file | Path |
|---|---|
| `CanoneConcordatoEligibilityServiceTests` | `Casazen.Tests/Unit/Services/CanoneConcordatoEligibilityServiceTests.cs` |

Not found: `AttestationGuidanceServiceTests.cs`, `ComuneImuNotificationServiceTests.cs`, `TerritorialRentAgreementMigrationTests.cs`.

### Frontend

| Artifact | Path |
|---|---|
| API | `frontend/src/api/canone-concordato.api.ts` |
| Queries | `frontend/src/queries/use-canone-concordato.ts` |
| Calculator | `frontend/src/features/leases/components/canone-concordato-calculator.tsx` |
| Guidance | `frontend/src/features/leases/components/attestation-guidance-panel.tsx` |
| Mount | `frontend/src/features/leases/lease-detail-page.tsx` |
| i18n | `frontend/src/i18n/locales/it.json`, `en.json` (`leases.canoneConcordato.*`) |

Not found: `imu-notification-export-button.tsx`; no FE tests matching canone/concordato.

## 3. AC → implementation mapping

| AC | Mapping present |
|---|---|
| AC1 | Entities, migration, seed, grep-style test for no hardcoded rates |
| AC2 | `HighTensionAreaComune` + tests ATA only when `VerifiedDirectly` |
| AC3 | Eligibility service + Seveso/Cesano/fascia tests |
| AC4 | Eligibility endpoint + 404/DTO controller tests |
| AC5 | Missing/no-zone tests; calculator empty state |
| AC6 | DTO fields on eligibility response; no RLI template consumer (sibling spec) |
| AC7 | Attestation service + endpoint + panel; test embedded in eligibility file |
| AC8 | Service + PDF writer exist; no endpoint, DI, tests, UI |
| AC9 | Enum + service emission; no mark-sent HTTP, no tests |
| AC10 | Eligibility/guidance RBAC; IMU endpoints absent |
| AC11 | Seed 54 comuni Partial/Missing; no dedicated migration test file |
| AC12 | Calculator + guidance on lease detail; no IMU button; no L2/L3 |
| AC13 | Italian keys + backend disclaimer; no authenticated IMU download path |

## 4. Explicit gaps (no mapping)

| AC / item | Gap |
|---|---|
| AC6 | No wiring into `LeaseContractTemplateService` (sibling-spec / SPEC-ONLY) |
| AC8 | No IMU export routes; service not in DI; no IMU tests; no FE export |
| AC9 | No mark-sent HTTP; no event-emission tests |
| AC10 | No RBAC mapping for IMU endpoints (endpoints absent) |
| AC11 | No dedicated migration/seed integration test file |
| AC7 tests | No standalone `AttestationGuidanceServiceTests.cs` |
| AC12 | No IMU button; not on property detail; no Playwright/L3 |
| AC13 | No authenticated IMU PDF download in FE |
| Spec path vs code | IMU service in `Services/` not `External/`; ATA repo co-located |
| Slice B | Service + enum + PDF writer; API, DI, tests, UI absent |
