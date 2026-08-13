using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class GdprService(
    IGuestRepository guestRepository,
    AppDbContext db,
    ILogger<GdprService> logger) : IGdprService
{
    public async Task<Dictionary<string, object>> ExportGuestDataAsync(Guid guestId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId)
            ?? throw new InvalidOperationException($"Guest {guestId} not found");

        return new Dictionary<string, object>
        {
            ["id"] = guest.Id,
            ["firstName"] = guest.FirstName,
            ["lastName"] = guest.LastName,
            ["email"] = guest.Email,
            ["phoneNumber"] = guest.PhoneNumber,
            ["address"] = guest.Address,
            ["city"] = guest.City,
            ["country"] = guest.Country,
            ["consentDate"] = guest.ConsentDate?.ToString("O") ?? "",
            ["consentVersion"] = guest.ConsentVersion,
            ["marketingConsent"] = guest.MarketingConsent,
            ["dataRetentionUntil"] = guest.DataRetentionUntil.ToString("O"),
            ["dataProcessingPurpose"] = guest.DataProcessingPurpose,
            ["bookingCount"] = guest.Bookings.Count,
            ["exportedAt"] = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task DeleteGuestDataAsync(Guid guestId, string reason)
    {
        var guest = await guestRepository.GetByIdAsync(guestId)
            ?? throw new InvalidOperationException($"Guest {guestId} not found");

        guest.IsDeleted = true;
        guest.DeletedAt = DateTime.UtcNow;
        guest.DeletionReason = reason;
        AnonymizeFields(guest);
        await guestRepository.UpdateAsync(guest);
        logger.LogInformation("Guest {GuestId} data deleted, reason: {Reason}", guestId, reason);
    }

    public async Task AnonymizeGuestDataAsync(Guid guestId)
    {
        var guest = await guestRepository.GetByIdAsync(guestId);
        if (guest == null) return;

        AnonymizeFields(guest);
        await guestRepository.UpdateAsync(guest);
        logger.LogInformation("Guest {GuestId} data anonymized (retention period expired)", guestId);
    }

    public async Task UpdateConsentAsync(Guid guestId, bool marketingConsent)
    {
        var guest = await guestRepository.GetByIdAsync(guestId)
            ?? throw new InvalidOperationException($"Guest {guestId} not found");

        guest.MarketingConsent = marketingConsent;
        guest.MarketingConsentDate = DateTime.UtcNow;
        await guestRepository.UpdateAsync(guest);
        logger.LogInformation("Guest {GuestId} marketing consent updated to {Consent}", guestId, marketingConsent);
    }

    private static void AnonymizeFields(Core.Entities.Guest guest)
    {
        var token = $"ANON-{guest.Id:N}";
        guest.FirstName = "ANONYMIZED";
        guest.LastName = "ANONYMIZED";
        guest.Email = $"{token}@deleted.local";
        guest.PhoneNumber = string.Empty;
        guest.Address = string.Empty;
        guest.City = string.Empty;
        guest.PostalCode = string.Empty;
        guest.Country = string.Empty;
        guest.Notes = string.Empty;
        guest.DocumentNumber = string.Empty;
        guest.PlaceOfBirth = string.Empty;
    }

    public async Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");
        var years = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.OrgId == orgId)
            .Select(y => new { y.PropertyId, y.TaxYear, Regime = y.Regime.ToString(), y.IsPrimaryForCedolare })
            .ToListAsync(cancellationToken);
        return new Dictionary<string, object>
        {
            ["hasPartitaIva"] = org.HasPartitaIva,
            ["partitaIvaNumber"] = org.PartitaIvaNumber ?? "",
            ["fiscalCode"] = org.FiscalCode ?? "",
            ["fiscalDataRetentionUntil"] = org.FiscalDataRetentionUntil?.ToString("O") ?? "",
            ["propertyFiscalYears"] = years,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
        };
    }

    public async Task AnonymizeOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return;
        var token = $"ANON-{orgId:N}";
        org.FiscalCode = token.Length > 16 ? token[..16] : token;
        org.PartitaIvaNumber = "00000000000";
        org.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Org {OrgId} fiscal identifiers anonymized", orgId);
    }
}
