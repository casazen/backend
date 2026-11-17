# ADR-037: Tourist Tax Calculation

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-46 (tax calculation)

## Context

Italian municipalities charge tourist tax per person per night with exemptions and caps. Tax rates must come from database (never hardcoded). Octorate write is conditional on Q-A25 (pending Q-004 intake question).

**Existing code:**
- `src/api/tourist-tax.api.ts` exists for tax display
- `src/features/compliance/activation-wizard.tsx` has tax step
- No calculation logic, no municipality rate storage

**Repo scope:** Backend (tax calculation, rate storage, Octorate write), Frontend (rate entry, calculation display), Mobile (tax display in checkout)

## Decision

### Tax Rate Entity

```csharp
public class TouristTaxRate
{
    public Guid Id { get; set; }
    public string ComuneIstatCode { get; set; }         // FK to Comuni
    public string Category { get; set; }                 // standard, luxury, etc.
    public decimal RatePerNight { get; set; }           // EUR, cents
    public int? MaxNightsPerStay { get; set; }          // Per Q-A26: per-stay cap
    public string ExemptionReasonsJson { get; set; }    // Age<18, disability, etc.
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}
```

Note: `TouristTaxRate` is platform reference data (no `ITenantOwned`). Admin-only write endpoint.

### Tax Calculation

Per Q-A26: per-stay cap (not annual tracking).

```csharp
public class TouristTaxCalculator
{
    public TouristTaxResult Calculate(TaxCalculationRequest request)
    {
        var rate = _rates.GetEffectiveRate(request.ComuneCode, request.StayDate);
        if (rate == null)
            return new TouristTaxResult { TaxAmount = 0, RateNotFound = true };
            
        var taxableNights = request.Nights;
        if (rate.MaxNightsPerStay.HasValue)
            taxableNights = Math.Min(taxableNights, rate.MaxNightsPerStay.Value);
            
        var exemptGuests = CountExemptGuests(request.Guests, rate.ExemptionReasonsJson);
        var taxableGuests = request.Guests.Count - exemptGuests;
        
        var totalTax = taxableNights * taxableGuests * rate.RatePerNight;
        
        return new TouristTaxResult
        {
            TaxAmount = Math.Round(totalTax, 2),
            Nights = taxableNights,
            TaxableGuests = taxableGuests,
            ExemptGuests = exemptGuests,
            RateApplied = rate.RatePerNight,
            CapApplied = rate.MaxNightsPerStay.HasValue && request.Nights > rate.MaxNightsPerStay.Value
        };
    }
}
```

### Exemption Reasons

```csharp
public enum TaxExemptionReason
{
    AgeUnder18,
    AgeOver70,
    Disability,
    Worker,              // Lavoro (business travel)
    Medical,             // Cure mediche
    Accompanier,         // Accompagnatore disabile
    // ... per municipality
}
```

Mapped to Octorate exemption codes when writing.

### Double-Charge Prevention

```csharp
public async Task<decimal> CalculateTaxAsync(Booking booking, CancellationToken ct)
{
    // Check if Octorate already includes tax in invoice
    if (booking.OctorateIncludesTax)
    {
        _logger.LogInformation("Tax already included by Octorate for booking {Id}", booking.Id);
        return 0;  // Don't add again
    }
    
    return _calculator.Calculate(new TaxCalculationRequest
    {
        ComuneCode = booking.Property.ComuneIstatCode,
        Nights = booking.Nights,
        Guests = await GetGuestsAsync(booking.Id, ct),
        StayDate = booking.CheckInLocal
    }).TaxAmount;
}
```

### Octorate Write (Conditional)

Per Q-A25: proceed without Octorate write until Q-004 confirmed.

```csharp
public async Task WriteTaxToOctorateAsync(Booking booking, TouristTaxResult tax, CancellationToken ct)
{
    if (!_featureFlags.IsEnabled("octorate_tax_write"))
    {
        _logger.LogInformation("Octorate tax write disabled (Q-A25 pending)");
        return;
    }
    
    await _octorate.UpdateCheckinAsync(booking.OctorateReservationId, new
    {
        cityTaxPrice = tax.TaxAmount,
        cityTaxExemption = MapExemptions(tax.ExemptGuests),
        excludeCityTax = false
    }, ct);
}
```

### Audit Logging

```csharp
public async Task LogTaxCalculationAsync(Booking booking, TouristTaxResult tax, CancellationToken ct)
{
    _events.Record(DomainEventType.TouristTaxCalculated, booking.Id, new
    {
        booking.PropertyId,
        tax.Nights,
        tax.TaxableGuests,
        tax.ExemptGuests,
        tax.RateApplied,
        tax.TaxAmount,
        tax.CapApplied
    });
    
    // Keep 10 years for fiscal audit
}
```

### Rejected Alternatives

- **Annual cap tracking:** Complex; per-stay is typical Italian ordinance structure (Q-A26)
- **Hardcoded rates:** Compliance violation; must be database per compliance.md
- **Always write to Octorate:** Q-A25 pending; feature flag controls write

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-037-R1 | P0 | `TouristTaxRate` table (ComuneIstatCode, Category, RatePerNight decimal(10,2), MaxNightsPerStay nullable, ExemptionReasonsJson, ValidFrom, ValidTo nullable); NEVER hardcoded |
| ADR-037-R2 | P0 | Tax calculation formula: `Tax = min(Nights, MaxNightsPerStay) * TaxableGuests * RatePerNight`; MUST be correct to the cent |
| ADR-037-R3 | P0 | Double-charge prevention: if `Booking.OctorateIncludesTax = true`, CasaZen MUST NOT add tax to total |
| ADR-037-R4 | P0 | Tax calculation MUST log `TouristTaxCalculated` event (BookingId, Amounts, ExemptionsApplied); retain 10 years (fiscal) |
| ADR-037-R5 | P0 | Tax calculation wizard MUST include municipality rate entry: rate per night, exemptions, night cap |
| ADR-037-R6 | P0 | Tax summary display MUST show breakdown: nights, guests, exempt count, rate, cap applied, total |
| ADR-037-R7 | P0 | Mobile checkout wizard MUST display calculated tax amount per guest with collection method choice |
| ADR-037-R8 | P1 | Octorate tax write (feature flag `octorate_tax_write` default OFF): if enabled, write `cityTaxPrice`, `cityTaxExemption` to Octorate guest record |
| ADR-037-R9 | P1 | Proof test `TouristTax_Calculation_CorrectToCent`: verify calculation with edge cases (0 nights, cap applied, all exempt) |
| ADR-037-R10 | P1 | Proof test `TouristTax_NeverDouble_WhenOctorateIssues`: verify tax not added when Octorate includes it |

## Consequences

**Follow-on work:**
- Backend: `TouristTaxRate` entity, tax calculator, double-charge check, Octorate write (behind flag), audit logging
- Frontend: Rate entry wizard, tax summary display
- Mobile: Tax display in checkout (already partially implemented)
- Tests: Tax calculation proof tests (money rules)

**Tier 2 priority:** This is a Tier 2 ADR. Tax calculation is important for compliance but can proceed without Octorate write.

**Assumptions pending confirmation (Q-A25):** Octorate tax write depends on Q-004 intake answer. Feature flag controls write until confirmed.

**Risks:**
- Tax rate changes mid-booking: recalculate on checkout date (document in tax logic)
- Q-A25 open: can implement calculation without Octorate write; add write path later

**CI cost:** +5s for tax calculation proof tests
