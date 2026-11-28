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
/// <para><b>The link</b> is the open link of the stay, renewed in place (<see cref="IGuestCheckInService.ReissueLinkAsync"/>): a new token and a new expiry on the same session, so the old
/// link dies but the consent, the document read, the language and the attempts of the face check already spent stay with the guest; a stay with no open link gets a new session.</para>
/// <para><b>Privacy.</b> Nothing but the token leaves, and every way of not finding a stay is the same <see cref="DoorCheckInResult.NotFound"/>. The log says the property and the
/// outcome, never a code, a surname or a date that was typed. The rate limits are the ones of the endpoint (per client IP); next to them the failed attempts of each door are counted
/// for every client together (<see cref="IDoorCheckInFailureTracker"/>, <see cref="DoorCheckInRules.FailureLimit"/> within <see cref="DoorCheckInRules.FailureWindow"/>): a door that had them is
/// closed to everybody, with the same <see cref="DoorCheckInResult.NotFound"/>, the right data included; the attempts that open never count.</para>
/// </remarks>
public sealed class DoorCheckInService(
    AppDbContext db,
    IGuestCheckInService checkIn,
    ILogger<DoorCheckInService> logger,
    TimeProvider? timeProvider = null,
    IDoorCheckInFailureTracker? failures = null) : IDoorCheckInService
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
                db.Entry(property).State = EntityState.Detached;
            }
        }
    }

    public async Task<DoorCheckInResult> OpenAsync(string doorToken, DoorCheckInInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

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

        if (failures?.IsLocked(doorToken) == true)
        {
            logger.LogInformation("Door check-in of property {PropertyId}: the door is closed, too many failed attempts", property.Id);
            return DoorCheckInResult.NotFound;
        }

        var today = _clock.TodayInRomeAsDateOnly();
        var windowStart = RomeCalendar.StartOfDayUtc(today.AddDays(-1));
        var windowEnd = RomeCalendar.StartOfDayUtc(today.AddDays(2));

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

        if (matches.Count != 1)
        {
            logger.LogInformation("Door check-in of property {PropertyId}: no stay matched", property.Id);
            RecordFailure(doorToken, property.Id);
            return DoorCheckInResult.NotFound;
        }

        var stay = matches[0];

        var current = await checkIn.GetSessionForBookingAsync(stay.Id);
        if (current?.IsCompleted == true)
        {
            logger.LogInformation("Door check-in of property {PropertyId}: the stay has no check-in left to do", property.Id);
            RecordFailure(doorToken, property.Id);
            return DoorCheckInResult.NotFound;
        }

        var link = await checkIn.ReissueLinkAsync(stay.Id, stay.OrgId);
        logger.LogInformation("Door check-in of property {PropertyId}: a link was issued for booking {BookingId}", property.Id, stay.Id);
        return new DoorCheckInResult(link.Token);
    }

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

    private void RecordFailure(string doorToken, Guid propertyId)
    {
        if (failures?.RecordFailure(doorToken) == true)
        {
            logger.LogWarning(
                "Door check-in of property {PropertyId}: {Limit} failed attempts, the door is closed until some of them are out of the window",
                propertyId, DoorCheckInRules.FailureLimit);
        }
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
