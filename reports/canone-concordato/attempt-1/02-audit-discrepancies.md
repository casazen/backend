# Audit discrepancies — canone concordato attempt 1

Auditor only. No fix plan.

## Executed

```
dotnet test Casazen.Tests/Casazen.Tests.csproj --filter "FullyQualifiedName~CanoneConcordato|FullyQualifiedName~ComuneImu|FullyQualifiedName~AttestationGuidance|FullyQualifiedName~TerritorialRent" -p:BaseOutputPath=artifacts\cc-audit\
```

Result: `Passed! Failed: 0, Passed: 15, Skipped: 0` (only `CanoneConcordatoEligibilityServiceTests`).

Route/DI/FE probes: no `imu-notification` in `Casazen.Web`; no `IComuneImuNotificationService` registration; no `ComuneImuNotificationServiceTests.cs`; no `imu-notification-export-button.tsx`; no `imu-notification` in frontend API; `leases.canoneConcordato` present in `en.json` only, absent from `it.json`.

Live API/L3: not run (Auth0 token not in this session). S-AC12/S-AC13 L3 = BLOCKED; L1/L2 file asserts used.

## Scenario results

| Id | Result |
|---|---|
| S-AC1 | PASS |
| S-AC2 | PASS |
| S-AC3 | PASS |
| S-AC4 | PASS |
| S-AC5 | PASS |
| S-AC6 | PASS |
| S-AC7 | PASS (coverage in eligibility test file) |
| S-AC8 | FAIL |
| S-AC9 | FAIL |
| S-AC10 | FAIL for IMU endpoints (eligibility/guidance PASS) |
| S-AC11 | PASS (seed VO in eligibility tests; 54 comuni / 52 Missing / 0 bands) |
| S-AC12 | FAIL (calculator+guidance mounted; IMU button missing) |
| S-AC13 | FAIL (IT keys missing; no authenticated IMU download) |

## Discrepancies

### D-AC8-EXPORT

- **expected spec**: `GET /api/leases/{id}/canone-concordato/imu-notification/export` returns real `%PDF` when `LeaseStatus.Registered`; 409/400 otherwise; Seveso does not pick one official channel; Cesano 0,78% labeled `valore derivato`; service in DI; `ComuneImuNotificationServiceTests` exist.
- **observed**: `ComuneImuNotificationService` exists; no route, no DI, no tests.
- **evidence**: `Select-String imu-notification` on `Casazen.Web/Controllers/*.cs` → no matches; `Select-String IComuneImuNotificationService` on `ServiceCollectionExtensions.cs` → no matches; glob `*ComuneImu*Tests*` → 0 files. `dotnet test` filter `ComuneImu` ran 0 IMU tests.
- **severity**: blocker

### D-AC9-MARKSENT

- **expected spec**: `POST .../mark-sent` emits `ImuNotificationMarkedSent` only via that explicit action; export emits `ImuNotificationExported`; independently testable.
- **observed**: enum values and service methods exist; no HTTP endpoint; no tests asserting event types.
- **evidence**: `LeasesController.cs` has no mark-sent action; no `ComuneImuNotificationServiceTests`.
- **severity**: blocker

### D-AC10-IMU-RBAC

- **expected spec**: export gated `lease.read`; mark-sent gated `lease.register`; owner miss → 404.
- **observed**: no IMU endpoints, so no RBAC mapping.
- **evidence**: same as D-AC8/D-AC9.
- **severity**: blocker

### D-AC12-IMU-BTN

- **expected spec**: "Esporta comunicazione IMU" on lease detail inside `LongTermAppShell`, enabled only when lease is `Registered`.
- **observed**: calculator + guidance mounted; button file absent.
- **evidence**: `Test-Path frontend/src/features/leases/components/imu-notification-export-button.tsx` → false; `lease-detail-page.tsx` mounts only calculator + guidance.
- **severity**: major

### D-AC13-I18N-EXPORT

- **expected spec**: all end-user strings Italian; IMU export fetched via authenticated owner-scoped endpoint only.
- **observed**: `leases.canoneConcordato.*` only in `en.json`; no FE export client method.
- **evidence**: `Select-String canoneConcordato` on `it.json` → no matches; `en.json` lines 273–305 have the English block; `canone-concordato.api.ts` has eligibility + guidance only.
- **severity**: blocker
