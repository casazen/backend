# Spec — EU Compliance Modules: Spain & France (US-017)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Take CasaZen's Italian compliance moat international by adding **Spain (ES)** and
**France (FR)** compliance modules that replicate the Italian pattern across three pillars:
**STR registration**, **local tourist tax**, and **lease law** — plus per-market guest
reporting. The IT implementation (CIN, Alloggiati Web, *imposta di soggiorno*, GDPR + the
LTR lease/RLI engine) is the **reference implementation**; this spec **abstracts the
compliance services behind market-agnostic interfaces** so a new market plugs in by adding
a strategy implementation + DB-driven rates, never by forking core logic.

This is a breadth-oriented Phase 4 spec. The architectural deliverable (the pluggable
compliance abstraction) is concrete; the per-market regulatory specifics require counsel.

Reference: **US-017** (Phase 4 — Scale + EU expansion; draft-v3 §B Phase 4 + §C row `spec-eu-compliance-es-fr`)
Stage of entry: **Stage 01 Planning** (epic-level macro-spec; splits into issues at Stage 02)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As an **operator with properties in Spain and/or France**, I want CasaZen to enforce local
STR registration, local tourist tax, and lease-law obligations exactly as it does for Italy,
so that I can operate compliantly in multiple markets from one platform.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: Compliance is resolved **per market** from `Property.CountryCode`
  (`IT | ES | FR`); a `IComplianceMarketResolver` selects the market strategy. Unknown/unsupported
  countries fail closed (compliance required, not silently skipped).

- **AC2**: **Registration validation is abstracted** behind `IRegistrationNumberValidator`
  with `IT` (existing CIN `IT-XXXXX-XXXXXXXXXX`) as reference + new `ES` and `FR` validators.
  The IT validator's behavior is unchanged (no regression).

- **AC3**: **Guest reporting is abstracted** behind `IGuestReportingService` (today's
  `IAlloggiatiWebService` becomes the IT implementation); ES and FR implementations are
  registered and selected by market. Reporting jobs are market-aware.

- **AC4**: **Tourist tax is market-driven** — `TouristTaxRate` rates remain DB-driven and
  **never hardcoded**; ES (*tasa turística* / regional) and FR (*taxe de séjour*) rate sets
  and calculation rules are added per municipality/region without code changes to the engine.

- **AC5**: **Lease registration is abstracted** behind the existing
  `ILeaseRegistrationService` (Openapi.it RLI = IT reference); ES/FR lease-registration
  strategies plug in per market (assisted/operator-attended, mirroring `spec-ltr-rli-registration`).

- **AC6**: A property in ES or FR triggers the **correct market's** registration validation,
  guest-reporting flow, and tourist-tax calculation end-to-end; an IT property is completely
  unaffected (parallel regression suite per market).

- **AC7**: Per-market **data-residency** configuration is recorded and surfaced (where guest/
  tenant PII for a given market is processed/stored), feeding the GDPR/DPA disclosure.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: Compliance UI is **market-aware**: labels and regulatory terms render in the
  market's language/term (IT *CIN*/*Alloggiati*, ES *registro*/*tasa turística*,
  FR *numéro d'enregistrement*/*taxe de séjour*) driven by the property's market.

- **AC9**: Per-market **registration badge** (valid / missing / invalid) reuses the property
  CIN-badge pattern, generalized to the resolved market's registration scheme.

- **AC10**: Tourist-tax display reflects the property's market rate and label; no IT-specific
  strings leak into ES/FR views.

- **AC11**: Existing IT compliance views are unchanged for IT properties (visual regression).

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `eu-compliance-es-fr`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Compliance is resolved **per market** from `Property.CountryCode` | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | **Registration validation is abstracted** behind `IRegistrationNumberValidator` | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | **Guest reporting is abstracted** behind `IGuestReportingService` (today's | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | **Tourist tax is market-driven** — `TouristTaxRate` rates remain DB-driven and | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | **Lease registration is abstracted** behind the existing | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | A property in ES or FR triggers the **correct market's** registration validation, | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | Per-market **data-residency** configuration is recorded and surfaced (where guest/ | Outcome not met; wrong status; silent no-op |
| AC8 | L2 + L3 | Compliance UI is **market-aware**: labels and regulatory terms render in the | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | Per-market **registration badge** (valid / missing / invalid) reuses the property | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | Tourist-tax display reflects the property's market rate and label; no IT-specific | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Existing IT compliance views are unchanged for IT properties (visual regression). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Services/IComplianceMarketResolver.cs` + impl | Create (new module) — resolve market from `Property.CountryCode` |
| `Casazen.Core/Services/IRegistrationNumberValidator.cs` | Create (new module) — abstract registration validation |
| `Casazen.Core/Validation/CinCodeAttribute.cs` | Modify — refactor IT logic behind the `IT` validator strategy (behavior preserved) |
| `Casazen.Infrastructure/Compliance/EsRegistrationValidator.cs` + `FrRegistrationValidator.cs` | Create (new module) — ES/FR registration formats |
| `Casazen.Core/Services/IGuestReportingService.cs` | Create (new module) — abstract guest reporting |
| `Casazen.Core/Services/IAlloggiatiWebService.cs` | Modify — becomes the `IT` `IGuestReportingService` implementation |
| `Casazen.Infrastructure/Compliance/EsGuestReportingService.cs` + `FrGuestReportingService.cs` | Create (new module) — ES/FR guest reporting |
| `Casazen.Web/BackgroundJobs/AlloggiatiWebReportJob.cs` | Modify — generalize to market-aware guest-reporting job dispatch |
| `Casazen.Infrastructure/Services/TouristTaxService.cs` + `Casazen.Core/Entities/TouristTaxRate.cs` | Modify — per-market rate sets + calculation (DB-driven, ES/FR) |
| `Casazen.Core/Services/ILeaseRegistrationService.cs` | Modify — market strategy seam (Openapi.it RLI = IT) |
| `Casazen.Infrastructure/Compliance/EsLeaseRegistrationService.cs` + `FrLeaseRegistrationService.cs` | Create (new module) — ES/FR lease registration (assisted) |
| `Casazen.Core/Entities/Property.cs` | Modify — ensure `CountryCode` drives market resolution |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — market config + ES/FR rate/format seeds |
| `Casazen.Infrastructure/Migrations/` | Create — migration `AddEsFrComplianceModules` (rebase on `AppDbContextModelSnapshot.cs`) |
| `Casazen.Web/Program.cs` | Modify — register market strategies (DI) + market-aware reporting jobs |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — bind per-market compliance service implementations |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/compliance/components/market-registration-badge.tsx` | Create (new module) — generalizes the CIN badge per market |
| `src/features/compliance/components/market-tax-display.tsx` | Create (new module) — per-market tourist-tax label/rate |
| `src/features/properties/components/property-cin-badge.tsx` | Modify — delegate to market-aware badge (IT unchanged) |
| `src/i18n/compliance.{it,es,fr}.ts` | Create (new module) — regulatory term strings per market |
| `src/api/compliance.api.ts` | Create (new module) — market-aware compliance calls |
| `src/queries/use-compliance.ts` | Create (new module) — TanStack Query hooks |
| `src/types/compliance.types.ts` | Create (new module) — market + registration/tax DTOs |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **ES STR registration + local tourist tax + lease law**: regional registration schemes,
  *tasa turística*, and Spanish lease-law obligations to be confirmed per autonomous
  community. **[COUNSEL_REQUIRED]** (ES).
- **FR STR registration + local tourist tax + lease law**: *numéro d'enregistrement* /
  *déclaration en mairie*, *taxe de séjour*, and French lease-law obligations to be confirmed
  per commune. **[COUNSEL_REQUIRED]** (FR).
- **Per-market data residency**: where ES/FR guest/tenant PII is processed and stored, fed
  into the GDPR/DPA subprocessor disclosure. **[COUNSEL_REQUIRED]** per market.
- **IT remains the reference implementation**: CIN (D.L. 145/2023), Alloggiati Web,
  *imposta di soggiorno*, GDPR, and LTR lease/RLI behavior are unchanged — abstraction must
  not regress the IT path (AC2/AC6/AC11).

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: existing **IT compliance services** as the reference implementation —
  `CinCodeAttribute`, `IAlloggiatiWebService` + `AlloggiatiWebReportJob`, `TouristTaxService`
  + `TouristTaxRate`, `GdprService`, and the LTR `ILeaseRegistrationService` (Openapi.it RLI);
  localized regulatory research (Legal) per market.
- **Blocks**: the Phase 4 exit criterion "first non-Italian market live with native compliance".
- **Related**: `spec-tenant-boundary` (`OrgId` scoping, RF1); `spec-saas-billing` (IVA/OSS
  per market); `spec-enterprise-scale` (sibling Phase 4 spec); `spec-ltr-rli-registration`
  (assisted/operator-attended registration pattern reused per market).

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
