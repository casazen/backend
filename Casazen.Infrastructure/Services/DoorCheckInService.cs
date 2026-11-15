using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDoorCheckInService" />
/// <remarks>
/// <para><b>The door token</b> is <see cref="Property.DoorToken"/>: random, unique, made on the first request and replaced by «Rigenera». The first time it is made with a
/// conditional update (only while the property has none), so two requests at once end with one token and both answer it.</para>
/// <para><b>The stay</b> is looked for among the stays of THAT property only (the door token names it), in the one definition of an active stay
/// (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> without the stays already checked out, and no no-show mark), that arrive yesterday, today or tomorrow or are in
/// progress (<see cref="DoorCheckInRules.IsAtTheDoor"/>, days of Europe/Rome), with the arrival date the guest typed and, as it typed, the booking code or the surname of the
/// booker or of a guest who leaves a document. It is found only when <b>exactly one</b> stay matches: a surname that two stays share opens none.</para>
/// <para><b>Privacy.</b> Nothing but the token leaves, and every way of not finding a stay is the same <see cref="DoorCheckInResult.NotFound"/>. The log says the property and the
/// outcome, never a code, a surname or a date that was typed. The rate limit is the policy of the endpoint.</para>
/// </remarks>
public sealed class DoorCheckInService(
    AppDbContext db,
    IGuestCheckInService checkIn,
    ILogger<DoorCheckInService> logger,
    TimeProvider? timeProvider = null) : IDoorCheckInService
{
    /// <summary>Attempts to make or replace a token when another request wins at the same time (or, one in 2^192, makes the same token).</summary>
    private const int MaxAttempts = 3;

    /// <summary>The stays the door serves: the active ones (the one definition of the Alloggiati job) that are not checked out.</summary>
    private static readonly BookingStatus[] DoorStatuses =
        [.. AlloggiatiStatusRules.ActiveBookingStatuses.Where(status => status != BookingStatus.CheckedOut)];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<string?> GetOrCreateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var property = await db.Properties.AsNoTracking()
                .Where(p => p.Id == propertyId && p.OrgId == orgId)
                .Select(p => new { p.DoorToken })
                .FirstOrDefaultAsync(cancellationToken);
            if (property is null)
                return null;

            if (!string.IsNullOrEmpty(property.DoorToken))
                return property.DoorToken;

            // False: another request set the token first (or the property is gone): look again and answer what is stored.
            var token = PropertyDoorTokens.New();
            if (await SetFirstTokenAsync(orgId, propertyId, token, cancellationToken))
                return token;
        }

        throw new InvalidOperationException($"The door token of property {propertyId} could not be made: the property kept changing.");
    }

    public async Task<string?> RotateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken);
            if (property is null)
                return null;

            property.DoorToken = PropertyDoorTokens.New();
            property.UpdatedAt = UtcNow();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return property.DoorToken;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // One in 2^192: the same token as another property. Look again with a new one.
                db.Entry(property).State = EntityState.Detached;
            }
        }
    }

    public async Task<DoorCheckInResult> OpenAsync(string doorToken, DoorCheckInInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // A made-up token never reaches the database; a date that was not typed is not a stay.
        if (!PropertyDoorTokens.IsWellFormed(doorToken) || input.ArrivalDate is not { } arrival)
            return DoorCheckInResult.NotFound;

        var byCode = !string.IsNullOrWhiteSpace(input.BookingCode);
        string? code = null;
        string? surname = null;
        if (byCode)
        {
            if (input.BookingCode!.Length > DoorCheckInRules.InputMaxLength || !BookingCodes.TryNormalize(input.BookingCode, out code))
                return DoorCheckInResult.NotFound;
        }
        else
        {
            surname = DoorCheckInRules.NormalizeSurname(input.Surname);
            if (surname is null)
                return DoorCheckInResult.NotFound;
        }

        var property = await db.Properties.AsNoTracking()
            .Where(p => p.DoorToken == doorToken)
            .Select(p => new { p.Id, p.OrgId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
            return DoorCheckInResult.NotFound;

        var today = _clock.TodayInRomeAsDateOnly();
        var windowStart = RomeCalendar.StartOfDayUtc(today.AddDays(-1));
        var windowEnd = RomeCalendar.StartOfDayUtc(today.AddDays(2));

        // Only the stays of the property the door token names, within a margin of the days the door serves (the exact days are decided below).
        var stays = await db.Bookings.AsNoTracking()
            .Where(b => b.PropertyId == property.Id
                && DoorStatuses.Contains(b.Status)
                && b.NoShowAt == null
                && b.CheckedOutAt == null
                && b.CheckInDate < windowEnd
                && b.CheckOutDate >= windowStart)
            .Select(b => new
            {
                b.Id,
                b.OrgId,
                b.BookingCode,
                b.CheckInDate,
                b.CheckOutDate,
                BookerLastName = b.Guest.LastName,
                Guests = b.StayGuests.Select(g => new { g.Type, g.LastName }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var matches = stays
            .Where(s => RomeCalendar.DateInRome(s.CheckInDate) == arrival
                && DoorCheckInRules.IsAtTheDoor(today, RomeCalendar.DateInRome(s.CheckInDate), RomeCalendar.DateInRome(s.CheckOutDate)))
            .Where(s => byCode
                ? string.Equals(s.BookingCode, code, StringComparison.Ordinal)
                : DoorCheckInRules.NormalizeSurname(s.BookerLastName) == surname
                    || s.Guests.Any(g => AlloggiatiRecordRules.RequiresDocument(g.Type) && DoorCheckInRules.NormalizeSurname(g.LastName) == surname))
            .Take(2)
            .ToList();

        // Not one stay, or more than one: the same answer, so the door never says which part of the data was wrong.
        if (matches.Count != 1)
        {
            logger.LogInformation("Door check-in of property {PropertyId}: no stay matched", property.Id);
            return DoorCheckInResult.NotFound;
        }

        var stay = matches[0];

        // A check-in that is complete has no identity step left to reach: the link of a completed session would answer 404 anyway.
        var current = await checkIn.GetSessionForBookingAsync(stay.Id);
        if (current?.IsCompleted == true)
        {
            logger.LogInformation("Door check-in of property {PropertyId}: the stay has no check-in left to do", property.Id);
            return DoorCheckInResult.NotFound;
        }

        var link = await checkIn.IssueLinkAsync(stay.Id, stay.OrgId);
        logger.LogInformation("Door check-in of property {PropertyId}: a link was issued for booking {BookingId}", property.Id, stay.Id);
        return new DoorCheckInResult(link.Token);
    }

    /// <summary>
    /// Sets the first token of a property: true when this call did, false when the property already has one (another request was faster) or is gone. A relational
    /// database does it with one conditional update; the in-memory database of the unit tests, which has none, with the tracked entity.
    /// </summary>
    private async Task<bool> SetFirstTokenAsync(Guid orgId, Guid propertyId, string token, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        if (db.Database.IsRelational())
        {
            var changed = await db.Properties
                .Where(p => p.Id == propertyId && p.OrgId == orgId && p.DoorToken == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.DoorToken, token)
                        .SetProperty(p => p.UpdatedAt, now),
                    cancellationToken);
            return changed == 1;
        }

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId && p.DoorToken == null, cancellationToken);
        if (property is null)
            return false;

        property.DoorToken = token;
        property.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
