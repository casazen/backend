---
id: COMP-003
slug: spec-regime-fiscale-2026
title: Regime fiscale STR / cedolare secca 2026
phase: compliance
type: compliance
priority: P1
status: planned
issue: 3
depends_on: []
blocks: []
exit_contributes_to: Owner can configure 2026 STR fiscal regime, record OTA 21% withholding, and export a commercialista-ready CSV/PDF pack
last_reviewed: 2026-08-13
---

# Spec — Regime fiscale STR / cedolare secca 2026 (#3)

> Template: `Sessions/specs/_TEMPLATE.md`. Process gates: Stage 02 G9b + Stage 03/04 `check-ac-depth.ps1`.

## Overview

Italian STR hosts need to apply L. 199/2025 / D.L. 50/2017 inside CasaZen: cedolare 21%/26% on at most two STR properties per tax year, Partita IVA from the third, OTA 21% withholding as an acconto, and year-end CSV/PDF packs for the commercialista. CasaZen does not file taxes.

**Done means:** an owner can complete regime assignment and download CSV+PDF packs that a commercialista can open and read (labeled columns/sections, disclaimer, no tax-due engine) — not only that UI chrome exists.

**Phase:** compliance · **Type:** compliance · **Status:** planned · **Issue:** [#3](https://github.com/casazen/backend/issues/3)

Design: `Sessions/design-3.md`. ADRs 001–003 do not inform this spec.

---

## User Story

As a property-owner, I want to assign the 2026 fiscal regime to each STR property, record OTA 21% withholding, and export yearly reports, so that I can apply L. 199/2025 and D.L. 50/2017 and give complete figures to my commercialista.

---

## Acceptance Criteria

Mapped 1:1 to GitHub #3 (`SPEC:regime-fiscale-2026:AC1`–`AC16`).

### Backend

- **AC1**: Given 1 active STR property for tax year 2026, When `GET /api/fiscal/regime?taxYear=2026`, Then `recommendedRegime=CedolareSecca21` and disclaimer contains informativa / non-consulenza language.
- **AC3**: Given 1 STR property, When `PUT` regime `CedolareSecca26`, Then **400**.
- **AC4**: Given 3 active STR properties, When reading regime, Then `requiresPartitaIva=true`; assigning cedolare on a third property returns **409**; creating the property is not blocked.
- **AC6**: Given OTA-sourced booking (Airbnb/BookingCom/…), When creating Payment, Then store gross, 21% withholding, net, `WithholdingTaxApplied=true`.
- **AC7**: Given Direct/Local booking, When creating Payment, Then do not auto-apply 21%; optional manual withholding allowed.
- **AC11**: Threshold counts only active STR properties in that tax year (not inactive, not LTR-only).
- **AC12**: Cross-org fiscal reads/writes return **404**; OrgId never client-supplied.

### Frontend

- **AC2**: Given 2 STR properties, When owner designates primary, Then primary shows 21% and the other 26%; only one primary per org+tax year.
- **AC4** (UI): Given `requiresPartitaIva`, Then alert «P.IVA obbligatoria» and wizard entry are visible and actionable in Italian.
- **AC5**: Given `hasPartitaIva=true`, Then owner can set Ordinario or Forfettario per property and save succeeds.
- **AC8**: Withholding report by OTA source shows per-source gross / withholding / net (labels only).
- **AC9**: Annual income report shows labeled totals without tax-due / IRPEF fields.
- **AC10**: Owner can download CSV **and** PDF commercialista packs for annual and withholding reports meeting **Export / Report Criteria** below.
- **AC13**: Italian copy citing L. 199/2025 and D.L. 50/2017; CasaZen does not file taxes / replace a commercialista.
- **AC14** (UX): Happy path “open fiscal → set regime on one property → open reports → download CSV” is completable without dead-ends; primary CTAs in Italian.
- **AC15** (UX): Empty reports (no payouts) show an Italian empty state, not a blank table.
- **AC16** (UX): API/validation errors surface a human-readable Italian message (no raw JSON / stack).

---


## UX / UI Quality

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Fiscal home → property regime → reports → download | AC14 L3 script |
| Language | Italian labels on primary CTAs and alerts | AC4/AC13/AC14 asserts |
| Empty state | No blank reports page | AC15 |
| Error state | Human Italian message | AC16 |
| Legal copy | Disclaimer always on fiscal + reports | AC13 |

**Happy-path script:**

1. Open `/app/short-rent/fiscal`
2. Confirm disclaimer + property card with recommended regime
3. Save/confirm regime on one property (if editable)
4. Open `/app/short-rent/fiscal/reports`
5. Download annual CSV and PDF; open files (CSV headers; PDF non-empty)

---

## Export / Report Criteria

Applies to **AC8–AC10** (annual + withholding, `format=csv|pdf`).

### CSV

| Column | Required | Notes |
|---|---|---|
| `taxYear` | yes | |
| `propertyName` or `propertyId` | yes | stable id or display name |
| `source` | yes on withholding | OTA / Direct |
| `gross` | yes | decimal |
| `withholding` | yes on withholding | decimal |
| `net` | yes | decimal |
| `packLabel` | yes (header comment or first meta row) | commercialista pack wording |

- Encoding: UTF-8
- Filename: no CF / P.IVA in `Content-Disposition`
- Empty dataset: header row still present; UI empty state per AC15

### PDF

| Requirement | Required |
|---|---|
| Real PDF bytes (`%PDF`), not stub/empty | yes |
| packLabel + legal disclaimer on first page | yes |
| Tabular or clearly labeled sections (property / OTA / totals) | yes |
| No tax-due / IRPEF / F24 computation fields | yes |
| Readable for a commercialista (not debug key dump) | yes |

### JSON

- Same business fields; must not include `taxDue` / `irpef`.

---


## Verifiable Outcomes



**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.



| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |

|---|---|---|---|

| AC1 | L1 | Given 1 active STR property for tax year 2026, When `GET /api/fiscal/regime?taxYear=2026`, Then `recommendedRegime=CedolareSecca21` and d... | Outcome not met; wrong status; silent no-op |

| AC3 | L1 | Given 1 STR property, When `PUT` regime `CedolareSecca26`, Then **400**. | Outcome not met; wrong status; silent no-op |

| AC4 | L2 + L3 | Given 3 active STR properties, When reading regime, Then `requiresPartitaIva=true`; assigning cedolare on a third property returns **409*... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC6 | L1 | Given OTA-sourced booking (Airbnb/BookingCom/…), When creating Payment, Then store gross, 21% withholding, net, `WithholdingTaxApplied=tr... | Outcome not met; wrong status; silent no-op |

| AC7 | L1 | Given Direct/Local booking, When creating Payment, Then do not auto-apply 21%; optional manual withholding allowed. | Outcome not met; wrong status; silent no-op |

| AC11 | L1 | Threshold counts only active STR properties in that tax year (not inactive, not LTR-only). | Outcome not met; wrong status; silent no-op |

| AC12 | L1 | Cross-org fiscal reads/writes return **404**; OrgId never client-supplied. | Outcome not met; wrong status; silent no-op |

| AC2 | L2 + L3 | Given 2 STR properties, When owner designates primary, Then primary shows 21% and the other 26%; only one primary per org+tax year. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC5 | L2 + L3 | Given `hasPartitaIva=true`, Then owner can set Ordinario or Forfettario per property and save succeeds. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC8 | L2 + L3 | Withholding report by OTA source shows per-source gross / withholding / net (labels only). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC9 | L2 + L3 | Annual income report shows labeled totals without tax-due / IRPEF fields. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC10 | L2 + L3 | Owner can download CSV **and** PDF commercialista packs for annual and withholding reports meeting **Export / Report Criteria** below. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC13 | L2 + L3 | Italian copy citing L. 199/2025 and D.L. 50/2017; CasaZen does not file taxes / replace a commercialista. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC14 | L2 + L3 | (UX): Happy path “open fiscal → set regime on one property → open reports → download CSV” is completable without dead-ends; primary CTAs ... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC15 | L2 + L3 | (UX): Empty reports (no payouts) show an Italian empty state, not a blank table. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

| AC16 | L2 + L3 | (UX): API/validation errors surface a human-readable Italian message (no raw JSON / stack). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |



Rules:

- UI ACs need L2 **and** L3 outcomes (titled tests per AC).

- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).

- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/Enums/StrFiscalRegime.cs` | Create — do not change LTR `FiscalRegime` |
| `Casazen.Core/Entities/PropertyFiscalYear.cs` | Create |
| `Casazen.Core/Entities/Org.cs` | Modify — tax profile columns |
| `Casazen.Core/Entities/Payment.cs` | Modify — withholding columns |
| `Casazen.Web/Controllers/FiscalController.cs` | Create |
| `Casazen.Web/Controllers/PaymentsController.cs` | Modify — withholding on create |
| `Casazen.Web/Controllers/GdprController.cs` | Modify — org tax-profile export/anonymize |
| FE `src/features/fiscal/*` | Create/modify — dashboard, wizard, reports |

**Complexity:** L  
**Migration:** yes — see `Sessions/design-3.md`  
**Dependencies:** none  
**Repos:** BE + FE

---

## Test expectations (process contract)

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit asserting outcomes above | Compile-only |
| L2 | Demo Playwright; `test('ACn:…')` per UI AC; mocks OK | One smoke for AC1–AC13; export = button visible |
| L3 | Real API; `test('ACn:…')` per UI AC; download CSV/PDF for AC10 | Mock `/api/fiscal`; single AC1 smoke claimed for all rows |

Design AC Test Map must list **distinct titled tests** per UI AC. `check-ac-depth.ps1 -RequireTests` is mandatory before Stage 03 exit / Stage 04 G11.

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] Exact `DataRetentionUntil` term for tax identifiers (design proposes tax-year-end + 10 years).
- [COUNSEL_REQUIRED] Mid-year 2→3 property transition legal outcome (product: alert + block cedolare on 3rd; do not rewrite prior months).
- [COUNSEL_REQUIRED] Forfettario eligibility is owner+commercialista, not product-guaranteed.

---

## Out of Scope

See GitHub #3: no AdE filing, no official CU, no tax-due engine, no OTA payout adapters, no LTR `LeaseContract.FiscalRegime` change, no native mobile, no admin fiscal stats.

---

## Open Questions

All product questions for v1 resolved in design; counsel items above do not block remediation of UX/export quality but block claiming legal certainty.
