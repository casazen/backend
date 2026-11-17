# ADR-033: Octorate Booking Import

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-72 (booking import), NF-74 (amounts), NF-75 (calendar), NF-76 (search)

## Context

Bookings from OTAs flow through Octorate to CasaZen. Import includes OTA amounts (commission, net, paid, balance), timestamps with timezone conversion, and guest data with CRM matching.

**Existing code:**
- `Booking` entity exists with basic fields
- `Guest` entity with CRM matching (ADR-021)
- No Octorate booking integration

**Repo scope:** Backend (import job, amount storage), Frontend (booking display with OTA amounts), Mobile (booking detail with amounts)

## Decision

### Booking Import Job

```csharp
public class OctorateBookingImportJob
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var connections = await _db.OctorateConnections.Where(c => c.Status == Connected).ToArrayAsync(ct);
        
        foreach (var conn in connections)
        {
            var since = conn.LastBookingSyncAt ?? DateTime.UtcNow.AddDays(-30);
            var reservations = await _octorate.GetReservationsAsync(since, ct);
            
            foreach (var res in reservations)
            {
                var existing = await _db.Bookings
                    .Where(b => b.OrgId == conn.OrgId && b.OctorateReservationId == res.Id)
                    .SingleOrDefaultAsync(ct);
                    
                if (existing != null)
                {
                    // Update state and amounts
                    existing.Status = MapStatus(res.Status);
                    existing.OtaCommission = res.Commission;
                    existing.OtaNetAmount = res.NetAmount;
                    existing.OtaPaidAmount = res.PaidAmount;
                    existing.OtaBalanceAmount = res.BalanceAmount;
                }
                else
                {
                    // Create new booking
                    var property = await ResolvePropertyAsync(res.PropertyRef, ct);
                    var guest = await MatchOrCreateGuestAsync(res.GuestInfo, ct);
                    
                    _db.Bookings.Add(new Booking
                    {
                        OrgId = conn.OrgId,
                        PropertyId = property.Id,
                        GuestId = guest.Id,
                        OctorateReservationId = res.Id,
                        OtaSource = MapOtaSource(res.Channel),
                        CheckInLocal = ConvertToPropertyTimezone(res.CheckInUtc, property.Timezone),
                        CheckOutLocal = ConvertToPropertyTimezone(res.CheckOutUtc, property.Timezone),
                        OtaCommission = res.Commission,
                        OtaNetAmount = res.NetAmount,
                        OtaPaidAmount = res.PaidAmount,
                        OtaBalanceAmount = res.BalanceAmount
                    });
                    
                    _events.Record(DomainEventType.BookingImported, res.Id, 
                        new { res.Channel, res.Commission, res.NetAmount });
                }
            }
            
            conn.LastBookingSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

Hangfire daily at 02:00 Europe/Rome.

### OTA Amount Fields

```csharp
public class Booking
{
    // ... existing fields ...
    
    public string? OctorateReservationId { get; set; }  // unique per org
    public OtaSource? OtaSource { get; set; }           // Airbnb, Booking, Expedia, BookingEngine
    
    // OTA amounts - immutable after import (read from Octorate)
    public decimal? OtaCommission { get; set; }         // Channel fee
    public decimal? OtaNetAmount { get; set; }          // Host receives
    public decimal? OtaPaidAmount { get; set; }         // Guest paid
    public decimal? OtaBalanceAmount { get; set; }      // Remaining
}

public enum OtaSource { Airbnb, Booking, Expedia, Vrbo, BookingEngine, Direct }
```

Unique index on `(OrgId, OctorateReservationId)` where not null.

### OTA Amounts Immutability

OTA amounts are read-only after import (Octorate is source of truth):

```csharp
public async Task UpdateBookingAsync(Guid bookingId, BookingUpdateDto update, CancellationToken ct)
{
    var booking = await _db.Bookings.FindAsync(bookingId, ct);
    
    if (booking.OctorateReservationId != null)
    {
        // Check for OTA amount modifications
        if (update.OtaCommission != null || update.OtaNetAmount != null)
        {
            throw new ValidationException("ota_amounts_readonly",
                _localizer["booking.error.otaAmountsReadonly"]);
        }
    }
    
    // Update allowed fields
    booking.Notes = update.Notes;
    // ...
}
```

### Timezone Conversion

```csharp
public DateTime ConvertToPropertyTimezone(DateTime utc, string timezone)
{
    var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
    return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
}
```

Italian properties: Europe/Rome. Swiss properties: Europe/Zurich.

### Guest CRM Matching

Per ADR-021: match by email, create if not found.

```csharp
public async Task<Guest> MatchOrCreateGuestAsync(OctorateGuestInfo info, CancellationToken ct)
{
    var existing = await _db.Guests
        .Where(g => g.Email == info.Email.ToLowerInvariant())
        .FirstOrDefaultAsync(ct);
        
    if (existing != null)
        return existing;
        
    var guest = new Guest
    {
        Email = info.Email.ToLowerInvariant(),
        Name = info.Name,
        Phone = info.Phone,
        ConsentBasis = ConsentBasis.Service  // GDPR art. 6.1.b per gdpr.md
    };
    
    _db.Guests.Add(guest);
    return guest;
}
```

### Rejected Alternatives

- **Per-reservation sync:** Too many API calls; daily batch is efficient
- **Editable OTA amounts:** Source of truth is Octorate; manual edits would cause discrepancies
- **Single timezone (Europe/Rome):** Swiss properties need Europe/Zurich; configurable per property

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-033-R1 | P0 | `Booking.OctorateReservationId` varchar(50) nullable with unique index (OrgId, OctorateReservationId) where not null; duplicate import returns 409 `booking_already_imported` |
| ADR-033-R2 | P0 | OTA amount fields: `OtaCommission`, `OtaNetAmount`, `OtaPaidAmount`, `OtaBalanceAmount` all decimal(10,2) nullable |
| ADR-033-R3 | P0 | OTA amounts MUST be immutable after import; `PUT /api/bookings/{id}` returns 422 `ota_amounts_readonly` if OTA fields modified |
| ADR-033-R4 | P0 | `OctorateBookingImportJob` (Hangfire daily 02:00 Europe/Rome) MUST call Octorate reservations API, upsert bookings by unique key |
| ADR-033-R5 | P0 | Booking import MUST convert UTC timestamps to property timezone (`Property.Timezone` field) |
| ADR-033-R6 | P0 | Booking import MUST match or create Guest (ADR-021 CRM matching by email); consent basis = Service (GDPR art. 6.1.b) |
| ADR-033-R7 | P0 | Booking import MUST log `BookingImported` event (BookingId, OctorateReservationId, OtaSource, Amounts) for audit; retain 10 years (fiscal) |
| ADR-033-R8 | P0 | Booking detail page MUST display OTA amounts section when `source='octorate'`: Commission, Net, Paid, Balance |
| ADR-033-R9 | P0 | Booking list MUST show "OTA" badge on imported bookings |
| ADR-033-R10 | P0 | Mobile booking detail MUST show OTA amounts section with same fields |
| ADR-033-R11 | P1 | Proof test `OctorateBookingImport_Amounts_CorrectToCent`: verify amounts match exactly, no rounding error |
| ADR-033-R12 | P1 | Proof test `OctorateBookingImport_UniqueKey_NoDuplicates`: verify duplicate import creates no duplicate rows |

## Consequences

**Follow-on work:**
- Backend: `Booking` migration for OTA fields, import job, guest matching, audit logging
- Frontend: Booking detail with OTA amounts, "OTA" badge in list
- Mobile: Booking detail with OTA amounts section
- Tests: Amount calculation proof test, duplicate prevention proof test

**Risks:**
- Import volume: 10k bookings/year at 100 req/min = 100 minutes; acceptable for daily batch
- Amount currency: assumed EUR; if multi-currency, need Currency field
- Guest CRM matching: if email changes in OTA, duplicate guest entry; acceptable per ADR-021

**CI cost:** +10s for proof tests
