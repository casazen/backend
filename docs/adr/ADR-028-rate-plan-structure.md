# ADR-028: Rate Plan Structure

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-35 (pricing rules), NF-36 (seasonal rates)

## Context

Rate plans define pricing structure: base rate, extra guest charges, length-of-stay discounts, and deposits. Existing fields are "saved without effect" and need implementation. Rates sync to Octorate which handles OTA distribution.

**Existing code:**
- Extra guest cost field exists but not applied in calculation
- Deposit field exists but not collected
- No length-of-stay discount structure

**Repo scope:** Backend (rate calculation, Octorate sync), Frontend (rate plan editor), Mobile (none - pricing is desktop config)

## Decision

### Rate Plan Entity

```csharp
public class RatePlan : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; }
    public decimal BaseRate { get; set; }               // decimal(10,2)
    public string Currency { get; set; }                // EUR, CHF
    public int MaxGuests { get; set; }                  // included in base rate
    public decimal ExtraGuestCost { get; set; }         // per guest per night
    public int MinNights { get; set; }
    public int? MaxNights { get; set; }
    public decimal? DepositAmount { get; set; }         // nullable = no deposit
    public DepositType DepositType { get; set; }        // FixedAmount, Percentage
}

public class RatePlanDiscount : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid RatePlanId { get; set; }
    public int MinNights { get; set; }
    public int? MaxNights { get; set; }
    public decimal PercentageValue { get; set; }        // 0-100
}

public enum DepositType { FixedAmount, Percentage }
```

### Extra Guest Cost Calculation

Per Q-A19: implement (apply), not remove.

```csharp
public decimal CalculateTotalRate(RatePlan plan, int guests, int nights)
{
    var baseTotal = plan.BaseRate * nights;
    
    var extraGuests = Math.Max(0, guests - plan.MaxGuests);
    var extraGuestTotal = extraGuests * plan.ExtraGuestCost * nights;
    
    var subtotal = baseTotal + extraGuestTotal;
    
    // Apply length-of-stay discount
    var discount = GetApplicableDiscount(plan, nights);
    if (discount != null)
    {
        subtotal -= subtotal * (discount.PercentageValue / 100);
    }
    
    return Math.Round(subtotal, 2);  // Never fractional cents
}
```

### Length-of-Stay Discounts

Per Q-A20: percentage (aligns with Octorate `percentageValue`).

```csharp
public class LengthOfStayDiscountTier
{
    public int MinNights { get; set; }      // e.g., 7
    public int? MaxNights { get; set; }     // e.g., 13, null = unlimited
    public decimal Percentage { get; set; } // e.g., 10 for 10%
}
```

Validation: no overlapping tiers, percentage 0-100.

### Deposit Collection

Per Q-A19: implement deposit collection in booking wizard.

```csharp
public class BookingDeposit
{
    public Guid BookingId { get; set; }
    public decimal Amount { get; set; }
    public DepositStatus Status { get; set; }  // Pending, Collected, Refunded
    public DateTime? CollectedAt { get; set; }
    public string PaymentIntentId { get; set; }  // Stripe
}
```

### Octorate Sync

Rate plans sync to Octorate rate structure:

```csharp
public async Task SyncRatePlanAsync(RatePlan plan, CancellationToken ct)
{
    var octorateRate = new OctorateRateUpdate
    {
        BasePrice = plan.BaseRate,
        ExtraPersonPrice = plan.ExtraGuestCost,
        Discounts = plan.Discounts.Select(d => new OctorateDiscount
        {
            MinNights = d.MinNights,
            PercentageValue = d.PercentageValue
        }).ToArray()
    };
    
    await _octorate.UpdateRateAsync(plan.OctorateRatePlanId, octorateRate, ct);
}
```

### Rejected Alternatives

- **Fixed amount discounts:** Octorate uses percentage; consistency is simpler
- **Remove deposit field:** Requirements say "apply"; implementation needed
- **Per-channel rates:** Deferred; one rate plan syncs to all channels via Octorate

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-028-R1 | P0 | `RatePlan` table (OrgId, PropertyId, Name, BaseRate decimal(10,2), Currency, MaxGuests, ExtraGuestCost decimal(10,2), MinNights, MaxNights nullable, DepositAmount nullable, DepositType, ITenantOwned) |
| ADR-028-R2 | P0 | `RatePlanDiscount` table (RatePlanId, MinNights, MaxNights nullable, PercentageValue decimal(5,2), ITenantOwned) with validation: no overlapping tiers, percentage 0-100 |
| ADR-028-R3 | P0 | Extra guest cost calculation: `TotalCost = (BaseRate * Nights) + (max(0, Guests - MaxGuests) * ExtraGuestCost * Nights) - LengthOfStayDiscount`; MUST be correct to the cent |
| ADR-028-R4 | P0 | Length-of-stay discount MUST apply percentage: `Discount = Subtotal * (PercentageValue / 100)`; MUST round to 2 decimals |
| ADR-028-R5 | P0 | Rate calculation MUST never produce fractional cents; decimal(10,2) enforced at storage |
| ADR-028-R6 | P0 | Rate plan editor MUST include: base rate, max guests, extra guest cost toggle with amount, length-of-stay discount table (up to 5 tiers) |
| ADR-028-R7 | P0 | Deposit implementation: if `DepositAmount` set, booking wizard MUST collect deposit via Stripe PaymentIntent |
| ADR-028-R8 | P1 | Proof test `RatePlan_ExtraGuestCost_CorrectToCent`: verify calculation with edge cases (0 guests, 10 guests, ExtraGuestCost=0) |
| ADR-028-R9 | P1 | Proof test `RatePlan_LengthOfStayDiscount_Percentage`: verify discount application and rounding |
| ADR-028-R10 | P1 | Octorate sync MUST write rate plan changes; Octorate is source of truth for availability/rates after sync |

## Consequences

**Follow-on work:**
- Backend: `RatePlan` and `RatePlanDiscount` entities, rate calculator service, deposit integration with Stripe, Octorate sync
- Frontend: Rate plan editor with discount table, deposit toggle, extra guest cost toggle
- Mobile: None (pricing configuration is desktop)
- Tests: Rate calculation proof tests (money rules)

**Risks:**
- Currency conversion: no auto-conversion; rate plan currency must match booking currency
- Octorate rate sync: if Octorate rejects rate (validation), surface error to user
- Deposit timing: when to collect (at booking, before check-in) needs UX decision (deferred to implementation)

**CI cost:** +5s for rate calculation proof tests
