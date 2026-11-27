using System.Security.Cryptography;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestRegisterService" />
/// <remarks>
/// <para><b>Which stays.</b> The bookings of the property that arrive in the period (the stored check-in day, both ends counted), confirmed, checked in or checked out
/// (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, the one definition of a stay that owes a police communication), not a no-show; the guests are the rows of the check-in
/// (<see cref="StayGuest"/>), or the booker alone while there are none (<see cref="TicinoStayFacts.From"/>, the same reading the Ticino file uses). Every read names the org of the property.</para>
/// <para><b>Which property.</b> One that the regime resolver puts under a Swiss regime (<see cref="GuestRegistrationRegime.IsSwiss"/>), the unknown Swiss place included: the register is the
/// host's own table, it needs no canton. An Italian property (or one of another country) answers <c>guest_register_not_available</c>, a 404.</para>
/// <para><b>The audit</b>: <see cref="GenerateAsync"/> writes one <see cref="SwissNotificationFileEvent"/> (format <c>register</c>, the first day of the period, the lines, the hash, the person who
/// asked; no personal data), in the request of the file, so a register never leaves unaudited. Nothing is logged here: the web layer logs ids, dates and counts, never a guest.</para>
/// </remarks>
public sealed class GuestRegisterService(
    AppDbContext context,
    IGuestRegistrationRegimeResolver regimes,
    IDocumentTexts documentTexts,
    TimeProvider? timeProvider = null) : IGuestRegisterService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestRegisterInfo> GetInfoAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var register = await ReadAsync(propertyId, from, to, cancellationToken);
        return new GuestRegisterInfo(register.Property.Id, from, to, register.Rows.Count, register.Regime.RetentionYears, register.Regime.Code);
    }

    public async Task<GuestRegisterDocument> GenerateAsync(
        Guid propertyId,
        DateOnly from,
        DateOnly to,
        GuestRegisterRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var register = await ReadAsync(propertyId, from, to, cancellationToken);

        // The words of the file are the resource keys, read in the language of the request: no text of the file is written in the code.
        var language = DocumentLanguages.Normalize(request?.Language);
        var content = GuestRegisterFile.Write(register.Rows, key => documentTexts.Get(key, language));
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // One audit row for every register, with no personal data (NF-130): the first day of the period, the lines, the hash and the person who asked. A row that cannot
        // be written fails the request: no register leaves unaudited. Added through the set, never through a collection of a parent: the Guid is preset.
        context.SwissNotificationFileEvents.Add(new SwissNotificationFileEvent
        {
            OrgId = register.Property.OrgId,
            PropertyId = register.Property.Id,
            ArrivalDay = from,
            Format = SwissNotificationFileFormats.Register,
            RowCount = register.Rows.Count,
            Sha256 = hash,
            CreatedByUserId = request?.UserId,
            CreatedAt = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime),
        });
        await context.SaveChangesAsync(cancellationToken);

        return new GuestRegisterDocument(
            content,
            GuestRegisterFile.FileName(from, to),
            GuestRegisterFile.ContentType,
            hash,
            register.Rows.Count,
            register.Regime.RetentionYears,
            register.Regime.Code);
    }

    private sealed record RegisterRead(Property Property, GuestRegistrationRegime Regime, IReadOnlyList<GuestRegisterRow> Rows);

    /// <summary>The period, the property, its regime and the lines of the register: what both the info and the file are made of.</summary>
    private async Task<RegisterRead> ReadAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (GuestRegisterFile.PeriodErrorOf(from, to) is { } periodError)
        {
            throw periodError == GuestRegisterFile.PeriodTooLongCode
                ? new DomainRuleException(periodError, "SwissPeriodTooLong", GuestRegisterFile.MaxPeriodDays)
                : new DomainRuleException(periodError, "SwissPeriodInvalid");
        }

        // The tenant filter of the context: another org's property is not found, the same answer as a property that does not exist.
        var property = await context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        // The one place that says under which regime a property is: a Swiss one has a register (the unknown Swiss place too), an Italian one has the Alloggiati file.
        var resolution = await regimes.ResolveAsync(property, cancellationToken);
        if (resolution.Regime is not { IsSwiss: true } regime)
        {
            throw new NotFoundException($"Property {propertyId} is not in Switzerland: it has no guest register")
            {
                Code = GuestRegisterErrorCodes.NotAvailable,
                MessageKey = GuestRegisterErrorCodes.NotAvailableMessageKey,
            };
        }

        var rows = await ReadRowsAsync(property, from, to, cancellationToken);
        return new RegisterRead(property, regime, rows);
    }

    /// <summary>The lines of the register of <paramref name="property"/>: the guests of the stays that arrive from <paramref name="from"/> to <paramref name="to"/>, both included, by arrival and stay.</summary>
    private async Task<IReadOnlyList<GuestRegisterRow>> ReadRowsAsync(Property property, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var first = StoredDay(from);
        var end = StoredDay(to.AddDays(1));

        // A stay owes a police communication when it is confirmed, checked in or checked out and is not a no-show: the one definition of a stay that counts.
        var bookings = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.OrgId == property.OrgId
                && b.PropertyId == property.Id
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null
                && b.CheckInDate >= first
                && b.CheckInDate < end)
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

        return bookings
            .SelectMany(b => GuestRegisterFile.RowsOf(TicinoStayFacts.From(b, guests[b.Id])))
            .ToList();
    }

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
