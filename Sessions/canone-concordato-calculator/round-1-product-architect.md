## Product Architect — Round 1 Response

**Vote**: OBJECT

**Reasoning**:
EXISTS vs NEW is now accurate against the codebase, and AC6/AC8/AC9 text honestly treats RLI work as spec-only. The increment is still not APPROVE-ready: AC3 is not implementable/falsifiable as written, AC4/AC10 cite a `long-rent` permission that does not exist, AC7 has no data model, AC6’s verifiable outcome is circular, and a Large slice (calculator + first real PDF + checklist events + 55-comune seed + FE) has no split note.

**Details**:

**EXISTS vs NEW (now mostly correct)**
Verified in code: `FiscalRegime.CanoneConcordato`; `Property.City`; `TouristTaxRate` city-scoped tables; `ILeaseTemplateService` / `LeaseContractTemplateService.GeneratePdfAsync` is a UTF-8 placeholder, not `%PDF`, no per-regime variant; `GetVerifiedLeaseAsync` is a private owner-scope helper; `LeaseEventType` has no IMU values; `LeaseStatus.Registered` exists; `long-rent` RBAC scaffolding exists (`lease.read/create/sign/register`, `rent.read/manage`). Grep: `ICedolareAdvisoryService`, `TerritorialRentAgreement`, `ConcordatoRentBand`, `HighTensionAreaComune` — zero `.cs` files. SPEC-ONLY vs NEW split matches that.

**Still false vs code**
- **AC4 / AC10**: `RequireContext:long-rent:property.read` does **not** exist. `property.read` is only on `short-rent`. Either gate eligibility/guidance on an existing `long-rent` permission (e.g. `lease.read`) or add `long-rent:property.read` as **NEW**.

**AC6 / AC8 / AC9 dependency honesty**
- **AC6 text**: honest.
- **AC6 VO**: not falsifiable. Pin the DTO contract in *this* spec (`comune`, `zone`, `subFascia`, `canoneMin/Max` annuo/mensile).
- **AC8**: Technical Notes must **not** say IMU PDF “reuses the PDF approach” of `LeaseContractTemplateService` — that approach is UTF-8 stub. AC8 is **NEW** real `%PDF` generation.
- **AC9**: keep events independently testable even if the checklist UI is absent.

**AC7 data-model gap**
Add `TerritorialAgreementSignatory` (or equivalent) to AC1 + NEW + migration seed for Seveso/Cesano Maderno.

**AC3 not observable**
1. Inputs are booleans but the rule needs counts (`≥3 B`, `≥3 C`, `≥2` counted D).
2. VO copies research §4 (2 type-B elements) while AC3 says sub-fascia 2 = all A + **≥3 B**. Two pass conditions.
3. Cesano Maderno has two zones. Add zone (or foglio) to `RentBandCharacteristics`.

**Spec size — needs a split note**
- **Slice A**: AC1–5, AC7, AC10–13 (reference data + eligibility + attestation + FE calculator/empty state). No PDF engine.
- **Slice B**: AC8–9 (real IMU `%PDF` + `LeaseEventType` IMU pair), after RLI can put a lease in `Registered`.

**Residual (not OBJECT blockers once the above are fixed)**
`Property.City` free-string vs `Comune` matching; `Partial` unused if AC5 treats only `Complete` as available; Open Question on splitting `FiscalRegime.CanoneConcordato`; Compliance wording “existing `ICedolareAdvisoryService`” should say SPEC-ONLY pattern. Phase stays frozen.
