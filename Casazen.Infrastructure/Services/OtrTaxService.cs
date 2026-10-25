using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// OTR (Overnight Tourist Registry) tax CSV generator for Ticino (ADR-016).
/// Generates CSV files of overnight stays per period for cantonal tax reporting.
/// </summary>
public class OtrTaxService(AppDbContext context) : IOtrTaxService
{
    private const string TicinoCantonCode = "TI";

    public async Task<OtrTaxResult> GeneratePeriodReportAsync(
        Guid propertyId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        // Validate property exists and is in Ticino
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw PropertyNotFound(propertyId);

        var municipality = property.SwissMunicipalityCode != null
            ? await context.SwissMunicipalities
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.MunicipalityCode == property.SwissMunicipalityCode, cancellationToken)
            : null;

        if (municipality?.CantonCode != TicinoCantonCode)
            throw new DomainRuleException(
                "property_not_in_ticino",
                "PropertyNotInTicino",
                propertyId);

        // Get overnight stays for the period
        var stays = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.PropertyId == propertyId
                && b.Status == BookingStatus.Confirmed
                && b.CheckInDate < endDate
                && b.CheckOutDate > startDate)
            .ToListAsync(cancellationToken);

        // Generate CSV in OTR format
        var csv = GenerateOtrCsv(property, stays, startDate, endDate);
        var reference = $"OTR-{property.Id:N}-{startDate:yyyyMMdd}-{endDate:yyyyMMdd}";
        var fileHash = ComputeSha256Hash(csv);

        return new OtrTaxResult(csv, reference, fileHash);
    }

    private static string GenerateOtrCsv(Property property, List<Booking> stays, DateTime startDate, DateTime endDate)
    {
        var csv = new StringBuilder();

        // CSV header
        csv.AppendLine("PropertyId,PropertyName,BookingId,GuestName,CheckIn,CheckOut,Nights,NumberOfGuests");

        foreach (var stay in stays)
        {
            // Calculate overlap with the period
            var effectiveCheckIn = stay.CheckInDate < startDate ? startDate : stay.CheckInDate;
            var effectiveCheckOut = stay.CheckOutDate > endDate ? endDate : stay.CheckOutDate;
            var nights = (effectiveCheckOut - effectiveCheckIn).Days;

            if (nights > 0)
            {
                csv.AppendLine($"{property.Id}," +
                    $"\"{EscapeCsv(property.Name)}\"," +
                    $"{stay.Id}," +
                    $"\"{EscapeCsv($"{stay.Guest.FirstName} {stay.Guest.LastName}")}\"," +
                    $"{effectiveCheckIn:yyyy-MM-dd}," +
                    $"{effectiveCheckOut:yyyy-MM-dd}," +
                    $"{nights}," +
                    $"{stay.NumberOfGuests}");
            }
        }

        return csv.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"'))
            return value.Replace("\"", "\"\"");
        return value;
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
