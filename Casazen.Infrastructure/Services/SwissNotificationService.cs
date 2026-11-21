using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISwissNotificationService" />
/// <remarks>
/// <para><b>Which properties.</b> The Swiss ones (country <c>CH</c>) whose regime, as the one resolver says it (<see cref="IGuestRegistrationRegimeResolver"/>: never a test of the canton
/// written here), the assisted notification covers (<see cref="SwissAssistedNotification.Covers"/>: not Ticino, an obligation to report to an authority) and, for the list, whose duty is
/// <see cref="GuestRegistrationEffectiveDuty.Required"/>: <c>Ask</c> and <c>NotRequired</c> never raise a day. The list is empty while <c>Features:GuestRegistrationRegimes</c> is off.</para>
/// <para><b>Which stays.</b> The bookings of the property that arrive on the day (the stored check-in date), confirmed, checked in or checked out
/// (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, the one definition of a stay that owes a communication to the authority), not a no-show; the guests are the rows of the check-in
/// (<see cref="StayGuest"/>), or the booker alone while there are none. Every read names the org of the property, so a context with its tenant filter off (the job) never mixes orgs.
/// The day is the calendar day of the stored check-in date: Switzerland and Italy share the time zone rules.</para>
/// <para><b>A closed day</b> is a day with a <see cref="SwissNotificationDeclaration"/> of the property, the day and <i>this regime's code</i> (<see cref="Casazen.Core.Entities.SwissNotificationDeclaration.RegimeCode"/>):
/// <see cref="ListPendingDaysAsync"/> is the one place that applies it. The rows without a code are the notification to the Ticino police and close nothing here; no migration is needed
/// (the table was designed for this, E30/F02/US02/T01).</para>
/// <para><b>Nothing is sent and nothing is stored but the declaration</b>: the guests are read from the check-in on every request and never copied, the answer holds personal data and is for
/// the web layer to authorize (<c>guest.read</c>) and never to cache. The declaration is told live to the hosts of the property, best effort once the row is saved
/// (<see cref="TicinoDeclarationLiveRefresh"/>): a push that cannot be queued is logged with ids and never fails the person who declared. The logs hold ids and the day, never a guest.</para>
/// </remarks>
public sealed class SwissNotificationService(
    AppDbContext context,
    IGuestRegistrationRegimeResolver regimes,
    IFeatureFlags featureFlags,
    TimeProvider? timeProvider = null,
    IPushNotificationService? pushNotifications = null,
    ILogger<SwissNotificationService>? logger = null) : ISwissNotificationService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<SwissNotificationDay> GetDayAsync(Guid propertyId, DateOnly day, CancellationToken cancellationToken = default)
    {
        var (property, resolution) = await LoadCoveredPropertyAsync(propertyId, cancellationToken);
        var regime = resolution.Regime!;
        var today = _clock.TodayInRomeAsDateOnly();

        var stays = await ReadStaysAsync(property, day, cancellationToken);
        var (guests, issues) = SwissAssistedNotification.Plan(stays.Select(s => s.Facts), today);
        var declaration = await DeclarationsOf(property, regime.Code, day)
            .AsNoTracking()
            .OrderBy(d => d.DeclaredAt)
            .ThenBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var declaredBy = declaration is null ? null : await DisplayNameAsync(declaration.DeclaredByUserId, cancellationToken);
        var pending = await ListPendingDaysAsync(property.OrgId, [property.Id], today, cancellationToken);

        return new SwissNotificationDay(
            property.Id,
            day,
            regime,
            resolution.EffectiveDuty,
            stays.Count,
            guests,
            issues,
            SwissAssistedNotification.DueAtUtc(regime, day, RomeCalendar.ZoneOf(property.Timezone), FirstArrival(stays)),
            declaration?.DeclaredAt,
            declaredBy,
            pending);
    }

    public async Task<IReadOnlyList<SwissPendingDay>> ListPendingDaysAsync(
        Guid? orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        // Behind the module's flag: off, nothing is raised. An empty list of properties is nobody, never everybody.
        if (!featureFlags.IsEnabled(FeatureFlags.GuestRegistrationRegimes) || propertyIds is { Count: 0 })
            return [];

        var propertyQuery = context.Properties.AsNoTracking().Where(p => p.Country.Trim().ToUpper() == "CH");
        if (orgId is { } onlyOrg)
            propertyQuery = propertyQuery.Where(p => p.OrgId == onlyOrg);
        if (propertyIds is not null)
            propertyQuery = propertyQuery.Where(p => propertyIds.Contains(p.Id));

        var swiss = await propertyQuery.ToListAsync(cancellationToken);
        if (swiss.Count == 0)
            return [];

        // The regime and the duty come from the one resolver: the properties the assisted notification covers and whose duty is raised, nothing else.
        var resolutions = await regimes.ResolveManyAsync(swiss, cancellationToken);
        var raised = swiss
            .Where(p => resolutions.TryGetValue(p.Id, out var resolution)
                        && SwissAssistedNotification.Covers(resolution.Regime)
                        && resolution.EffectiveDuty == GuestRegistrationEffectiveDuty.Required)
            .ToDictionary(p => p.Id, p => (Property: p, Regime: resolutions[p.Id].Regime!));
        if (raised.Count == 0)
            return [];

        var ids = raised.Keys.ToList();
        var firstDay = today.AddDays(-ISwissNotificationService.PendingLookbackDays);
        var from = StoredDay(firstDay);
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

        // THE rule of a closed day: a declaration of the property, the day and the code of the regime the property is under now. The cockpit and the alerts read only this list.
        var declared = (await context.SwissNotificationDeclarations
                .AsNoTracking()
                .Where(d => ids.Contains(d.PropertyId) && d.RegimeCode != null && d.ArrivalDay >= firstDay && d.ArrivalDay <= today)
                .Select(d => new { d.OrgId, d.PropertyId, d.RegimeCode, d.ArrivalDay })
                .ToListAsync(cancellationToken))
            .Select(d => (d.OrgId, d.PropertyId, d.RegimeCode, d.ArrivalDay))
            .ToHashSet();

        return arrivals
            .Where(a => raised.TryGetValue(a.PropertyId, out var owner) && owner.Property.OrgId == a.OrgId)
            .GroupBy(a => (a.PropertyId, Day: DateOnly.FromDateTime(a.CheckInDate)))
            .Where(g => !declared.Contains((raised[g.Key.PropertyId].Property.OrgId, g.Key.PropertyId, (string?)raised[g.Key.PropertyId].Regime.Code, g.Key.Day)))
            .Select(g =>
            {
                var (property, regime) = raised[g.Key.PropertyId];
                var recorded = g.Where(a => a.ArrivedAt.HasValue).Select(a => a.ArrivedAt!.Value).ToList();
                return new SwissPendingDay(
                    property.OrgId,
                    property.Id,
                    property.Name,
                    regime.Code,
                    g.Key.Day,
                    g.Count(),
                    SwissAssistedNotification.DueAtUtc(regime, g.Key.Day, RomeCalendar.ZoneOf(property.Timezone), recorded.Count == 0 ? null : recorded.Min()));
            })
            .OrderBy(d => d.Day)
            .ThenBy(d => d.PropertyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SwissDeclaration> DeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var (property, resolution) = await LoadCoveredPropertyAsync(propertyId, cancellationToken);
        var regimeCode = resolution.Regime!.Code;

        // A day that has not started cannot be declared: it would close the day of an arrival that has not happened and silence its alerts.
        if (day > _clock.TodayInRomeAsDateOnly())
            throw new DomainRuleException(SwissNotificationErrorCodes.DeclarationDayInFuture, SwissNotificationErrorCodes.DeclarationDayInFutureMessageKey);

        // Idempotent: a day declared already answers the declaration that exists, whoever repeats the call; nothing else changes and nobody is told again.
        var existing = await FirstDeclarationAsync(property, regimeCode, day, cancellationToken);
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
            Channel = SwissNotificationChannels.Assisted,
            RegimeCode = regimeCode,
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
            var winner = await FirstDeclarationAsync(property, regimeCode, day, cancellationToken);
            if (winner is null)
                throw;

            return await ToDeclarationAsync(winner, created: false, cancellationToken);
        }

        // Ids, the regime and the day only: the log never names a guest, and the person is the Auth0 subject.
        _logger.LogInformation(
            "Swiss notification ({RegimeCode}) of property {PropertyId} for {ArrivalDay} declared by {UserId}",
            regimeCode,
            property.Id,
            day,
            userId);
        TicinoDeclarationLiveRefresh.TellSwiss(pushNotifications, property.OrgId, property.Id, property.Name, day, declared: true, declaration.DeclaredAt, _logger);
        return await ToDeclarationAsync(declaration, created: true, cancellationToken);
    }

    public async Task<bool> UndeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var (property, resolution) = await LoadCoveredPropertyAsync(propertyId, cancellationToken);
        var regimeCode = resolution.Regime!.Code;
        var declarations = await DeclarationsOf(property, regimeCode, day).ToListAsync(cancellationToken);
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

            _logger.LogInformation(
                "Swiss notification declaration ({RegimeCode}) of property {PropertyId} for {ArrivalDay} was already removed by a co-host when {UserId} undid it",
                regimeCode,
                property.Id,
                day,
                userId);
            return false;
        }

        _logger.LogInformation(
            "Swiss notification declaration ({RegimeCode}) of property {PropertyId} for {ArrivalDay} undone by {UserId}",
            regimeCode,
            property.Id,
            day,
            userId);
        TicinoDeclarationLiveRefresh.TellSwiss(pushNotifications, property.OrgId, property.Id, property.Name, day, declared: false, _clock.GetUtcNow().UtcDateTime, _logger);
        return true;
    }

    /// <summary>
    /// The property (through the tenant filter of the context: another org's is not found) and what the one resolver says of it; refuses a property the assisted notification does not
    /// cover (422 <c>swiss_notification_not_applicable</c>): not Swiss, in Ticino, a canton that only keeps a register, or a place that is not known.
    /// </summary>
    private async Task<(Property Property, GuestRegistrationResolution Resolution)> LoadCoveredPropertyAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        var resolution = await regimes.ResolveAsync(property, cancellationToken);
        if (!SwissAssistedNotification.Covers(resolution.Regime))
            throw new DomainRuleException(SwissNotificationErrorCodes.NotApplicable, SwissNotificationErrorCodes.NotApplicableMessageKey);

        return (property, resolution);
    }

    private sealed record StayRead(TicinoStayFacts Facts, DateTime? ArrivedAt);

    /// <summary>The earliest arrival recorded among the stays, <c>null</c> when none is recorded.</summary>
    private static DateTime? FirstArrival(IEnumerable<StayRead> stays)
    {
        var recorded = stays.Where(s => s.ArrivedAt.HasValue).Select(s => s.ArrivedAt!.Value).ToList();
        return recorded.Count == 0 ? null : recorded.Min();
    }

    private async Task<List<StayRead>> ReadStaysAsync(Property property, DateOnly day, CancellationToken cancellationToken)
    {
        var from = StoredDay(day);
        var to = StoredDay(day.AddDays(1));
        return await ReadStaysAsync(property, b => b.CheckInDate >= from && b.CheckInDate < to, cancellationToken);
    }

    /// <summary>
    /// The stays of <paramref name="property"/> that <paramref name="which"/> selects and that owe a communication (the one definition of
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
    /// A guest of the stay as the plan reads it: the facts of the check-in, the same way the Ticino file reads them (the home is the one the check-in recorded for the guest; for the row linked to
    /// the booker's record, a part the check-in does not have is taken from that record). The tourist tax exemption of the row is health-related data and is never read here.
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

    /// <summary>The declarations of the notification of <paramref name="regimeCode"/> of <paramref name="property"/> for <paramref name="day"/>; the org of the property is always named.</summary>
    private IQueryable<SwissNotificationDeclaration> DeclarationsOf(Property property, string regimeCode, DateOnly day) =>
        context.SwissNotificationDeclarations
            .Where(d => d.OrgId == property.OrgId && d.PropertyId == property.Id && d.ArrivalDay == day && d.RegimeCode == regimeCode);

    /// <summary>The declaration of <paramref name="day"/> that stands (the first one, tracked), or <c>null</c> when the day is not declared.</summary>
    private Task<SwissNotificationDeclaration?> FirstDeclarationAsync(Property property, string regimeCode, DateOnly day, CancellationToken cancellationToken) =>
        DeclarationsOf(property, regimeCode, day).OrderBy(d => d.DeclaredAt).ThenBy(d => d.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task<SwissDeclaration> ToDeclarationAsync(SwissNotificationDeclaration declaration, bool created, CancellationToken cancellationToken) =>
        new(
            declaration.PropertyId,
            declaration.ArrivalDay,
            declaration.RegimeCode ?? string.Empty,
            declaration.DeclaredAt,
            await DisplayNameAsync(declaration.DeclaredByUserId, cancellationToken),
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
    /// A unique violation (23505) of the key of the declaration of a regime (<see cref="SwissNotificationDeclaration.NamedRegimeIndexName"/>), whatever the wrapper EF or the provider put
    /// around it: a co-host declared the same day first.
    /// </summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SwissNotificationDeclaration.NamedRegimeIndexName })
                return true;
        }

        return false;
    }

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
