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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

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
/// <para><b>A closed day</b> (E30/F02/US02/T01, NF-128): a day with a <c>Police</c> receipt (<see cref="SwissComplianceReceipt"/>) or a declaration «I uploaded the file»
/// (<see cref="SwissNotificationDeclaration"/> without a regime code: the notification to the Ticino police). <see cref="ListPendingDaysAsync"/> is the one place that applies it, so
/// the alerts and the cockpit never read the two tables; the file of a closed day is generated again only on an explicit confirmation (the portal may create duplicates).</para>
/// <para><b>The audit</b>: every file and every group sheet writes one <see cref="SwissNotificationFileEvent"/>, with the format, the rows and the hash and no personal data. A
/// declaration or its undo is told live to the hosts of the property by <see cref="TicinoDeclarationLiveRefresh"/> (<c>ticino-notification-declared</c>), best effort once the row is
/// saved: a push that cannot be queued is logged and never fails the person who declared. The service never reads the caller: the web layer says who asks.</para>
/// </remarks>
public class TicinoNotificationService(
    AppDbContext context,
    TimeProvider? timeProvider = null,
    IPropertySwissPlaceResolver? placeResolver = null,
    IOptions<GuestNotificationOptions>? guestNotificationOptions = null,
    IPushNotificationService? pushNotifications = null,
    ILogger<TicinoNotificationService>? logger = null) : ITicinoNotificationService
{
    private const string PoliceReceiptType = nameof(SwissComplianceFileType.Police);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IPropertySwissPlaceResolver _places = placeResolver ?? new PropertySwissPlaceResolver(context);
    private readonly TicinoCsvCharset _csvCharset = (guestNotificationOptions?.Value ?? new GuestNotificationOptions()).ResolveTicinoCsvCharset();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

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

        // E30/F02/US02/T01: who declared the day and when, and how many times its file was generated (the audit rows hold no personal data).
        var declaration = await DeclarationsOf(property, day).AsNoTracking().OrderBy(d => d.DeclaredAt).ThenBy(d => d.Id).FirstOrDefaultAsync(cancellationToken);
        var declaredBy = declaration is null ? null : await DisplayNameAsync(declaration.DeclaredByUserId, cancellationToken);
        var policeFormats = SwissNotificationFileFormats.PoliceFile.ToList();
        var generatedAt = await context.SwissNotificationFileEvents
            .AsNoTracking()
            .Where(e => e.OrgId == property.OrgId && e.PropertyId == property.Id && e.ArrivalDay == day && policeFormats.Contains(e.Format))
            .Select(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

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
            csv.TransliteratedCount,
            declaration?.DeclaredAt,
            declaredBy,
            generatedAt.Count == 0 ? null : generatedAt.Max(),
            generatedAt.Count);
    }

    public async Task<TicinoNotificationFile> GenerateFileAsync(
        Guid propertyId,
        DateOnly day,
        TicinoPoliceFileFormat format,
        TicinoFileRequest? request = null,
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

        // NF-128 AC2: the portal may create duplicates when the same day is uploaded twice (specifica-ticino §3), so the file of a day that is declared (or has a receipt) is generated
        // again only when the person confirmed it. The checks of the data come first: a day with nothing to write answers that, not a confirmation for a file that would not exist.
        if (request?.ConfirmRegenerate != true && await IsClosedAsync(property, day, cancellationToken))
            throw new DomainConflictException("ticino_day_already_declared", "TicinoDayAlreadyDeclared");

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

        var file = new TicinoNotificationFile(
            content,
            $"notifiche-ti-{date}.{extension}",
            contentType,
            $"TI-{property.Id:N}-{day:yyyyMMdd}",
            Sha256Of(content),
            plan.Registrations.Count,
            transliterated);

        // NF-128 AC3: every generation leaves one row, with no personal data. A row that cannot be written fails the request: no file leaves unaudited.
        await RecordFileEventAsync(
            property,
            day,
            format == TicinoPoliceFileFormat.Xml ? SwissNotificationFileFormats.Xml : SwissNotificationFileFormats.Csv,
            file.Rows,
            file.FileHash,
            request?.UserId,
            cancellationToken);
        return file;
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

        // THE rule of a closed day (E30/F02/US02/T01): a receipt of the portal for the day, or a declaration of it. The alerts and the cockpit read only this list.
        var notified = (await context.SwissComplianceReceipts
                .AsNoTracking()
                .Where(r => ids.Contains(r.PropertyId) && r.Type == PoliceReceiptType && r.ReportDate >= from && r.ReportDate < to)
                .Select(r => new { r.OrgId, r.PropertyId, r.ReportDate })
                .ToListAsync(cancellationToken))
            .Select(r => (r.OrgId, r.PropertyId, Day: DateOnly.FromDateTime(r.ReportDate)))
            .ToHashSet();
        var firstDay = today.AddDays(-ITicinoNotificationService.PendingLookbackDays);
        var declared = await context.SwissNotificationDeclarations
            .AsNoTracking()
            .Where(d => ids.Contains(d.PropertyId) && d.RegimeCode == null && d.ArrivalDay >= firstDay && d.ArrivalDay <= today)
            .Select(d => new { d.OrgId, d.PropertyId, d.ArrivalDay })
            .ToListAsync(cancellationToken);
        notified.UnionWith(declared.Select(d => (d.OrgId, d.PropertyId, Day: d.ArrivalDay)));

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

    public async Task<TicinoDeclaration> DeclareDayAsync(
        Guid propertyId,
        DateOnly day,
        string userId,
        Guid? receiptId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);

        // A day that has not started cannot be declared: it would close the day of an arrival that has not happened and silence its alerts.
        if (day > _clock.TodayInRomeAsDateOnly())
            throw new DomainRuleException("ticino_declaration_day_in_future", "TicinoDeclarationDayInFuture");
        if (receiptId is { } receipt && !await IsPoliceReceiptOfTheDayAsync(property, day, receipt, cancellationToken))
            throw new DomainRuleException("ticino_declaration_receipt_invalid", "TicinoDeclarationReceiptInvalid");

        // Idempotent: a day declared already answers the declaration that exists, whoever repeats the call. A receipt it does not have yet is attached; nothing else changes.
        var existing = await FirstDeclarationAsync(property, day, cancellationToken);
        if (existing is not null && receiptId is { } attach && existing.ReceiptId != attach)
        {
            existing.ReceiptId = attach;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A co-host undid the declaration between the read and the save: the row to update is gone, so this call comes after the undo and declares the day again below.
                context.Entry(existing).State = EntityState.Detached;
                existing = await FirstDeclarationAsync(property, day, cancellationToken);
            }
        }

        if (existing is not null)
            return await ToDeclarationAsync(existing, created: false, cancellationToken);

        var declaration = new SwissNotificationDeclaration
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            ArrivalDay = day,
            DeclaredByUserId = userId,

            // Whole microseconds, the precision of the column: the answer of this call and the answer of every replay (read back from the database) carry the same instant.
            DeclaredAt = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime),
            Channel = SwissNotificationChannels.Portal,
            ReceiptId = receiptId,
            RegimeCode = null,
        };

        // Added through the set, never through a collection of a parent: the Guid is preset, and EF would track a child added that way as Modified.
        context.SwissNotificationDeclarations.Add(declaration);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A co-host declared the same day at the same moment and won the unique index: the answer is the declaration that is there, as if the call had come second.
            context.Entry(declaration).State = EntityState.Detached;
            var winner = await FirstDeclarationAsync(property, day, cancellationToken);
            if (winner is null)
                throw;

            return await ToDeclarationAsync(winner, created: false, cancellationToken);
        }

        // Ids and the day only: the log never names a guest, and the person is the Auth0 subject.
        _logger.LogInformation(
            "Ticino notification of property {PropertyId} for {ArrivalDay} declared by {UserId} (receipt attached: {HasReceipt})",
            property.Id,
            day,
            userId,
            receiptId is not null);
        TicinoDeclarationLiveRefresh.Tell(pushNotifications, property.OrgId, property.Id, property.Name, day, declared: true, declaration.DeclaredAt, _logger);
        return await ToDeclarationAsync(declaration, created: true, cancellationToken);
    }

    public async Task<bool> UndeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);
        var declarations = await DeclarationsOf(property, day).ToListAsync(cancellationToken);
        if (declarations.Count == 0)
            return false;

        context.SwissNotificationDeclarations.RemoveRange(declarations);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A co-host undid the same declaration at the same moment and won: the row is gone, which is what was asked. This call has nothing to undo and tells nobody, the answer
            // is the idempotent one (false) and the winner told the hosts.
            foreach (var removed in declarations)
                context.Entry(removed).State = EntityState.Detached;

            return false;
        }

        _logger.LogInformation(
            "Ticino notification declaration of property {PropertyId} for {ArrivalDay} undone by {UserId}",
            property.Id,
            day,
            userId);
        TicinoDeclarationLiveRefresh.Tell(pushNotifications, property.OrgId, property.Id, property.Name, day, declared: false, _clock.GetUtcNow().UtcDateTime, _logger);
        return true;
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

    public async Task<TicinoGroupSheet> GetGroupSheetAsync(
        Guid propertyId,
        Guid bookingId,
        TicinoFileRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadTicinoPropertyAsync(propertyId, cancellationToken);

        // The same stays as the file: of this property and of its org, owing a notification (never a pending, cancelled or no-show booking).
        var stay = (await ReadStaysAsync(property, b => b.Id == bookingId, cancellationToken)).SingleOrDefault()
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
        var members = TicinoPoliceFile.GroupSheetOf(stay.Facts)
            ?? throw new DomainRuleException("ticino_not_a_group", "TicinoNotAGroup", TicinoPoliceFile.GroupMinSize);

        var arrival = stay.Facts.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var code = string.IsNullOrWhiteSpace(stay.Facts.ReferenceNumber) ? bookingId.ToString("N") : stay.Facts.ReferenceNumber;
        var sheet = new TicinoGroupSheet(
            TicinoPoliceFile.WriteGroupSheet(members), $"scheda-gruppo-ti-{arrival}-{code}.csv", "text/csv; charset=utf-8", members.Count);

        // NF-128 AC3: the sheet is audited like the file of the day (the arrival day of its stay, the members, the hash); no name, no date of birth, no booking code.
        await RecordFileEventAsync(
            property, stay.Facts.Arrival, SwissNotificationFileFormats.GroupSheet, sheet.Members, Sha256Of(sheet.Content), request?.UserId, cancellationToken);
        return sheet;
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

    /// <summary>The declarations of the notification to the Ticino police (no regime code) of <paramref name="property"/> for <paramref name="day"/>; the org of the property is always named.</summary>
    private IQueryable<SwissNotificationDeclaration> DeclarationsOf(Property property, DateOnly day) =>
        context.SwissNotificationDeclarations
            .Where(d => d.OrgId == property.OrgId && d.PropertyId == property.Id && d.ArrivalDay == day && d.RegimeCode == null);

    /// <summary>The declaration of <paramref name="day"/> that stands (the first one, tracked), or <c>null</c> when the day is not declared.</summary>
    private Task<SwissNotificationDeclaration?> FirstDeclarationAsync(Property property, DateOnly day, CancellationToken cancellationToken) =>
        DeclarationsOf(property, day).OrderBy(d => d.DeclaredAt).ThenBy(d => d.Id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Whether the day is closed: a declaration of it, or a <c>Police</c> receipt recorded for it (the rule of <see cref="ListPendingDaysAsync"/>).</summary>
    private async Task<bool> IsClosedAsync(Property property, DateOnly day, CancellationToken cancellationToken) =>
        await DeclarationsOf(property, day).AsNoTracking().AnyAsync(cancellationToken)
        || await context.SwissComplianceReceipts
            .AsNoTracking()
            .AnyAsync(
                r => r.OrgId == property.OrgId && r.PropertyId == property.Id && r.Type == PoliceReceiptType && r.ReportDate == StoredDay(day),
                cancellationToken);

    /// <summary>Whether <paramref name="receiptId"/> is a <c>Police</c> receipt of this property, of its org, whose report date is the arrival day.</summary>
    private Task<bool> IsPoliceReceiptOfTheDayAsync(Property property, DateOnly day, Guid receiptId, CancellationToken cancellationToken) =>
        context.SwissComplianceReceipts
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == receiptId
                    && r.OrgId == property.OrgId
                    && r.PropertyId == property.Id
                    && r.Type == PoliceReceiptType
                    && r.ReportDate == StoredDay(day),
                cancellationToken);

    /// <summary>The audit row of a file (no personal data: a format, a number of rows, a hash, the day and the person who asked).</summary>
    private async Task RecordFileEventAsync(
        Property property,
        DateOnly day,
        string format,
        int rows,
        string sha256,
        string? userId,
        CancellationToken cancellationToken)
    {
        context.SwissNotificationFileEvents.Add(new SwissNotificationFileEvent
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            ArrivalDay = day,
            Format = format,
            RowCount = rows,
            Sha256 = sha256,
            CreatedByUserId = userId,
            CreatedAt = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime),
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<TicinoDeclaration> ToDeclarationAsync(SwissNotificationDeclaration declaration, bool created, CancellationToken cancellationToken) =>
        new(
            declaration.PropertyId,
            declaration.ArrivalDay,
            declaration.DeclaredAt,
            await DisplayNameAsync(declaration.DeclaredByUserId, cancellationToken),
            declaration.ReceiptId,
            created);

    /// <summary>The first and last name of a user as the team sees them; <c>null</c> when the account no longer exists or has no name.</summary>
    private async Task<string?> DisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        var name = user is null ? string.Empty : $"{user.FirstName} {user.LastName}".Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// A unique violation (23505) of the key of the declaration (<see cref="SwissNotificationDeclaration.DefaultRegimeIndexName"/>), whatever the wrapper EF or the provider put around it: a
    /// co-host declared the same day first.
    /// </summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SwissNotificationDeclaration.DefaultRegimeIndexName })
                return true;
        }

        return false;
    }

    private static string Sha256Of(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>24 hours from the first recorded arrival; the start of the next day (Europe/Zurich) when none is recorded.</summary>
    private static DateTime DueAt(DateOnly day, IEnumerable<DateTime?> arrivedAt)
    {
        var first = arrivedAt.Where(a => a.HasValue).Select(a => a!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        return first == DateTime.MaxValue ? RomeCalendar.StartOfDayUtc(day.AddDays(1)) : first.AddHours(24);
    }
}
