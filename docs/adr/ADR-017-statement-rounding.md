# ADR-017: Monthly Owner Statement Rounding

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-17

## Context

Monthly owner statements must calculate revenue, commission, expenses, withholding, and co-owner splits with precise rounding. Rounding errors can accumulate across many line items.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `Payment.cs` uses decimal for amounts (no rounding in DB)
- No owner statement entity exists

**Repo scope:** Backend (statement calculation, rounding logic), Frontend (statement CRUD, export)

## Decision

### Round Once at Total

Per O-adr-lead-be-3 resolution, round once at statement total (not per line):

- Line items store full precision decimal(18,2)
- Total computed as `Sum(GrossRevenue - Commission - Expenses - Withholding)`
- Total rounded ONCE to 2 decimals using banker's rounding

### Banker's Rounding (Half-to-Even)

Per Q-A31 answer:
```csharp
Math.Round(value, 2, MidpointRounding.ToEven)
```

Examples:
- 100.005 rounds to 100.00
- 100.015 rounds to 100.02

### Co-Owner Split

Per Q-A32 answer, last owner gets remainder:

```csharp
var splits = new decimal[owners.Length];
var total = statement.Net;
for (int i = 0; i < owners.Length - 1; i++)
{
    splits[i] = Math.Round(owners[i].SharePercent / 100m * total, 2, MidpointRounding.ToEven);
}
splits[^1] = total - splits.Take(owners.Length - 1).Sum();
```

Example: EUR 100.03, three owners 33.33% each = 33.33 + 33.33 + 33.37 = 100.03

### Statement Calculation

```csharp
public class OwnerStatementLine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid StatementId { get; set; }
    public Guid BookingId { get; set; }
    public decimal GrossRevenue { get; set; }  // Full precision
    public decimal Commission { get; set; }
    public decimal Expenses { get; set; }
    public decimal Withholding { get; set; }
    public OwnerStatementLineType Type { get; set; }
}

public class OwnerStatement : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyOwnerId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Net { get; set; }  // Rounded total
    public OwnerStatementStatus Status { get; set; }  // Draft | Published
}
```

### Adjustment Rows

Per Q-A36 answer, corrections via manual adjustment rows:
- `OwnerStatementLineType.Adjustment`
- Created in next month's statement if published statement needs correction
- Reason required

### OTA CSV Import

Per Q-A33 answer, import format:
- Columns: BookingId, OtaGross, OtaCommission, NetCollected
- Delimiter: comma
- Encoding: UTF-8

### Statement Export

Formats: PDF, CSV, Excel
- PDF shows rounding disclosure if co-owner remainder != 0
- "Last owner adjusted for rounding"

### Rejected Alternatives

- **Round per line then sum:** Risk of total mismatch due to accumulation
- **Automatic adjustment rows:** Less control, harder audit trail

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-017-R1 | P0 | OwnerStatementLine amounts (GrossRevenue, Commission, Expenses, Withholding) MUST be decimal(18,2) stored without intermediate rounding |
| ADR-017-R2 | P0 | OwnerStatement.Net MUST be computed as `Sum(GrossRevenue - Commission - Expenses - Withholding)` rounded ONCE to 2 decimals with `MidpointRounding.ToEven` |
| ADR-017-R3 | P0 | Co-owner split MUST round each owner's `Net * SharePercent / 100` individually to 2 decimals |
| ADR-017-R4 | P0 | Co-owner split: last owner MUST receive remainder (`total - sum(other splits)`) to preserve total exactly to cent |
| ADR-017-R5 | P0 | Adjustment row (OwnerStatementLine.Type = Adjustment) MUST be created manually in next month if published statement needs correction |
| ADR-017-R6 | P0 | OTA CSV import MUST accept columns BookingId, OtaGross, OtaCommission, NetCollected; comma delimiter; UTF-8 |
| ADR-017-R7 | P1 | Statement PDF MUST show co-owner split with disclosure "Last owner adjusted for rounding" if remainder != 0 |
| ADR-017-R8 | P1 | PROOF TEST: statement with 3 co-owners (33.33% each), EUR 100.03 total, splits 33.33 + 33.33 + 33.37 sum exactly |

## Consequences

**Follow-on work:**
- Backend: OwnerStatement entity + calculation, OTA amounts (manual entry + CSV import), Stripe fee integration, co-owner split, PDF/CSV/Excel export, adjustment rows
- Frontend: Statement creation UI, list and detail, draft editing, approve/publish actions, OTA amounts form + CSV upload, export buttons, adjustment row UI
- Tests: Money proof tests `StatementCalculation_RoundsToCent_HalfToEven`, `CoOwnerSplit_SumsToTotal_LastOwnerRemainder`

**Pre-launch gate:**
- Statement PDF template legal/fiscal compliance review

**Risks:**
- Float precision: intermediate calculations must use decimal, not double
- Adjustment rows only after publication (no inline correction)

**CI cost:** +5s for money proof tests
