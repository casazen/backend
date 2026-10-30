# ADR-018: Withholding Tax Regulatory References

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-18

## Context

Italian agencies acting as sostituto d'imposta must withhold 21% tax on owner payments for short-term rentals. Regulatory references may change on 1/1/2027. Some exclusions require legal review.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `Payment.OtaWithholdingTax` exists but is withholding SUFFERED by host on OTA (different)
- No agency withholding entity exists

**Repo scope:** Backend (withholding calculation, F24 generation), Frontend (withholding settings, statement display)

## Decision

### Withholding Calculation Base

Per requirements section 3.7:
- Base = Gross revenue + flat-rate cleaning + flat-rate linen
- Commission added or not per OwnerMandate.CommissionTreatment

### Exclusions (Legal Review Required)

Per Q-A38 answer and section 3.8:
- Deposits MUST be excluded from base
- Down payments MUST be excluded from base
- Penalties MUST be excluded from base

**LEGAL REVIEW GATE:** These exclusions require legal confirmation before production.

### VAT Holder Exemption

Owner with VAT regime (`OwnerTaxRegime.HasVat = true`):
- WithholdingAmount = 0 (no withholding)

### Cross-Year Split

Per Q-A41 answer, bookings spanning calendar years split by:
- Cash year: when payment received
- Stay year: when stay occurred

Two columns in WithholdingStatement for cross-year reporting.

### Regulatory References

Current: D.L. 50/2017 art. 4
Expected 1/1/2027: D.Lgs. 117/2026 artt. 207-208

Per Q-A37 answer, legal review must verify renumbering.

### WithholdingStatement Entity

```csharp
public class OwnerWithholdingStatement : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyOwnerId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public Guid BookingId { get; set; }
    public decimal GrossBase { get; set; }
    public decimal Commission { get; set; }
    public decimal WithholdingAmount { get; set; }  // 21% of base
    public int CashYear { get; set; }
    public int StayYear { get; set; }
}
```

### F24 PDF Generation

Per Q-A39 answer, use official AdE F24 form structure:
- Tax code: 1919
- Reference month/year
- Total for all owners
- Payment deadline: 16th of following month

### AgencyWithholdingSettings

```csharp
public class AgencyWithholdingSettings : ITenantOwned
{
    public Guid OrgId { get; set; }
    public bool IsSubstituteTaxAgent { get; set; }
}
```

### CU and 770 Out of Scope

Per Q-A40 answer, CU (Certificazione Unica) and 770 model are out of phase 1.

### Rejected Alternatives

- **Inline withholding in statement:** Separate entity clearer for cross-year and F24 aggregation
- **Automatic CU/770:** Regulatory complexity, deferred to future phase

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-018-R1 | P0 | PRECONDITION: Legal review MUST confirm deposit/penalty exclusion and regulatory references before production release (gate) |
| ADR-018-R2 | P0 | AgencyWithholdingSettings table (OrgId, IsSubstituteTaxAgent bool, ITenantOwned) |
| ADR-018-R3 | P0 | OwnerWithholdingStatement table (OrgId, OwnerId, Month, BookingId, GrossBase, Commission, WithholdingAmount, CashYear, StayYear, ITenantOwned) |
| ADR-018-R4 | P0 | Withholding base MUST be `GrossRevenue + FlatRateCleaning + FlatRateLinen` per section 3.7, commission added or not per OwnerMandate.CommissionTreatment |
| ADR-018-R5 | P0 | Owner with VAT (OwnerTaxRegime.HasVat = true) MUST have WithholdingAmount = 0 (no withholding) |
| ADR-018-R6 | P0 | Deposit, down payment, penalty MUST be excluded from base (requires legal review confirmation) |
| ADR-018-R7 | P0 | Cross-year booking MUST split by cash year and stay year (two columns if booking spans calendar year) |
| ADR-018-R8 | P0 | F24Service.GenerateF24Pdf MUST use tax code 1919, reference month/year, total for all owners, payment deadline 16th of following month |
| ADR-018-R9 | P0 | GET /api/orgs/me/withholding/f24/{month} MUST return pre-filled F24 PDF with reminder if unreconciled after 16th |
| ADR-018-R10 | P0 | Regulatory references in code comments MUST note 1/1/2027 renumbering (D.L. 50/2017 art. 4 to D.Lgs. 117/2026 artt. 207-208) |
| ADR-018-R11 | P1 | CU and 770 out of phase 1 (documented in ADR Consequences as future work) |

## Consequences

**Follow-on work:**
- Backend: Withholding calculation rules, WithholdingStatement entity, F24 PDF generation, cross-year split logic, payment reconciliation
- Frontend: Org settings withholding section, owner tax regime form, withholding monthly statement display + download, F24 download + payment reminder, payment reconciliation form
- Tests: Money proof tests `WithholdingTax_CalculationBase_21Percent`, `CrossYearBooking_WithholdingSplit`

**Pre-launch gates:**
- Legal review: deposit/penalty exclusion confirmation
- Legal review: F24 template compliance
- Legal review: regulatory reference verification (D.L. 50/2017 renumbering)

**Future work:**
- CU (Certificazione Unica) generation
- 770 model generation

**Risks:**
- Regulatory renumbering on 1/1/2027 requires code comment update
- Deposit/penalty treatment is legal question (gate before production)

**CI cost:** +5s for withholding calculation proof tests
