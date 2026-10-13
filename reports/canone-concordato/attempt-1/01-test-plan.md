# Test plan — canone concordato attempt 1

Environment: local only. No production URLs. No InMemory as the path under test for seed/migration (L1 unit tests may use InMemory for service logic).

| Piece | Value |
|---|---|
| Backend | `C:\Users\luca.la-malfa\private-project\casazen\backend` |
| Frontend | `C:\Users\luca.la-malfa\private-project\casazen\frontend` |
| API | `http://localhost:5000` |
| FE | `http://localhost:5173` |
| Postgres | `casazen_dev` :5432 `postgres`/`dev` |

Shared seed: L1 tests use `CanoneConcordatoMbSeed` + in-memory property `Seveso` / `Cesano Maderno` / `Monza`. Live API/UI scenarios need an owned Seveso property + Auth0 token; if no token, those scenarios are **BLOCKED** and L1/file asserts remain the pass gate.

## Commands

```powershell
cd C:\Users\luca.la-malfa\private-project\casazen\backend
$out = "$pwd\artifacts\cc-audit\"
dotnet test Casazen.Tests/Casazen.Tests.csproj --filter "FullyQualifiedName~CanoneConcordato|FullyQualifiedName~ComuneImu|FullyQualifiedName~AttestationGuidance|FullyQualifiedName~TerritorialRent" -p:BaseOutputPath=$out --no-restore
```

File/route probes (auditor records exit + output):

```powershell
# S-AC8/9 routes
Select-String -Path Casazen.Web\Controllers\*.cs -Pattern "imu-notification"
# S-AC8 DI
Select-String -Path Casazen.Web\Extensions\ServiceCollectionExtensions.cs -Pattern "IComuneImuNotificationService"
# S-AC12/13 FE
Test-Path ..\frontend\src\features\leases\components\imu-notification-export-button.tsx
Select-String -Path ..\frontend\src\api\canone-concordato.api.ts -Pattern "imu-notification"
```

Live API (only if `GET http://localhost:5000/api/health` is 200 **and** a bearer token is available). Unique slug: `cc-{yyyyMMddHHmmss}`.

```powershell
# unauthenticated must be 401
Invoke-WebRequest http://localhost:5000/api/properties/00000000-0000-0000-0000-000000000001/canone-concordato/eligibility?sqm=65&typeACount=2&typeBCount=3&typeCCount=0&typeDCount=0&furnished=false&years=3
```

## Scenarios

| Id | AC | Layer | Command / actor | Pass |
|---|---|---|---|---|
| S-AC1 | AC1 | L1 | `dotnet test` filter `EligibilityService_HasNoHardcodedCanoneLiterals` + `MbSeed_MissingComuni_HaveNoBands`; files `TerritorialRentAgreement.cs`, `ConcordatoRentBand.cs`, `TerritorialAgreementSignatory.cs`, migration `AddTerritorialRentAgreements` exist | Tests pass; 54 comuni; no `3445`/`5525` in eligibility service |
| S-AC2 | AC2 | L1 | `Calculate_AtaApplies_OnlyWhenVerifiedDirectly`, `Calculate_DoesNotTreatAgreementCoverageAsAta`, `MbSeed_AtaCandidates_AreUnverified` | All pass; unverified seed ⇒ `AtaApplies=false` |
| S-AC3 | AC3 | L1 | `Calculate_Seveso_AllA_AtLeast3B_SubFascia2_FromSeededBand`, `Calculate_TwoTypeB_IsSubFascia1_NotFascia2` | Fascia 2 = 3445–5525; two type-B = fascia 1 1300–3380; disclaimer contains `informativa` |
| S-AC4 | AC4 | L1 | `Controller_Eligibility_OtherOwner_Returns404`, `Controller_Eligibility_OwnedProperty_ReturnsDtoShape`; route on `CanoneConcordatoController` | 404 other owner; DTO shape; policy `lease.read` |
| S-AC5 | AC5 | L1 | `Calculate_MissingComune_NoNumericRange`, `Calculate_CesanoWithoutZone_NoBlendedRange` | `Available=false`; no numeric range |
| S-AC6 | AC6 | L1 | `Controller_Eligibility_OwnedProperty_ReturnsDtoShape` field asserts | Fields: comune, zone, subFascia, canone min/max annuo/mensile, dataCompleteness, imuAppliesTheoretical, ataApplies, attestationRequired, disclaimer |
| S-AC7 | AC7 | L1 | `Attestation_ReturnsSignatories_WithoutHttpClient`; `GET .../attestation-guidance` exists | ≥1 signatory name+contact; no HttpClient ctor |
| S-AC8 | AC8 | L1 | File `ComuneImuNotificationServiceTests.cs` exists and passes; `LeasesController` has `GET .../imu-notification/export`; DI registers service; PDF starts `%PDF` | Registered → `%PDF`; not Registered → 409/400; Seveso body does not pick one official channel; Cesano 0,78% labeled `valore derivato` |
| S-AC9 | AC9 | L1 | Tests assert `ImuNotificationExported` on export and `ImuNotificationMarkedSent` only via mark-sent; `POST .../mark-sent` exists | Events only from those actions |
| S-AC10 | AC10 | L1 | Eligibility/guidance/export/mark-sent policies are existing `lease.read` / `lease.register`; owner miss → 404 | No `property.read`; no client `OrgId` |
| S-AC11 | AC11 | L1 | `MbSeed_MissingComuni_HaveNoBands`; dedicated migration/seed test file exists **or** `MigrationTests` covers seed | 52 Missing with 0 bands; 54 comuni |
| S-AC12 | AC12 | L2 | Files: calculator, guidance, mount on `lease-detail-page.tsx`; IMU button file exists and gated on `Registered`; IT labels in `it.json` | Calculator+guidance mounted; export button disabled/hidden unless `Registered`; empty state key `unavailable` |
| S-AC13 | AC13 | L2 | `it.json` `leases.canoneConcordato.*`; FE export uses authenticated API client (no raw `<a href>` to unauthenticated URL) | Italian primary copy; export via `canoneConcordatoApi` |

## Auth0 / live UI

S-AC12/S-AC13 L3 (browser) require Auth0. If token/FE login is unavailable, mark **BLOCKED** and keep L1/L2 file+unit asserts. Do not hit production.

## Out of scope this plan

- Wiring AC6 DTO into `LeaseContractTemplateService` (sibling spec, SPEC-ONLY)
- Extra-EU Questura checklist (`spec-ltr-rli-registration` AC7)
