using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Working XML summary of overnight stays in Ticino (ADR-016).
/// The current structure is not the official notifiche.ti.ch import schema; it must not be presented as a verified submission file.
/// </summary>
public class TicinoNotificationService(AppDbContext context) : ITicinoNotificationService
{
    private const string TicinoCantonCode = "TI";

    public async Task<TicinoNotificationResult> GenerateDailyNotificationAsync(
        Guid propertyId,
        DateTime reportDate,
        CancellationToken cancellationToken = default)
    {
        // Validate property is in Ticino
        var property = await context.Properties
            .AsNoTracking()
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw PropertyNotFound(propertyId);

        var municipality = property.SwissMunicipalityCode != null
            ? await context.SwissMunicipalities
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.MunicipalityCode == property.SwissMunicipalityCode, cancellationToken)
            : null;

        if (!string.Equals(property.Country?.Trim(), "CH", StringComparison.OrdinalIgnoreCase)
            || municipality?.CantonCode != TicinoCantonCode)
            throw new DomainRuleException(
                "property_not_in_ticino",
                "PropertyNotInTicino",
                propertyId);

        // Get overnight stays for the report date
        var reportDateOnly = DateOnly.FromDateTime(reportDate);
        var stays = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Where(b => b.OrgId == property.OrgId && b.PropertyId == propertyId
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut)
                && DateOnly.FromDateTime(b.CheckInDate) <= reportDateOnly
                && DateOnly.FromDateTime(b.CheckOutDate) > reportDateOnly)
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

        // A working summary only: see docs/compliance/ticino-import-format.md for the verified official schema and remaining inputs.
        var xml = GenerateNotificationXml(property, stays, reportDate);
        var reference = $"TI-{property.Id:N}-{reportDate:yyyyMMdd}";
        var fileHash = ComputeSha256Hash(xml);

        return new TicinoNotificationResult(xml, reference, fileHash);
    }

    public async Task<bool> IsPropertyInTicinoAsync(Guid propertyId)
    {
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId);

        if (!string.Equals(property?.Country?.Trim(), "CH", StringComparison.OrdinalIgnoreCase)
            || property?.SwissMunicipalityCode == null)
            return false;

        var municipality = await context.SwissMunicipalities
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MunicipalityCode == property.SwissMunicipalityCode);

        return municipality?.CantonCode == TicinoCantonCode;
    }

    private static string GenerateNotificationXml(Property property, List<Booking> stays, DateTime reportDate)
    {
        // Internal summary, not the official registrationForms/registration import format.
        var notification = new XElement("TicinoNotification",
            new XElement("Header",
                new XElement("ReportDate", reportDate.ToString("yyyy-MM-dd")),
                new XElement("PropertyId", property.Id),
                new XElement("PropertyName", property.Name),
                new XElement("PropertyAddress", property.Address),
                new XElement("MunicipalityCode", property.SwissMunicipalityCode ?? "")
            ),
            new XElement("Stays",
                stays.Select(s => new XElement("Stay",
                    new XElement("BookingId", s.Id),
                    new XElement("GuestName", $"{s.Guest.FirstName} {s.Guest.LastName}"),
                    new XElement("CheckIn", s.CheckInDate.ToString("yyyy-MM-dd")),
                    new XElement("CheckOut", s.CheckOutDate.ToString("yyyy-MM-dd")),
                    new XElement("NumberOfGuests", s.NumberOfGuests)
                ))
            )
        );

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            notification
        );

        return doc.ToString();
    }

    private static string ComputeSha256Hash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static NotFoundException PropertyNotFound(Guid id) =>
        new($"Property {id} not found")
        {
            Code = "property_not_found",
            MessageKey = "PropertyNotFound"
        };
}
