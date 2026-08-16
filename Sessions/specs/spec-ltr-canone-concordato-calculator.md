---
slug: spec-ltr-canone-concordato-calculator
title: Canone concordato eligibility calculator & assisted IMU notification
phase: 1.5
type: feature
priority: —
status: frozen
issue:
depends_on: [spec-ltr-rli-registration, spec-ltr-frontend]
blocks: []
exit_contributes_to: LTR GA — a long-rent landlord can determine canone concordato eligibility for a property's comune, feed the result into the existing counsel-reviewed contract template, and export an assisted (non-automatic) comune IMU-reduction notification
last_reviewed: 2026-08-16
---

# Spec — Canone Concordato Eligibility Calculator & Assisted IMU Notification

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

Research (`Sessions/research-canone-concordato-mb.md`, `Sessions/business-analysis-canone-concordato.md`) confirms canone concordato is a real, currently-unclaimed fiscal opportunity for CasaZen's long-rent landlords: IMU −25% applies **nationally** with no restriction, and cedolare secca 10% / IRPEF −30% / registro −30% apply in "alta tensione abitativa" comuni — Seveso and Cesano Maderno both qualify, pending direct verification of the underlying 2003 CIPE list. Today a landlord must navigate four disconnected channels (territorial agreement tables, a category association for the attestazione di conformità, the Agenzia delle Entrate RLI portal, and a separate comune-level IMU communication) with no tooling support.

This spec adds the **data and calculation layer** the codebase is missing: a `TerritorialRentAgreement` / `ConcordatoRentBand` reference-data model (same pattern as `TouristTaxRate` — city-scoped rates, never hardcoded), an eligibility/rent-range calculator, attestation-guidance surfacing, and a comune-specific IMU-notification export. It explicitly **reuses** the existing `LeaseContract.FiscalRegime.CanoneConcordato` value and **builds on** `spec-ltr-rli-registration` (counsel-reviewed template variants, the assisted/delega RLI framework, the extra-EU Questura checklist item) rather than re-specifying any of it. It is **new data + calculation**, not a rebuild of the lease workflow.

**Done means**: a long-rent landlord with a property in Seveso or Cesano Maderno can enter the property's characteristics, see a canone range with a clear split between the universal IMU benefit and the ATA-conditional benefits (never a guessed number when data is incomplete), have that range feed the existing contract-template variant, see which local associations can issue the attestazione di conformità, and — once the lease is RLI-registered — export a comune-specific IMU-notification package to send themselves. CasaZen never files anything automatically and never issues the attestation itself.

**Governance note**: the entire "Phase 1.5 — LTR" roadmap phase is marked `frozen` in `Sessions/specs/README.md` (issue #269, "closed — do not resume"). This spec is prepared as ready-to-build documentation for when that freeze lifts; its `status: frozen` frontmatter is intentional, not an oversight, and mirrors the four sibling `spec-ltr-*` files.

**Phase:** 1.5 — LTR Complete + Verify (frozen) · **Type:** feature · **Status:** frozen · **Issue:** none yet

Design: not yet started (blocked by phase freeze). ADRs: none.

### What EXISTS vs what is NEW

| | Item |
|---|---|
| **EXISTS** | `LeaseContract.FiscalRegime` enum incl. `CanoneConcordato` (`Casazen.Core/Entities/Enums/FiscalRegime.cs`); `Property.City` (comune, used by `TouristTaxRate` today — same pattern to reuse); `ILeaseTemplateService` / `LeaseContractTemplateService.GeneratePdfAsync` (`Casazen.Infrastructure/External/`); the counsel-reviewed template-variant-per-`FiscalRegime` mechanism specced in `spec-ltr-rli-registration` AC3; `ICedolareAdvisoryService` (non-binding disclaimer pattern, same spec AC4); the assisted/delega RLI flow, 30-day checklist, and extra-EU Questura checklist item (`spec-ltr-rli-registration` AC1, AC6, AC7); `TouristTaxRate` as the precedent for city-scoped, never-hardcoded rate tables (`.claude/rules/gotchas.md`) |
| **NEW** | `TerritorialRentAgreement` + `ConcordatoRentBand` entities and migration seed (Monza e Brianza, full data for Seveso/Cesano Maderno, honest `Missing` rows for the other 53 comuni); `HighTensionAreaComune` lookup (nationally-scoped ATA list, kept structurally separate from territorial-agreement coverage); `ICanoneConcordatoEligibilityService`; `IAttestationGuidanceService`; `IComuneImuNotificationService` + export endpoint; one new `LeaseEventType` pair for IMU-notification tracking |

---

## User Story

As a **long-rent landlord** with a property in a comune covered by a territorial agreement (piloting Seveso and Cesano Maderno), I want CasaZen to tell me whether canone concordato applies to my property, what canone range and fiscal benefits result, which local association can attest conformity, and — once my lease is registered — an assisted comune-specific IMU notification I can send myself, so that I can access the real fiscal benefit without personally researching four disconnected sources, while remaining fully responsible for filing and attestation myself.

---

## Acceptance Criteria

### Backend

- **AC1 (reference data model)**: New entities `TerritorialRentAgreement` (`Comune`, `Region`, `AgreementName`, `SignedDate`, `EffectiveDate`, `SourceUrl`, `DataCompleteness` enum `Complete`/`Partial`/`Missing`) and `ConcordatoRentBand` (`TerritorialRentAgreementId` FK, `ZoneName`, `MinSqm`, `MaxSqm` nullable, `SubFascia1MinEurSqmYear`/`Max`, `SubFascia2Min`/`Max`, `SubFascia3Min`/`Max`). Seeded via EF migration — rates are **never hardcoded in service code**, mirroring the `TouristTaxRate` convention (`.claude/rules/gotchas.md`).

- **AC2 (national ATA list — kept separate)**: New entity `HighTensionAreaComune` (`Comune`, `Region`, `SourceReference`, `VerifiedDirectly` bool) seeded with known comuni including Seveso and Cesano Maderno, structurally independent from `TerritorialRentAgreement`. Research found these are two **legally distinct** lists that happen to overlap in Monza e Brianza — a regression test asserts the eligibility service reads them from separate tables, never conflates "agreement covers this comune" with "comune is ATA for cedolare/IRPEF/registro purposes."

- **AC3 (eligibility calculation)**: `ICanoneConcordatoEligibilityService.CalculateAsync(propertyId, RentBandCharacteristics)` — given `Sqm`, boolean element groups (`HasTypeAElements`, `HasTypeBElements`, `HasTypeCElements`, `TypeDElementCount`), `IsFurnished`, `ContractYears` — resolves the property's `TerritorialRentAgreement` by `Property.City`, determines sub-fascia per the rule (all A + ≥3 B ⇒ sub-fascia 2; + ≥3 C + ≥2 of the counted D elements ⇒ sub-fascia 3; otherwise sub-fascia 1), applies the furnished/size/duration coefficients, and returns canone min/max (annuo and mensile) plus a `FiscalBenefits` object separating `ImuReduction` (`Applies = true` whenever the contract is genuinely concordato, no territorial condition) from `CedolareIrpefRegistroBenefits` (`Applies` = true only if `HighTensionAreaComune` contains the property's comune) — with a non-binding `Disclaimer` string, mirroring the `ICedolareAdvisoryService` pattern.

- **AC4 (endpoint)**: `GET /api/properties/{propertyId}/canone-concordato/eligibility?sqm=&hasTypeA=&hasTypeB=&hasTypeC=&typeDCount=&furnished=&years=` returns the AC3 result. Gated `RequireContext:long-rent:property.read`, owner-scoped; cross-org access → 404 (RF1).

- **AC5 (no silent fallback)**: When the property's comune has no `TerritorialRentAgreement` or `DataCompleteness != Complete`, the endpoint returns `Available = false` with a `Reason` string (e.g. "dato non disponibile per questo comune — verificare con l'associazione di categoria locale") — never a guessed or interpolated canone range. A test seeds a `Missing`-completeness comune and asserts no numeric range is returned.

- **AC6 (feeds the existing template mechanism — no engine changes)**: The AC3 result (comune, zone, sub-fascia, canone) is exposed as a DTO consumable by the **existing** `ILeaseTemplateService` / `LeaseContractTemplateService` `CanoneConcordato` template variant (per `spec-ltr-rli-registration` AC3). This spec supplies data fields only; it does **not** modify template rendering, versioning, or the counsel-review gate.

- **AC7 (attestation guidance — CasaZen never issues it)**: New `IAttestationGuidanceService.GetSignatoryOrganizationsAsync(propertyId)` returns the signatory organizations (`Name`, `Role` Proprietà/Inquilini, `Contact`) recorded against the property's `TerritorialRentAgreement`, for the landlord to contact directly. A regression test asserts no code path in this service calls an external association API or auto-submits a request — CasaZen surfaces contact data only.

- **AC8 (comune IMU-notification export — new, distinct from RLI)**: New `IComuneImuNotificationService.ExportAsync(leaseId)` / `GET /api/leases/{id}/canone-concordato/imu-notification/export` returns a comune-specific notification package (recipient email/PEC, any known official module reference, a pre-filled cover-letter referencing the lease) as a PDF. Requires `LeaseContract.Status == LeaseStatus.Registered` (the RLI step, owned by `spec-ltr-rli-registration`, must already be complete). **Export/preview only — no auto-send**, consistent with the "assisted, not automatic" framing already established for RLI.

- **AC9 (checklist extension, not duplication)**: New `LeaseEventType` values `ImuNotificationExported` and `ImuNotificationMarkedSent` (the latter set by an explicit landlord action, never inferred) extend the **existing** 30-day checklist pattern from `spec-ltr-rli-registration` AC6 with one additional item. This spec does **not** touch the extra-EU Questura checklist item — that is already owned by `spec-ltr-rli-registration` AC7 and is out of scope here (see Out of Scope).

- **AC10 (RBAC / RF1)**: All new endpoints are gated under the existing `long-rent` context permissions (`property.read`, `lease.read`) and owner-scoped via the existing verified-property / verified-lease pattern (`GetVerifiedLeaseAsync`-equivalent). Cross-org reads return 404; `OrgId` is never client-supplied.

- **AC11 (honest seed data)**: The Monza e Brianza migration seeds full `ConcordatoRentBand` rows for Seveso and Cesano Maderno (`DataCompleteness = Complete`, per `Sessions/research-canone-concordato-mb.md` §3–4); the remaining 53 comuni of the same agreement are seeded as `TerritorialRentAgreement` rows with `DataCompleteness = Missing` and **no** `ConcordatoRentBand` children. A test asserts `CalculateAsync` for a `Missing` comune returns `Available = false`, never a fabricated range.

### Frontend

- **AC12 (calculator + guidance UI — existing shell only)**: The property/lease detail view (inside the **existing** `LongTermAppShell` from `spec-ltr-frontend`, no new route tree) gains: a characteristics form → canone range + fiscal-benefit breakdown (IMU vs. cedolare/IRPEF/registro shown as visually distinct, each with its own disclaimer), an explicit "dato non disponibile" empty state when `Available = false`, an attestation-guidance panel listing signatory associations with contact info, and an "Esporta comunicazione IMU" button enabled only once the lease is `Registered`.

- **AC13 (GDPR / i18n)**: No new PII beyond property/lease data already governed by existing GDPR rules. All end-user strings in Italian; disclaimers shown verbatim as returned by the backend; the IMU-notification export is fetched via the authenticated, owner-scoped endpoint only (never a raw link).

---

## UX / UI Quality

**Required** (Frontend ACs present).

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Landlord completes calculator → sees range/benefits → (later) exports IMU notification without guessing next step | L3 scripted flow below |
| Language | End-user strings Italian | L2/L3 assert Italian primary labels and disclaimers |
| Empty state | "Dato non disponibile" shown, not a blank panel, when `Available = false` | L2 empty fixture (Missing-completeness comune) |
| Error state | 4xx/5xx surfaced as human Italian message | L2/L3 forced error |
| Destructive / legal copy | Disclaimers ("informativa, non consulenza fiscale/legale") shown exactly as documented | Assert exact phrases from AC3/AC12 |

**Happy-path script:**

1. Open the property/lease detail view inside the long-rent layer for a Seveso or Cesano Maderno property.
2. Enter mq, elementi A/B/C/D, arredamento, durata → see canone range with IMU (always) vs. cedolare/IRPEF/registro (ATA-conditional) clearly separated, each with its disclaimer.
3. Open the attestation-guidance panel → see at least one signatory association with contact details.
4. After the lease reaches `Registered` (via the existing `spec-ltr-rli-registration` flow) → "Esporta comunicazione IMU" becomes enabled and downloads a PDF.
5. Done when the Verifiable Outcome for AC3/AC8 holds.

---

## Verifiable Outcomes

**Required.** One row per AC.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `TerritorialRentAgreement`/`ConcordatoRentBand` exist as EF entities with migration; no rate literal appears in service code (grep-level check) | Hardcoded canone value in a service class; missing migration |
| AC2 | L1 | `HighTensionAreaComune` is a separate table from `TerritorialRentAgreement`; a comune present in one but not the other resolves independently | Service treats agreement coverage as proof of ATA status |
| AC3 | L1 | Given Seveso, 65 mq, all A + ascensore/cucina (B) only → sub-fascia 2 → canone 3.445–5.525 €/anno; `ImuReduction.Applies = true`; `CedolareIrpefRegistroBenefits.Applies = true` (Seveso is ATA) | Wrong sub-fascia; benefits not split; missing disclaimer |
| AC4 | L1 | `GET .../eligibility` returns 200 with the AC3 shape for an owned property; 404 for another org's property | Wrong status; `OrgId` accepted from client |
| AC5 | L1 | `Missing`-completeness comune → `Available = false` + non-empty `Reason`; no numeric canone field populated | Silent fallback to a guessed/interpolated range |
| AC6 | L1 | The DTO returned by AC3 contains every field `LeaseContractTemplateService`'s `CanoneConcordato` variant declares as required input | Missing field forces the template service to guess or hardcode |
| AC7 | L1 | Response lists ≥1 organization for Seveso/Cesano Maderno with name + role + contact; no outbound HTTP call to any association domain occurs during the test | Service attempts to call an external association API |
| AC8 | L1 + L3 | `GET .../imu-notification/export` on a `Registered` lease returns `%PDF` bytes with recipient + lease reference; on a non-`Registered` lease → 409/400 | Export succeeds pre-registration; empty/stub PDF |
| AC9 | L1 | `ImuNotificationExported` emitted on export; `ImuNotificationMarkedSent` only settable via an explicit endpoint, never inferred from other state | Event emitted without an explicit landlord action |
| AC10 | L1 | Missing `long-rent` context or wrong owner → 403/404 on every new endpoint | Any new endpoint reachable without context/ownership check |
| AC11 | L1 | Query for a `Missing` comune (one of the other 53) returns zero `ConcordatoRentBand` rows | A fabricated band exists for a comune the research never confirmed |
| AC12 | L2 + L3 | Calculator, benefit split, empty state, guidance panel, and export button render inside the existing shell with correct enabled/disabled state per lease status | Blank panel on empty data; export button enabled before `Registered` |
| AC13 | L2 + L3 | Italian labels/disclaimers verbatim; export fetched only via authenticated endpoint | English fallback copy; direct/unauthenticated download link |

Rules: UI ACs need L2 **and** L3 outcomes. Non-UI ACs may be L1-only. Visibility-only asserts are insufficient for the export (AC8).

---

## Export / Report Criteria

**Required** (AC8 is a PDF export).

### PDF — IMU notification export

| Requirement | Required |
|---|---|
| Real PDF bytes (`%PDF`), not stub/empty | yes |
| Recipient (comune email/PEC) and lease/contract reference visible on first page | yes |
| Clear statement that this is a **pre-filled draft for the landlord to review and send themselves** — no CasaZen branding implying official submission | yes |
| No tax-due computation, no fabricated aliquota presented as official when the source is a derived value (e.g. Cesano Maderno's ≈0,78%) — label derived values explicitly | yes |
| Readable, labeled content (not a debug dump) | yes |

### JSON (underlying data, if exposed)

- Same business fields as the PDF; must not omit the `DataCompleteness`/derivation flags that justify the disclaimer.

---

## Technical Notes

### Backend — Files to create / modify

| File | Action |
|---|---|
| `Casazen.Core/Entities/TerritorialRentAgreement.cs` | **CREATE** — `Comune`, `Region`, `AgreementName`, `SignedDate`, `EffectiveDate`, `SourceUrl`, `DataCompleteness` |
| `Casazen.Core/Entities/ConcordatoRentBand.cs` | **CREATE** — `TerritorialRentAgreementId` FK, `ZoneName`, `MinSqm`/`MaxSqm`, sub-fascia min/max ×3 |
| `Casazen.Core/Entities/HighTensionAreaComune.cs` | **CREATE** — `Comune`, `Region`, `SourceReference`, `VerifiedDirectly` |
| `Casazen.Core/Entities/Enums/DataCompleteness.cs` | **CREATE** — `Complete`, `Partial`, `Missing` |
| `Casazen.Core/Entities/Enums/LeaseEventType.cs` | **MODIFY** — add `ImuNotificationExported`, `ImuNotificationMarkedSent` (EXISTS enum) |
| `Casazen.Core/Repositories/ITerritorialRentAgreementRepository.cs`, `IHighTensionAreaComuneRepository.cs` | **CREATE** |
| `Casazen.Infrastructure/Repositories/TerritorialRentAgreementRepository.cs`, `HighTensionAreaComuneRepository.cs` | **CREATE** (EF Core) |
| `Casazen.Core/Services/ICanoneConcordatoEligibilityService.cs` | **CREATE** |
| `Casazen.Infrastructure/Services/CanoneConcordatoEligibilityService.cs` | **CREATE** |
| `Casazen.Core/Services/IAttestationGuidanceService.cs` | **CREATE** |
| `Casazen.Infrastructure/Services/AttestationGuidanceService.cs` | **CREATE** |
| `Casazen.Core/Services/IComuneImuNotificationService.cs` | **CREATE** |
| `Casazen.Infrastructure/External/ComuneImuNotificationService.cs` | **CREATE** — PDF generation, reuses the PDF approach already used by `LeaseContractTemplateService` |
| `Casazen.Web/Controllers/CanoneConcordatoController.cs` | **CREATE** — `/api/properties/{id}/canone-concordato/eligibility`, `/api/properties/{id}/canone-concordato/attestation-guidance` |
| `Casazen.Web/Controllers/LeasesController.cs` | **MODIFY** — add `/{id}/canone-concordato/imu-notification/export` and `/mark-sent` (EXISTS controller) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | **MODIFY** — register new repos/services (EXISTS) |
| `Casazen.Infrastructure/Migrations/*_AddTerritorialRentAgreements.cs` | **CREATE** — seed Monza e Brianza agreement + Seveso/Cesano Maderno bands + the other 53 comuni as `Missing`; rebase onto `AppDbContextModelSnapshot.cs` (RF3) |
| `Casazen.Tests/Unit/Services/CanoneConcordatoEligibilityServiceTests.cs` | **CREATE** — sub-fascia matrix, ATA-vs-agreement separation, Missing-comune no-fallback (AC3, AC5) |
| `Casazen.Tests/Unit/Services/AttestationGuidanceServiceTests.cs` | **CREATE** — no external call (AC7) |
| `Casazen.Tests/Unit/Services/ComuneImuNotificationServiceTests.cs` | **CREATE** — pre-registration rejection, PDF shape (AC8) |
| `Casazen.Tests/Integration/TerritorialRentAgreementMigrationTests.cs` | **CREATE** (or extend `MigrationTests.cs`) — AC1, AC11 |

### Frontend — Files to create / modify

| File | Action |
|---|---|
| `src/features/leases/components/canone-concordato-calculator.tsx` | **CREATE** — characteristics form + benefit split + empty state |
| `src/features/leases/components/attestation-guidance-panel.tsx` | **CREATE** — association list |
| `src/features/leases/components/imu-notification-export-button.tsx` | **CREATE** — gated on `Registered` |
| `src/api/canone-concordato.api.ts` | **CREATE** |
| `src/queries/use-canone-concordato.ts` | **CREATE** |
| `src/features/leases/lease-detail-page.tsx` | **MODIFY** — mount the three components above (EXISTS, per `spec-ltr-frontend`) |

**Complexity:** L
**Migration:** yes — new tables, seeded reference data
**Dependencies:** `spec-ltr-rli-registration`, `spec-ltr-frontend`
**Repos:** BE + FE

---

## Compliance

- **No new intermediation risk**: this spec adds no filing capability of its own — the RLI submission remains entirely owned by `spec-ltr-rli-registration` (Openapi.it as filing channel, delega gate, CasaZen ≠ *intermediario abilitato* DPR 322/1998). The IMU-notification export (AC8) is explicitly **export/preview only**, never auto-sent, for the same reason.
- **[COUNSEL_REQUIRED]** Confirm Seveso and Cesano Maderno's presence on the official Agenzia delle Entrate / CIPE "alta tensione abitativa" list from a primary source — current seed data (AC2) relies on converging secondary sources, not the Delibera CIPE 87/2003 text itself.
- **[COUNSEL_REQUIRED]** Confirm whether one or two signatory organizations are required for a valid attestazione di conformità in the Monza e Brianza territorial agreement — national law requires "at least one"; local sources describe a bilateral practice. `IAttestationGuidanceService` (AC7) must not encode an unverified rule as fact.
- **[COUNSEL_REQUIRED]** The Cesano Maderno IMU aliquota used in AC8/export (≈0,78%) is a **derived** value, not a separately published municipal rate — must be labeled as such, never presented as an official aliquota.
- **Disclaimer discipline**: every fiscal-benefit output (AC3) carries a non-binding "informativa, non consulenza fiscale" string, matching the existing `ICedolareAdvisoryService` convention — this spec does not introduce a second disclaimer wording.
- **Data provenance**: every `TerritorialRentAgreement`/`HighTensionAreaComune` row must retain its `SourceUrl`/`SourceReference` and `DataCompleteness`, so the UI can show "ultima verifica" per comune (mitigates the "obsolescence" risk flagged in `Sessions/business-analysis-canone-concordato.md` §4).
- **GDPR**: no new personal-data categories; existing `LeaseContract`/`Party` retention and erasure rules are unaffected.

---

## Dependencies

- **Requires**: `LeaseContract.FiscalRegime` incl. `CanoneConcordato` (EXISTS); `Property.City` (EXISTS); `ILeaseTemplateService`/`LeaseContractTemplateService` (EXISTS); the counsel-reviewed template-variant mechanism and `ICedolareAdvisoryService` disclaimer pattern (both from `spec-ltr-rli-registration` AC3/AC4 — depends on that spec landing first); the `long-rent` context/RBAC scaffolding (EXISTS); `spec-tenant-boundary` `OrgId` invariant (EXISTS, RF1).
- **Blocks**: nothing currently scheduled — sits inside the frozen LTR phase.
- **Related**: `spec-ltr-frontend` (mounts the new components into the existing lease detail page, no new shell); `spec-ltr-recurring-rent` (its bollo rule already keys off `FiscalRegime`, unaffected by this spec).
- **Does not modify**: RLI submission mechanics, the delega/authorization gate, the e-sign flow, the rent ledger, or the existing extra-EU Questura checklist item (all owned by sibling specs).

## Test expectations (process contract)

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |
| L2 | Playwright demo + `page.route` OK; titled `test('ACn: …')` per UI AC | One smoke for all ACs; visibility-only for the export |
| L3 | Real API local/staging; titled test per UI AC; real PDF bytes asserted for AC8 | Mocking the export path under test |

Design Stage 02 must produce `## AC Test Map` with one row per AC before Stage 03/04 gates apply.

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] Primary-source confirmation of Seveso/Cesano Maderno on the official ATA list (see Compliance).
- [COUNSEL_REQUIRED] Number of signatory organizations required for a valid attestazione di conformità in the MB territorial agreement (see Compliance).
- [COUNSEL_REQUIRED] Labeling requirement for the Cesano Maderno derived IMU value (see Compliance).

## Out of Scope

- National coverage beyond the Monza e Brianza pilot (Seveso + Cesano Maderno fully seeded; the other 53 comuni of the same agreement exist as `Missing` rows only, not fabricated data).
- Any change to RLI submission mechanics, the delega/authorization gate, or Openapi.it integration — entirely owned by `spec-ltr-rli-registration`.
- Automatic submission of the IMU notification to the comune, or of the attestation request to an association — both remain explicit, manual, landlord-initiated actions.
- CasaZen issuing or countersigning the attestazione di conformità itself.
- Redesigning `FiscalRegime` to separate "contract type" (concordato vs. libero) from "tax treatment" (cedolare vs. ordinario) — today `CanoneConcordato` is a single bundled enum value; see Open Questions.
- The extra-EU Questura "cessione di fabbricato" checklist item — already specced and owned by `spec-ltr-rli-registration` AC7.
- Choice of e-signature provider — reuses whatever the existing e-sign flow already wires.

## Open Questions

- Should `FiscalRegime.CanoneConcordato` eventually be split into contract-type × tax-treatment (allowing concordato + regime ordinario, not just the implied concordato + cedolare-10% bundle)? Not resolved here — flagged for a future spec if the product needs the finer distinction; changing it now would ripple into `spec-regime-fiscale-2026` and `spec-ltr-recurring-rent`'s bollo rule.
- Confirm whether the Monza e Brianza territorial agreement (in force since 2024-05-01, nominally 18 months) has been renewed as of the implementation start date — re-verify `Sessions/research-canone-concordato-mb.md` §11 before seeding data, since the research found no confirmation either way.
- Owner for keeping `TerritorialRentAgreement`/`HighTensionAreaComune` data current over time (manual review cadence vs. a `regulatory_agent` job per `.claude/rules/compliance.md`) — not decided in this spec.
