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
exit_contributes_to: Owner can configure 2026 STR fiscal regime, record OTA 21% withholding, and export a commercialista data pack
last_reviewed: 2026-08-13
---

# Spec — Regime fiscale STR / cedolare secca 2026 (#3)

## Overview

Italian STR hosts need to apply L. 199/2025 / D.L. 50/2017 inside CasaZen: cedolare 21%/26% on at most two STR properties per tax year, Partita IVA from the third, OTA 21% withholding as an acconto, and year-end CSV/PDF packs for the commercialista. CasaZen does not file taxes.

**Phase:** compliance · **Type:** compliance · **Status:** planned · **Issue:** [#3](https://github.com/casazen/backend/issues/3)

Design: `Sessions/design-3.md`. ADRs 001–003 do not inform this spec.

---

## User Story

As a property-owner, I want to assign the 2026 fiscal regime to each STR property, record OTA 21% withholding, and export yearly reports, so that I can apply L. 199/2025 and D.L. 50/2017 and give complete figures to my commercialista.

---

## Acceptance Criteria

Mapped 1:1 to GitHub #3 Stage 01 ACs (`SPEC:regime-fiscale-2026:AC1`–`AC13`).

### Backend

- **AC1**: 1 active STR property → recommend `CedolareSecca21` + informativa disclaimer.
- **AC3**: Reject `CedolareSecca26` when the org has only 1 STR property in the tax year.
- **AC4**: 3rd STR property → `requiresPartitaIva`; cedolare assignment on a third property returns 409; creating the property is not blocked.
- **AC6**: Payment on OTA-sourced booking auto-stores gross, 21% withholding, net, `WithholdingTaxApplied=true`.
- **AC7**: Direct/Local/Stripe payout does not auto-apply 21%; optional manual withholding.
- **AC11**: Threshold counts only active STR properties in that tax year (not inactive, not LTR-only).
- **AC12**: Cross-org fiscal reads/writes return 404; OrgId never client-supplied.

### Frontend

- **AC2**: Designate primary → 21%/26% swap; only one primary per org+tax year.
- **AC4** (UI): Alert «P.IVA obbligatoria» + wizard.
- **AC5**: With P.IVA recorded, owner can set Ordinario or Forfettario per property.
- **AC8–AC10**: Withholding report by OTA, annual income report (labels only, no tax due), CSV+PDF commercialista pack.
- **AC13**: Italian copy citing L. 199/2025 and D.L. 50/2017; CasaZen does not file taxes / replace a commercialista.

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

**Complexity:** L  
**Migration:** yes — see `Sessions/design-3.md`  
**Dependencies:** none

---

## Regulatory / Legal Gates

- [COUNSEL_REQUIRED] Exact `DataRetentionUntil` term for tax identifiers (design proposes tax-year-end + 10 years).
- [COUNSEL_REQUIRED] Mid-year 2→3 property transition legal outcome (product: alert + block cedolare on 3rd; do not rewrite prior months).
- [COUNSEL_REQUIRED] Forfettario eligibility is owner+commercialista, not product-guaranteed.

---

## Out of Scope

See GitHub #3: no AdE filing, no official CU, no tax-due engine, no OTA payout adapters, no LTR `LeaseContract.FiscalRegime` change, no native mobile, no admin fiscal stats.
