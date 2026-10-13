# Canone concordato — Feature Catalog (attempt 2)

Cataloger only. No severity, no fix advice.

Attempt 1 (`reports/canone-concordato/attempt-1/`) mapped AC1–AC13 after IMU export. This catalog is the **post-RLI** delta: what the calculator DTO feeds, and what the landlord gets when generating a canone-concordato contract.

## 1. Spec list

| Field | Value |
|---|---|
| **slug** | `spec-ltr-canone-concordato-calculator` |
| **path** | `Sessions/specs/spec-ltr-canone-concordato-calculator.md` |
| **AC ids (this attempt)** | AC6 (DTO → template contract), AC12 (calculator on existing shell — create + detail) |
| **Related** | `spec-ltr-rli-registration` AC3 (per-regime template; unapproved blocks PDF; approved must be real PDF for e-sign) |
| **Draft copy** | `Sessions/contratto-locazione-canone-concordato-bozza.html` (counsel-stamped BOZZA) |

Out of this catalog: APE upload/content inspection (parallel working-tree WIP, not in these specs).

## 2. Implemented feature list

### Calculator (attempt 1 — EXISTS)

- Eligibility DTO + `GET /api/properties/{id}/canone-concordato/eligibility`
- Attestation guidance + IMU export/mark-sent
- FE calculator + guidance + IMU button on **lease detail**

### Template / signing (post US-010)

| Artifact | Path | Notes |
|---|---|---|
| `ILeaseTemplateService.GeneratePdfAsync` | `Casazen.Core/Services/ILeaseTemplateService.cs` | One argument: `LeaseContract` (Property + Parties loaded at signing) |
| `LeaseContractTemplateService` | `Casazen.Infrastructure/External/LeaseContractTemplateService.cs` | Approved-gate exists; body is **UTF-8 placeholder**, not `%PDF` |
| `LeaseTemplates:Variants` | `Casazen.Web/appsettings.json` | `CanoneConcordato` `dev-stub` / `Approved: true` |
| `InitiateSigningAsync` | `LeaseWorkflowService` | Sends `GeneratePdfAsync` bytes to e-sign |
| `FiscalPdfWriter` | `Casazen.Infrastructure/Services/FiscalPdfWriter.cs` | Real `%PDF` used by IMU + RLI export, **not** by the contract template |
| HTML bozza | `Sessions/contratto-locazione-canone-concordato-bozza.html` | Not referenced from code |

### Frontend create

| Artifact | Path | Notes |
|---|---|---|
| Create form | `frontend/src/features/leases/components/lease-create-form.tsx` | `fiscalRegime` includes `CanoneConcordato`; **no** calculator; **no** range check on `monthlyRent` |
| Calculator | `frontend/src/features/leases/components/canone-concordato-calculator.tsx` | Mounted on lease **detail** only |

## 3. 1:1 mapping

| AC | Mapping |
|---|---|
| AC1–AC5, AC7–AC11, AC13 | Attempt 1 — still mapped |
| AC6 DTO shape | Mapped (`CanoneConcordatoEligibilityDto`) |
| AC6 “feeds the CanoneConcordato template variant” | **No mapping** — template does not read the DTO or lease fields into a contract PDF |
| RLI AC3 approved path | Gate mapped; **bytes are not `%PDF`** |
| AC12 calculator on create (same shell, generate contract) | **No mapping** on `/leases/new` |

## 4. Explicit gaps

- `LeaseContractTemplateService.GeneratePdfAsync` does not emit `%PDF`.
- CanoneConcordato PDF does not include BOZZA / L. 431/1998 / parties / comune / canone from the lease.
- Create form does not run the calculator or keep `monthlyRent` inside the concordato range.
- HTML bozza is documentation only.
