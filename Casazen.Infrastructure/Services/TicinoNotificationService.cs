using System.Globalization;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITicinoNotificationService" />
/// <remarks>
/// <para><b>Which stays.</b> The bookings of the property that arrive on the day (the stored check-in date), confirmed, checked in or checked
/// out (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, the one definition of a stay that owes a police communication), not a
/// no-show; the guests are the rows of the check-in (<see cref="StayGuest"/>), or the booker alone while there are none. Every read names the
/// org of the property, so a context with its tenant filter off (a job) never mixes orgs.</para>
/// <para><b>The day.</b> Switzerland and Italy share the time zone rules (CET/CEST): the calendar of Rome gives the day of Lugano.</para>
/// </remarks>
public class TicinoNotificationService(AppDbContext context, TimeProvider? timeProvider = null) : ITicinoNotificationService
{
    private const string TicinoCantonCode = "TI";
    private const string PoliceReceiptType = nameof(SwissComplianceFileType.Police);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TicinoNotificationDay> GetDayAsync(Guid propertyId, DateOnly day, CancellationToken cancellationToken = default)
    {
        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);
        var stays = await ReadStaysAsync(property, day, cancellationToken);
        var plan = TicinoPoliceFile.Plan(property.TicinoStructureId ?? "-", stays.Select(s => s.Facts), _clock.TodayInRomeAsDateOnly());
        var receipts = await context.SwissComplianceReceipts
            .AsNoTracking()
            .CountAsync(
                r => r.OrgId == property.OrgId && r.PropertyId == property.Id && r.Type == PoliceReceiptType && r.ReportDate == StoredDay(day),
                cancellationToken);
        var pending = await ListPendingDaysAsync(property.OrgId, [property.Id], _clock.TodayInRomeAsDateOnly(), cancellationToken);

        return new TicinoNotificationDay(
            property.Id, day, property.TicinoStructureId, stays.Count, plan, receipts, DueAt(day, stays.Select(s => s.ArrivedAt)), pending);
    }

    public async Task<TicinoNotificationFile> GenerateFileAsync(
        Guid propertyId,
        DateOnly day,
        TicinoPoliceFileFormat format,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);
        if (property.TicinoStructureId is not { } structureId || !TicinoStructureId.IsValid(structureId))
            throw new DomainRuleException("ticino_structure_id_missing", "TicinoStructureIdMissing");

        var stays = await ReadStaysAsync(property, day, cancellationToken);
        var plan = TicinoPoliceFile.Plan(structureId, stays.Select(s => s.Facts), _clock.TodayInRomeAsDateOnly());
        if (plan.Registrations.Count == 0)
            throw new DomainRuleException("ticino_notification_empty", "TicinoNotificationEmpty");
        if (format == TicinoPoliceFileFormat.Xml && !plan.XmlAvailable)
            throw new DomainRuleException("ticino_xml_not_available", "TicinoXmlNotAvailable");

        var content = format == TicinoPoliceFileFormat.Xml
            ? TicinoPoliceFile.WriteXml(plan.Registrations)
            : TicinoPoliceFile.WriteCsv(plan.Registrations);
        var extension = format == TicinoPoliceFileFormat.Xml ? "xml" : "csv";
        var contentType = format == TicinoPoliceFileFormat.Xml ? "application/xml" : "text/csv; charset=iso-8859-1";
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new TicinoNotificationFile(
            content,
            $"notifiche-ti-{date}.{extension}",
            contentType,
            $"TI-{property.Id:N}-{day:yyyyMMdd}",
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            plan.Registrations.Count);
    }

    public async Task<IReadOnlyList<TicinoPendingDay>> ListPendingDaysAsync(
        Guid? orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var propertyQuery = context.Properties.AsNoTracking()
            .Where(p => p.Country.Trim().ToUpper() == "CH" && p.SwissMunicipalityCode != null);
        if (orgId is { } onlyOrg)
            propertyQuery = propertyQuery.Where(p => p.OrgId == onlyOrg);
        if (propertyIds is not null)
            propertyQuery = propertyQuery.Where(p => propertyIds.Contains(p.Id));

        var properties = await propertyQuery
            .Join(
                context.SwissMunicipalities.AsNoTracking().Where(m => m.CantonCode == TicinoCantonCode),
                p => p.SwissMunicipalityCode,
                m => m.MunicipalityCode,
                (p, _) => new { p.Id, p.OrgId, p.Name })
            .ToListAsync(cancellationToken);
        if (properties.Count == 0)
            return [];

        var ids = properties.Select(p => p.Id).ToList();
        var from = StoredDay(today.AddDays(-ITicinoNotificationService.PendingLookbackDays));
        var to = StoredDay(today.AddDays(1));
        var arrivals = await context.Bookings
            .AsNoTracking()
            .Where(b => ids.Contains(b.PropertyId)
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null
                && b.CheckInDate >= from
                && b.CheckInDate < to)
            .Select(b => new { b.OrgId, b.PropertyId, b.CheckInDate, b.ArrivedAt })
            .ToListAsync(cancellationToken);
        var notified = (await context.SwissComplianceReceipts
                .AsNoTracking()
                .Where(r => ids.Contains(r.PropertyId) && r.Type == PoliceReceiptType && r.ReportDate >= from && r.ReportDate < to)
                .Select(r => new { r.OrgId, r.PropertyId, r.ReportDate })
                .ToListAsync(cancellationToken))
            .Select(r => (r.OrgId, r.PropertyId, Day: DateOnly.FromDateTime(r.ReportDate)))
            .ToHashSet();

        var byId = properties.ToDictionary(p => p.Id);
        return arrivals
            .Where(a => byId.TryGetValue(a.PropertyId, out var owner) && owner.OrgId == a.OrgId)
            .GroupBy(a => (a.PropertyId, Day: DateOnly.FromDateTime(a.CheckInDate)))
            .Where(g => !notified.Contains((byId[g.Key.PropertyId].OrgId, g.Key.PropertyId, g.Key.Day)))
            .Select(g => new TicinoPendingDay(
                byId[g.Key.PropertyId].OrgId,
                g.Key.PropertyId,
                byId[g.Key.PropertyId].Name,
                g.Key.Day,
                g.Count(),
                DueAt(g.Key.Day, g.Select(a => a.ArrivedAt))))
            .OrderBy(d => d.Day)
            .ThenBy(d => d.PropertyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<bool> IsPropertyInTicinoAsync(Guid propertyId)
    {
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId);
        return property is not null && await IsInTicinoAsync(property, CancellationToken.None);
    }

    private async Task<Property> LoadTicinoPropertyAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        if (!await IsInTicinoAsync(property, cancellationToken))
            throw new DomainRuleException("property_not_in_ticino", "PropertyNotInTicino", propertyId);
        return property;
    }

    private async Task<bool> IsInTicinoAsync(Property property, CancellationToken cancellationToken)
    {
        if (!string.Equals(property.Country?.Trim(), "CH", StringComparison.OrdinalIgnoreCase) || property.SwissMunicipalityCode is null)
            return false;

        return await context.SwissMunicipalities
            .AsNoTracking()
            .AnyAsync(m => m.MunicipalityCode == property.SwissMunicipalityCode && m.CantonCode == TicinoCantonCode, cancellationToken);
    }

    private sealed record StayRead(TicinoStayFacts Facts, DateTime? ArrivedAt);

    private async Task<List<StayRead>> ReadStaysAsync(Property property, DateOnly day, CancellationToken cancellationToken)
    {
        var from = StoredDay(day);
        var to = StoredDay(day.AddDays(1));
        var bookings = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.OrgId == property.OrgId
                && b.PropertyId == property.Id
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null
                && b.CheckInDate >= from
                && b.CheckInDate < to)
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);
        if (bookings.Count == 0)
            return [];

        var ids = bookings.Select(b => b.Id).ToList();
        var guests = (await context.StayGuests
                .AsNoTracking()
                .Include(g => g.Guest)
                .Where(g => g.OrgId == property.OrgId && ids.Contains(g.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(g => g.BookingId);

        return bookings.Select(b =>
        {
            var rows = guests[b.Id].OrderBy(g => g.Position).ToList();
            var facts = rows.Count > 0
                ? rows.Select(g => FactsOf(g, g.Guest)).ToList()
                : [FactsOf(StayGuest.FromBooker(b, b.Guest), b.Guest)];
            var booker = $"{b.Guest?.FirstName} {b.Guest?.LastName}".Trim();
            return new StayRead(
                new TicinoStayFacts(
                    b.Id, booker, DateOnly.FromDateTime(b.CheckInDate), DateOnly.FromDateTime(b.CheckOutDate), b.NumberOfGuests, facts),
                b.ArrivedAt);
        }).ToList();
    }

    private static TicinoGuestFacts FactsOf(StayGuest guest, Guest? record) => new(
        guest.Position,
        guest.Type,
        guest.FirstName,
        guest.LastName,
        guest.DateOfBirth is { } born ? DateOnly.FromDateTime(born) : null,
        guest.CitizenshipCode,
        guest.CitizenshipName,
        guest.BornInItaly,
        guest.BirthComuneName,
        guest.BirthCountryName,
        guest.DocumentType,
        guest.DocumentTypeCode,
        guest.DocumentNumber,
        guest.ResidenceInItaly,
        guest.ResidenceComuneName,
        guest.ResidenceCountryCode,
        guest.ResidenceCountryName,
        record?.Address,
        record?.PostalCode,
        record?.City);

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>24 hours from the first recorded arrival; the start of the next day (Europe/Zurich) when none is recorded.</summary>
    private static DateTime DueAt(DateOnly day, IEnumerable<DateTime?> arrivedAt)
    {
        var first = arrivedAt.Where(a => a.HasValue).Select(a => a!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        return first == DateTime.MaxValue ? RomeCalendar.StartOfDayUtc(day.AddDays(1)) : first.AddHours(24);
    }
}
