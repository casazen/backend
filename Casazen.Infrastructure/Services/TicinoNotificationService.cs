using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITicinoNotificationService" />
/// <remarks>
/// <para><b>Which stays.</b> The bookings of the property that arrive on the day (the stored check-in date), confirmed, checked in or checked
/// out (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, the one definition of a stay that owes a police communication), not a
/// no-show; the guests are the rows of the check-in (<see cref="StayGuest"/>), or the booker alone while there are none. Every read names the
/// org of the property, so a context with its tenant filter off (a job) never mixes orgs.</para>
/// <para><b>The day.</b> Switzerland and Italy share the time zone rules (CET/CEST): the calendar of Rome gives the day of Lugano.</para>
/// <para><b>In Ticino.</b> A property is in Ticino when the one resolver of the place of a Swiss property says so
/// (<see cref="IPropertySwissPlaceResolver"/>, E29/F01/US01/T02): its stored municipality, or the one its postal code and city give; an ambiguous
/// postal code is not in Ticino until the host chooses the municipality.</para>
/// <para><b>The CSV</b> is written in the character set of <c>GuestNotifications:TicinoCsvEncoding</c> (<see cref="GuestNotificationOptions"/>,
/// Windows-1252 by default); the day says how many characters of its rows that set cannot hold (NF-126).</para>
/// </remarks>
public class TicinoNotificationService(
    AppDbContext context,
    TimeProvider? timeProvider = null,
    IPropertySwissPlaceResolver? placeResolver = null,
    IOptions<GuestNotificationOptions>? guestNotificationOptions = null) : ITicinoNotificationService
{
    private const string PoliceReceiptType = nameof(SwissComplianceFileType.Police);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IPropertySwissPlaceResolver _places = placeResolver ?? new PropertySwissPlaceResolver(context);
    private readonly TicinoCsvCharset _csvCharset = (guestNotificationOptions?.Value ?? new GuestNotificationOptions()).ResolveTicinoCsvCharset();

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

        // The characters the CSV of the day would write without their accent (or as «?»), counted on the file itself.
        var csv = TicinoPoliceFile.WriteCsvFile(plan.Registrations, _csvCharset);

        return new TicinoNotificationDay(
            property.Id,
            day,
            property.TicinoStructureId,
            stays.Count,
            plan,
            receipts,
            DueAt(day, stays.Select(s => s.ArrivedAt)),
            pending,
            csv.Charset,
            csv.TransliteratedCount);
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

        byte[] content;
        string contentType;
        var transliterated = 0;
        if (format == TicinoPoliceFileFormat.Xml)
        {
            content = TicinoPoliceFile.WriteXml(plan.Registrations);
            contentType = "application/xml";
        }
        else
        {
            var csv = TicinoPoliceFile.WriteCsvFile(plan.Registrations, _csvCharset);
            content = csv.Content;
            contentType = $"text/csv; charset={csv.Charset}";
            transliterated = csv.TransliteratedCount;
        }

        var extension = format == TicinoPoliceFileFormat.Xml ? "xml" : "csv";
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new TicinoNotificationFile(
            content,
            $"notifiche-ti-{date}.{extension}",
            contentType,
            $"TI-{property.Id:N}-{day:yyyyMMdd}",
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            plan.Registrations.Count,
            transliterated);
    }

    public async Task<IReadOnlyList<TicinoPendingDay>> ListPendingDaysAsync(
        Guid? orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var propertyQuery = context.Properties.AsNoTracking()
            .Where(p => p.Country.Trim().ToUpper() == "CH");
        if (orgId is { } onlyOrg)
            propertyQuery = propertyQuery.Where(p => p.OrgId == onlyOrg);
        if (propertyIds is not null)
            propertyQuery = propertyQuery.Where(p => propertyIds.Contains(p.Id));

        // The Swiss properties the one resolver places in Ticino (the stored municipality, or the one the postal code and the city give).
        var swiss = await propertyQuery
            .Select(p => new { p.Id, p.OrgId, p.Name, p.Country, p.SwissMunicipalityCode, p.PostalCode, p.City })
            .ToListAsync(cancellationToken);
        if (swiss.Count == 0)
            return [];

        var places = await _places.ResolveManyAsync(
            [.. swiss.Select(p => new SwissPlaceLookup(p.Id, p.Country, p.SwissMunicipalityCode, p.PostalCode, p.City))], cancellationToken);
        var properties = swiss
            .Where(p => places.TryGetValue(p.Id, out var place) && place.IsInTicino)
            .Select(p => new { p.Id, p.OrgId, p.Name })
            .ToList();
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

    private async Task<bool> IsInTicinoAsync(Property property, CancellationToken cancellationToken) =>
        (await _places.ResolveAsync(property, cancellationToken))?.IsInTicino == true;

    public async Task<TicinoGroupSheet> GetGroupSheetAsync(Guid propertyId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);

        // The same stays as the file: of this property and of its org, owing a notification (never a pending, cancelled or no-show booking).
        var stay = (await ReadStaysAsync(property, b => b.Id == bookingId, cancellationToken)).SingleOrDefault()
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
        var members = TicinoPoliceFile.GroupSheetOf(stay.Facts)
            ?? throw new DomainRuleException("ticino_not_a_group", "TicinoNotAGroup", TicinoPoliceFile.GroupMinSize);

        var arrival = stay.Facts.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var code = string.IsNullOrWhiteSpace(stay.Facts.ReferenceNumber) ? bookingId.ToString("N") : stay.Facts.ReferenceNumber;
        return new TicinoGroupSheet(
            TicinoPoliceFile.WriteGroupSheet(members), $"scheda-gruppo-ti-{arrival}-{code}.csv", "text/csv; charset=utf-8", members.Count);
    }

    private sealed record StayRead(TicinoStayFacts Facts, DateTime? ArrivedAt);

    private Task<List<StayRead>> ReadStaysAsync(Property property, DateOnly day, CancellationToken cancellationToken)
    {
        var from = StoredDay(day);
        var to = StoredDay(day.AddDays(1));
        return ReadStaysAsync(property, b => b.CheckInDate >= from && b.CheckInDate < to, cancellationToken);
    }

    /// <summary>
    /// The stays of <paramref name="property"/> that <paramref name="which"/> selects and that owe a police communication (the one definition of
    /// <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, not a no-show), with their guests; every read names the org of the property.
    /// </summary>
    private async Task<List<StayRead>> ReadStaysAsync(Property property, Expression<Func<Booking, bool>> which, CancellationToken cancellationToken)
    {
        var bookings = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.OrgId == property.OrgId
                && b.PropertyId == property.Id
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null)
            .Where(which)
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

            // NF-126: «Numero bollettino» is the code of the booking as hosts read it, «Numero autovettura» the plate of the stay.
            return new StayRead(
                new TicinoStayFacts(
                    b.Id,
                    booker,
                    DateOnly.FromDateTime(b.CheckInDate),
                    DateOnly.FromDateTime(b.CheckOutDate),
                    b.NumberOfGuests,
                    facts,
                    string.IsNullOrWhiteSpace(b.BookingCode) ? null : BookingCodes.Format(b.BookingCode),
                    b.VehiclePlate),
                b.ArrivedAt);
        }).ToList();
    }

    /// <summary>
    /// A guest of the stay as the file reads it. The home is the one the check-in recorded for the guest (NF-132); for the row linked to the booker's
    /// record, a part the check-in does not have is taken from that record, as before NF-132.
    /// </summary>
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
        FirstText(guest.ResidenceStreet, record?.Address),
        FirstText(guest.ResidencePostalCode, record?.PostalCode),
        FirstText(guest.ResidenceCityName, record?.City));

    private static string? FirstText(string? recorded, string? fallback) => string.IsNullOrWhiteSpace(recorded) ? fallback : recorded;

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>24 hours from the first recorded arrival; the start of the next day (Europe/Zurich) when none is recorded.</summary>
    private static DateTime DueAt(DateOnly day, IEnumerable<DateTime?> arrivedAt)
    {
        var first = arrivedAt.Where(a => a.HasValue).Select(a => a!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        return first == DateTime.MaxValue ? RomeCalendar.StartOfDayUtc(day.AddDays(1)) : first.AddHours(24);
    }
}
