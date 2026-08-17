---
id: —
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

Research (`Sessions/research-canone-concordato-mb.md`, `Sessions/business-analysis-canone-concordato.md`) confirms canone concordato is a real, currently-unclaimed fiscal opportunity for CasaZen's long-rent landlords: IMU −25% applies **nationally** with no territorial restriction, and cedolare secca 10% / IRPEF −30% / registro −30% apply only in "alta tensione abitativa" (ATA) comuni. Secondary sources converge on Seveso and Cesano Maderno being ATA; that is **not** product truth until counsel confirms the official AdE / CIPE list (`VerifiedDirectly` is a hard gate — see AC2/AC3). Today a landlord must navigate four disconnected channels (territorial agreement tables, a category association for the attestazione di conformità, the Agenzia delle Entrate RLI portal, and a separate comune-level IMU communication) with no tooling support.

This spec adds the **data and calculation layer** the codebase is missing: a `TerritorialRentAgreement` / `ConcordatoRentBand` / `TerritorialAgreementSignatory` reference-data model (same pattern as `TouristTaxRate` — city-scoped rates, never hardcoded), an eligibility/rent-range calculator, attestation-guidance surfacing, and a comune-specific IMU-notification export. It **reuses** the existing `LeaseContract.FiscalRegime.CanoneConcordato` value and **builds on** `spec-ltr-rli-registration` (counsel-reviewed template variants, the assisted/delega RLI framework, the extra-EU Questura checklist item — all SPEC-ONLY, not in code) rather than re-specifying any of it. It is **new data + calculation**, not a rebuild of the lease workflow.

**Done means**: a long-rent landlord with a property in Seveso or Cesano Maderno can enter the property's characteristics **including zone / foglio catastale**, see a canone range with a clear split between theoretical IMU eligibility (national) and ATA-conditional benefits (shown as pending until `VerifiedDirectly`), never a guessed number when data or zone is incomplete, have that range exposed as a DTO for the `spec-ltr-rli-registration` template contract, see which local associations can issue the attestazione di conformità, and — once the lease is RLI-registered — export a comune-specific IMU-notification package to send themselves. CasaZen never files anything automatically and never issues the attestation itself. All `Applies` flags are theoretical eligibility, not proof the landlord has obtained the benefit.

**Governance note**: the entire "Phase 1.5 — LTR" roadmap phase is marked `frozen` in `Sessions/specs/README.md` (issue #269, "closed — do not resume"). This spec is prepared as ready-to-build documentation for when that freeze lifts; its `status: frozen` frontmatter is intentional, not an oversight, and mirrors the four sibling `spec-ltr-*` files.

**Phase:** 1.5 — LTR Complete + Verify (frozen) · **Type:** feature · **Status:** frozen · **Issue:** none yet

Design: not yet started (blocked by phase freeze). ADRs: none.

### What EXISTS vs what is NEW

| | Item |
|---|---|
| **EXISTS (code, verified)** | `FiscalRegime.CanoneConcordato` (`Casazen.Core/Entities/Enums/FiscalRegime.cs`); `Property.City`; `TouristTaxRate` city-scoped rate-table pattern (`.claude/rules/gotchas.md`); `ILeaseTemplateService` / `LeaseContractTemplateService.GeneratePdfAsync` — **stub** (UTF-8 placeholder bytes, not `%PDF`, no per-regime variant); `GetVerifiedLeaseAsync` owner-scope helper (`LeaseWorkflowService`); `LeaseEventType` enum (no IMU-notification values yet); `long-rent` RBAC (`lease.read`, `lease.create`, `lease.sign`, `lease.register`, `rent.read`, `rent.manage`) — there is **no** `long-rent:property.read` |
| **SPEC-ONLY (depends on `spec-ltr-rli-registration`, not in code)** | Counsel-reviewed template-variant-per-`FiscalRegime` (that spec AC3); `ICedolareAdvisoryService` disclaimer pattern (that spec AC4 — zero files in repo); assisted/delega RLI flow, 30-day checklist, extra-EU Questura item (that spec AC1/AC6/AC7) |
| **NEW** | `TerritorialRentAgreement` + `ConcordatoRentBand` + `TerritorialAgreementSignatory` entities and migration seed (Monza e Brianza, full data for Seveso/Cesano Maderno, honest `Missing` rows for the other comuni of the same agreement); `HighTensionAreaComune` lookup (nationally-scoped ATA list, kept structurally separate from territorial-agreement coverage, `VerifiedDirectly` hard-gates ATA `Applies`); `ICanoneConcordatoEligibilityService`; `IAttestationGuidanceService`; `IComuneImuNotificationService` + **new** `%PDF` export (does **not** reuse the template stub); one new `LeaseEventType` pair for IMU-notification tracking |

---

## User Story

As a **long-rent landlord** with a property in a comune covered by a territorial agreement (piloting Seveso and Cesano Maderno), I want CasaZen to tell me whether canone concordato applies to my property, what canone range and fiscal benefits result, which local association can attest conformity, and — once my lease is registered — an assisted comune-specific IMU notification I can send myself, so that I can access the real fiscal benefit without personally researching four disconnected sources, while remaining fully responsible for filing and attestation myself.

---

## Acceptance Criteria

### Backend

- **AC1 (reference data model)**: New entities `TerritorialRentAgreement` (`Comune`, `Region`, `AgreementName`, `SignedDate`, `EffectiveDate`, `SourceUrl`, `DataCompleteness` enum `Complete`/`Partial`/`Missing`), `ConcordatoRentBand` (`TerritorialRentAgreementId` FK, `ZoneName`, `CadastralSheets` nullable, `MinSqm`, `MaxSqm` nullable, `SubFascia1MinEurSqmYear`/`Max`, `SubFascia2Min`/`Max`, `SubFascia3Min`/`Max`), and `TerritorialAgreementSignatory` (`TerritorialRentAgreementId` FK, `Name`, `Role` Proprietà/Inquilini, `Contact`). Seeded via EF migration — rates are **never hardcoded in service code**, mirroring the `TouristTaxRate` convention (`.claude/rules/gotchas.md`). Re-count MB comuni from the agreement PDF at seed time (research notes 54 vs 55); do not invent a missing comune to hit a round number.

- **AC2 (national ATA list — kept separate)**: New entity `HighTensionAreaComune` (`Comune`, `Region`, `SourceReference`, `VerifiedDirectly` bool) seeded with candidate comuni including Seveso and Cesano Maderno from **secondary sources only**, structurally independent from `TerritorialRentAgreement`. `VerifiedDirectly` defaults to `false` until counsel confirms the official AdE / CIPE list. A regression test asserts the eligibility service reads the two lists from separate tables, never conflates "agreement covers this comune" with "comune is ATA." **Hard gate**: `CedolareIrpefRegistroBenefits.Applies` is `true` only when `VerifiedDirectly = true`; a seeded-but-unverified row must not make `Applies = true`.

- **AC3 (eligibility calculation)**: `ICanoneConcordatoEligibilityService.CalculateAsync(propertyId, RentBandCharacteristics)` — given `Sqm`, **integer counts** (`TypeAElementCount`, `TypeBElementCount`, `TypeCElementCount`, `TypeDElementCount`), `IsFurnished`, `ContractYears`, and `ZoneName` (or `CadastralSheet` when the comune uses fogli). Resolves the property's `TerritorialRentAgreement` by `Property.City`, selects the `ConcordatoRentBand` for the given zone/foglio, determines sub-fascia per the rule (all required A elements present + `TypeBElementCount ≥ 3` ⇒ sub-fascia 2; plus `TypeCElementCount ≥ 3` + `TypeDElementCount ≥ 2` of the counted D elements ⇒ sub-fascia 3; otherwise sub-fascia 1), applies the furnished/size/duration coefficients from the agreement, and returns canone min/max (annuo and mensile) plus a `FiscalBenefits` object: `ImuReduction.Applies` = theoretical national eligibility when a genuine concordato range is returned (not proof the landlord has obtained IMU −25%); `CedolareIrpefRegistroBenefits.Applies` = true only if `HighTensionAreaComune.VerifiedDirectly` for that comune; `AttestationRequired` = true for non-assisted contracts (attestazione is a condition for all agevolazioni — CasaZen does not issue it). Non-binding `Disclaimer` string, mirroring the SPEC-ONLY `ICedolareAdvisoryService` pattern (`spec-ltr-rli-registration` AC4). Research §4's Seveso 65 mq / two type-B example (3.445–5.525 €) is **illustrative only**, not the L1 oracle (that example uses 2 type-B elements, which is fascia 1 under the ≥3 B rule).

- **AC4 (endpoint)**: `GET /api/properties/{propertyId}/canone-concordato/eligibility?sqm=&typeACount=&typeBCount=&typeCCount=&typeDCount=&furnished=&years=&zone=` returns the AC3 result. Gated `RequireContext:long-rent:lease.read` (EXISTS — there is no `long-rent:property.read`), owner-scoped; cross-org access → 404 (RF1).

- **AC5 (no silent fallback)**: When the property's comune has no `TerritorialRentAgreement` or `DataCompleteness != Complete`, **or** the comune has multiple zones and `zone`/`foglio` is missing or unmatched, the endpoint returns `Available = false` with a `Reason` string (e.g. "dato non disponibile per questo comune — verificare con l'associazione di categoria locale" / "zona o foglio catastale obbligatorio") — never a guessed, interpolated, or blended-across-zones canone range. A test seeds a `Missing`-completeness comune and a two-zone comune without zone and asserts no numeric range is returned.

- **AC6 (feeds the RLI-spec template contract — no engine changes here)**: The AC3 result is exposed as a DTO owned by **this** spec with fields `comune`, `zone`, `subFascia`, `canoneMinAnnuo`, `canoneMaxAnnuo`, `canoneMinMensile`, `canoneMaxMensile`, `dataCompleteness`, `imuAppliesTheoretical`, `ataApplies`, `attestationRequired`, `disclaimer`. That DTO is the interface for the `CanoneConcordato` template variant **specified** in `spec-ltr-rli-registration` AC3 (not yet in code — `LeaseContractTemplateService` is a stub with no per-regime variant). This spec supplies data fields only; it does **not** implement template rendering, versioning, or the counsel-review gate.

- **AC7 (attestation guidance — CasaZen never issues it)**: New `IAttestationGuidanceService.GetSignatoryOrganizationsAsync(propertyId)` returns the `TerritorialAgreementSignatory` rows (`Name`, `Role` Proprietà/Inquilini, `Contact`) for the property's `TerritorialRentAgreement`, for the landlord to contact directly. Surfaces contacts only — does **not** encode a 1-vs-2 signatory validity rule (that remains `[COUNSEL_REQUIRED]`). A regression test asserts no code path in this service calls an external association API or auto-submits a request.

- **AC8 (comune IMU-notification export — new, distinct from RLI)**: New `IComuneImuNotificationService.ExportAsync(leaseId)` / `GET /api/leases/{id}/canone-concordato/imu-notification/export` returns a comune-specific notification package (known recipient email/PEC **and**, where research records more than one channel, an explicit uncertainty label — AC8 must not pick Seveso's email-vs-SPID as "the" official path) as a **real `%PDF`**. Requires `LeaseContract.Status == LeaseStatus.Registered` (the RLI step, owned by `spec-ltr-rli-registration`, must already be complete). **Export/preview only — no auto-send**. Derived IMU figures (Cesano Maderno ≈0,78% from 2025 "Altri fabbricati" 1,04% × 75%) must be labeled `valore derivato` + tax year; 2026 Cesano delibera was not published at research date. Does **not** reuse `LeaseContractTemplateService` (that is a UTF-8 stub).

- **AC9 (checklist extension, not duplication)**: New `LeaseEventType` values `ImuNotificationExported` and `ImuNotificationMarkedSent` (the latter set by an explicit landlord action, never inferred). Events are independently testable even if the 30-day checklist UI from `spec-ltr-rli-registration` AC6 is absent. This spec does **not** touch the extra-EU Questura checklist item — that is already owned by `spec-ltr-rli-registration` AC7 and is out of scope here (see Out of Scope).

- **AC10 (RBAC / RF1)**: All new endpoints are gated under existing `long-rent` permissions: eligibility and attestation-guidance use `lease.read`; IMU export and mark-sent use `lease.read` (export) / `lease.register` (mark-sent, landlord attestation that they sent it). Owner-scoped via the existing verified-property / verified-lease pattern (`GetVerifiedLeaseAsync`-equivalent). Cross-org reads return 404; `OrgId` is never client-supplied.

- **AC11 (honest seed data)**: The Monza e Brianza migration seeds full `ConcordatoRentBand` + `TerritorialAgreementSignatory` rows for Seveso and Cesano Maderno (`DataCompleteness = Complete`, per `Sessions/research-canone-concordato-mb.md` §3–4) **only after** the `[COUNSEL_REQUIRED]` gates on agreement currency and Seveso reception are recorded on the row (`SourceUrl` + last-verified date); until then those two comuni may be seeded `Partial` with bands present but `Available` still requiring the zone/completeness rules. The remaining comuni of the same agreement are seeded as `TerritorialRentAgreement` rows with `DataCompleteness = Missing` and **no** `ConcordatoRentBand` children. A test asserts `CalculateAsync` for a `Missing` comune returns `Available = false`, never a fabricated range.

### Frontend

- **AC12 (calculator + guidance UI — existing shell only)**: The property/lease detail view (inside the **existing** `LongTermAppShell` from `spec-ltr-frontend`, no new route tree) gains: a characteristics form including **zone / foglio** → canone range + fiscal-benefit breakdown (theoretical IMU vs. ATA-conditional, ATA shown as pending when `VerifiedDirectly = false`, each with its own disclaimer), an explicit "dato non disponibile" empty state when `Available = false`, an attestation-guidance panel listing signatory associations with contact info, and an "Esporta comunicazione IMU" button enabled only once the lease is `Registered` (Slice B UI; Slice A may hide the button).

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
2. Enter mq, zone/foglio, element counts A/B/C/D, arredamento, durata → see canone range with theoretical IMU vs. ATA-conditional (pending when unverified) clearly separated, each with its disclaimer.
3. Open the attestation-guidance panel → see at least one signatory association with contact details.
4. After the lease reaches `Registered` (via the existing `spec-ltr-rli-registration` flow, Slice B) → "Esporta comunicazione IMU" becomes enabled and downloads a PDF.
5. Done when the Verifiable Outcome for AC3/AC8 holds.

---

## Verifiable Outcomes

**Required.** One row per AC.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `TerritorialRentAgreement`/`ConcordatoRentBand`/`TerritorialAgreementSignatory` exist as EF entities with migration; no rate literal appears in service code (grep-level check) | Hardcoded canone value in a service class; missing signatory table; missing migration |
| AC2 | L1 | `HighTensionAreaComune` is a separate table; seeded Seveso/Cesano have `VerifiedDirectly = false`; ATA `Applies` stays false until that flag is true | Service treats agreement coverage as ATA; `Applies = true` on unverified seed |
| AC3 | L1 | Given Seveso (single zone), 65 mq, all required A + `TypeBElementCount ≥ 3` and `TypeCElementCount < 3` → sub-fascia 2; numeric min/max come from the seeded band × mq × documented coefficients; `ImuReduction.Applies` is theoretical + `AttestationRequired = true`; `CedolareIrpefRegistroBenefits.Applies = false` while `VerifiedDirectly = false` | Boolean inputs; fascia 2 from only 2 type-B; ATA `Applies = true` on unverified seed; missing disclaimer |
| AC4 | L1 | `GET .../eligibility` returns 200 with the AC3 shape for an owned property using `lease.read`; 404 for another org's property | Gated on non-existent `long-rent:property.read`; `OrgId` accepted from client |
| AC5 | L1 | `Missing`-completeness comune **or** Cesano Maderno without zone → `Available = false` + non-empty `Reason`; no numeric canone field populated | Silent fallback; blended min/max across Cesano zones |
| AC6 | L1 | AC3 DTO contains exactly `comune`, `zone`, `subFascia`, `canoneMinAnnuo`, `canoneMaxAnnuo`, `canoneMinMensile`, `canoneMaxMensile`, `dataCompleteness`, `imuAppliesTheoretical`, `ataApplies`, `attestationRequired`, `disclaimer` | Missing field; asserting against an undeclared RLI template field set |
| AC7 | L1 | Response lists ≥1 `TerritorialAgreementSignatory` for Seveso/Cesano Maderno with name + role + contact; no outbound HTTP call to any association domain; no 1-vs-2 validity rule encoded | Service attempts to call an external association API; encodes bilateral-vs-unilateral as fact |
| AC8 | L1 + L3 | `GET .../imu-notification/export` on a `Registered` lease returns `%PDF` bytes with recipient + lease reference + derived-value/year labels where applicable; on a non-`Registered` lease → 409/400; Seveso export does not assert a single official channel | Export succeeds pre-registration; empty/stub/UTF-8 PDF; Cesano 0,78% presented as official aliquota |
| AC9 | L1 | `ImuNotificationExported` emitted on export; `ImuNotificationMarkedSent` only settable via an explicit endpoint, never inferred from other state | Event emitted without an explicit landlord action |
| AC10 | L1 | Missing `long-rent` context or wrong owner → 403/404 on every new endpoint; policies used are existing `lease.read` / `lease.register` | Any new endpoint reachable without context/ownership check; `property.read` on long-rent |
| AC11 | L1 | Query for a `Missing` comune returns zero `ConcordatoRentBand` rows | A fabricated band exists for a comune the research never confirmed |
| AC12 | L2 + L3 | Calculator (incl. zone), benefit split with ATA-pending state, empty state, guidance panel, and (Slice B) export button render inside the existing shell with correct enabled/disabled state per lease status | Blank panel on empty data; export button enabled before `Registered`; ATA shown as confirmed while unverified |
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
| Where research records more than one IMU channel (Seveso email vs SPID), PDF labels the uncertainty — does not pick one as official | yes |
| Clear statement that this is a **pre-filled draft for the landlord to review and send themselves** — no CasaZen branding implying official submission | yes |
| No tax-due computation, no fabricated aliquota presented as official when the source is a derived value (e.g. Cesano Maderno's ≈0,78% from 2025 1,04% × 75%) — label derived values + tax year explicitly | yes |
| Readable, labeled content (not a debug dump) | yes |

### JSON (underlying data, if exposed)

- Same business fields as the PDF; must not omit the `DataCompleteness`/derivation flags that justify the disclaimer.

---

## Technical Notes

### Backend — Files to create / modify

| File | Action |
|---|---|
| `Casazen.Core/Entities/TerritorialRentAgreement.cs` | **CREATE** — `Comune`, `Region`, `AgreementName`, `SignedDate`, `EffectiveDate`, `SourceUrl`, `DataCompleteness` |
| `Casazen.Core/Entities/ConcordatoRentBand.cs` | **CREATE** — `TerritorialRentAgreementId` FK, `ZoneName`, `CadastralSheets`, `MinSqm`/`MaxSqm`, sub-fascia min/max ×3 |
| `Casazen.Core/Entities/TerritorialAgreementSignatory.cs` | **CREATE** — `TerritorialRentAgreementId` FK, `Name`, `Role`, `Contact` |
| `Casazen.Core/Entities/HighTensionAreaComune.cs` | **CREATE** — `Comune`, `Region`, `SourceReference`, `VerifiedDirectly` (default false) |
| `Casazen.Core/Entities/Enums/DataCompleteness.cs` | **CREATE** — `Complete`, `Partial`, `Missing` |
| `Casazen.Core/Entities/Enums/LeaseEventType.cs` | **MODIFY** — add `ImuNotificationExported`, `ImuNotificationMarkedSent` (EXISTS enum) |
| `Casazen.Core/Repositories/ITerritorialRentAgreementRepository.cs`, `IHighTensionAreaComuneRepository.cs` | **CREATE** |
| `Casazen.Infrastructure/Repositories/TerritorialRentAgreementRepository.cs`, `HighTensionAreaComuneRepository.cs` | **CREATE** (EF Core) |
| `Casazen.Core/Services/ICanoneConcordatoEligibilityService.cs` | **CREATE** |
| `Casazen.Infrastructure/Services/CanoneConcordatoEligibilityService.cs` | **CREATE** |
| `Casazen.Core/Services/IAttestationGuidanceService.cs` | **CREATE** |
| `Casazen.Infrastructure/Services/AttestationGuidanceService.cs` | **CREATE** |
| `Casazen.Core/Services/IComuneImuNotificationService.cs` | **CREATE** |
| `Casazen.Infrastructure/External/ComuneImuNotificationService.cs` | **CREATE** — **new** real `%PDF` generation (do **not** reuse `LeaseContractTemplateService` UTF-8 stub) |
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

**Complexity:** L — keep one frozen spec; unfreeze as **one epic, two issues**:
- **Slice A** (calculator): AC1–5, AC7, AC10–13 — reference data + eligibility + attestation + FE calculator/empty state. No PDF engine.
- **Slice B** (IMU pack): AC8–9 — real IMU `%PDF` + `LeaseEventType` IMU pair, after RLI can put a lease in `Registered`.
**Migration:** yes — new tables, seeded reference data
**Dependencies:** `spec-ltr-rli-registration` (SPEC-ONLY for AC6/AC8/AC9), `spec-ltr-frontend` (shell)
**Repos:** BE + FE

---

## Compliance

- **No new intermediation risk**: this spec adds no filing capability of its own — the RLI submission remains entirely owned by `spec-ltr-rli-registration` (Openapi.it as filing channel, delega gate, CasaZen ≠ *intermediario abilitato* DPR 322/1998). The IMU-notification export (AC8) is explicitly **export/preview only**, never auto-sent, for the same reason.
- **[COUNSEL_REQUIRED]** Confirm Seveso and Cesano Maderno's presence on the official Agenzia delle Entrate / CIPE "alta tensione abitativa" list from a primary source — current seed data (AC2) relies on converging secondary sources, not the Delibera CIPE 87/2003 text itself. Until confirmed, `VerifiedDirectly = false` and ATA `Applies` stays false.
- **[COUNSEL_REQUIRED]** Confirm whether one or two signatory organizations are required for a valid attestazione di conformità in the Monza e Brianza territorial agreement — national law requires "at least one"; local sources describe a bilateral practice. `IAttestationGuidanceService` (AC7) must not encode an unverified rule as fact.
- **[COUNSEL_REQUIRED]** The Cesano Maderno IMU figure used in AC8/export (≈0,78%) is a **derived** value from the 2025 "Altri fabbricati" 1,04% delibera × 75%, not a separately published municipal rate — must be labeled `valore derivato` + tax year. 2026 Cesano delibera was not published at research date (research §11 #8).
- **[COUNSEL_REQUIRED]** Confirm the Monza e Brianza territorial agreement is still in force at seed time (in force 2024-05-01, nominally 18 months — research §11 #4). Do not treat `DataCompleteness = Complete` as product truth until this is verified.
- **[COUNSEL_REQUIRED]** Confirm Seveso's formal reception of the MB agreement (research §11 #5 — only Seregno's reception delibera was found).
- **[COUNSEL_REQUIRED]** Confirm which Seveso IMU notification channel is currently valid (email/PEC vs SPID portal — research §11 #7). AC8 must label uncertainty, not pick one as official.
- **Disclaimer discipline**: every fiscal-benefit output (AC3) carries a non-binding "informativa, non consulenza fiscale" string, matching the SPEC-ONLY `ICedolareAdvisoryService` convention (`spec-ltr-rli-registration` AC4) — this spec does not introduce a second disclaimer wording.
- **Data provenance**: every `TerritorialRentAgreement`/`HighTensionAreaComune` row must retain its `SourceUrl`/`SourceReference` and `DataCompleteness`, so the UI can show "ultima verifica" per comune (mitigates the "obsolescence" risk flagged in `Sessions/business-analysis-canone-concordato.md` §4).
- **GDPR**: no new personal-data categories; existing `LeaseContract`/`Party` retention and erasure rules are unaffected.

---

## Dependencies

- **Requires**: `LeaseContract.FiscalRegime` incl. `CanoneConcordato` (EXISTS); `Property.City` (EXISTS); `ILeaseTemplateService`/`LeaseContractTemplateService` (EXISTS as stub); the counsel-reviewed template-variant mechanism and `ICedolareAdvisoryService` disclaimer pattern (both SPEC-ONLY from `spec-ltr-rli-registration` AC3/AC4 — that spec must land before AC6 is wired); the `long-rent` context/RBAC scaffolding (`lease.read` / `lease.register`, EXISTS); `spec-tenant-boundary` `OrgId` invariant (EXISTS, RF1).
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

- [COUNSEL_REQUIRED] Primary-source confirmation of Seveso/Cesano Maderno on the official ATA list — hard-gates `VerifiedDirectly` / ATA `Applies` (see Compliance).
- [COUNSEL_REQUIRED] Number of signatory organizations required for a valid attestazione di conformità in the MB territorial agreement (see Compliance).
- [COUNSEL_REQUIRED] Labeling requirement for the Cesano Maderno derived IMU value + tax year (see Compliance).
- [COUNSEL_REQUIRED] MB territorial agreement still in force at seed time (research §11 #4).
- [COUNSEL_REQUIRED] Seveso formal reception of the MB agreement (research §11 #5).
- [COUNSEL_REQUIRED] Seveso IMU notification channel currently valid (research §11 #7).

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
