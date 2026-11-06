using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the helpers of the composer (the templates, the translation) need to know about the conversation they work for (E05/F02/US02/T02): the property
/// and its clock, the booking it is about and its guest, the host, and where the conversation is on the channel manager. Read once, in the reach of the
/// caller, and carried as plain values.
/// </summary>
/// <param name="OtaThreadId">The row of the conversation of a channel (<c>OtaMessageThread.Id</c>); <c>null</c> for the conversation of a direct booking.</param>
/// <param name="BookingId">The booking the conversation is about; <c>null</c> for a request with no booking, or when the booking it is linked to is of another house.</param>
/// <param name="GuestErased">The guest of the booking was erased or anonymized, or the conversation was anonymized with it: its name is gone and is not used.</param>
internal sealed record ConversationFacts(
    Guid OrgId,
    MessageThreadKey Key,
    Guid? OtaThreadId,
    string? OctorateThreadId,
    string? OctorateAccommodationId,
    Guid? PropertyId,
    string? PropertyName,
    TimeOnly? CheckInFrom,
    TimeOnly? CheckOutBy,
    Guid? BookingId,
    string? BookingCode,
    DateTime? CheckInDate,
    DateTime? CheckOutDate,
    string? GuestFirstName,
    string? GuestLastName,
    string? ThreadGuestName,
    bool GuestErased,
    string? HostName,
    string OrgDisplayName)
{
    /// <summary>The first name of the guest as a template writes it; <c>null</c> when the conversation does not know it or the guest was erased.</summary>
    public string? GuestFirstNameForTemplates
    {
        get
        {
            if (GuestErased)
                return null;

            if (!string.IsNullOrWhiteSpace(GuestFirstName))
                return GuestFirstName.Trim();

            return ThreadGuestName?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
    }

    /// <summary>How the host is called in a template: the name it chose for the site, else the name of the organization (not the placeholder of one that never chose).</summary>
    public string? HostNameForTemplates =>
        !string.IsNullOrWhiteSpace(HostName) ? HostName.Trim()
        : !string.IsNullOrWhiteSpace(OrgDisplayName) && !string.Equals(OrgDisplayName.Trim(), Org.PlaceholderName, StringComparison.Ordinal) ? OrgDisplayName.Trim()
        : null;
}

/// <summary>
/// The one place the composer's helpers find the conversation of a key (E05/F02/US02/T02), in the reach of the caller: the same rule as the hub. The org of
/// the scope is named in every query and <c>InScope</c> narrows it to the properties the caller reaches, so a conversation of another org, or out of the
/// reach of a collaborator «Solo alcuni», or one with no property for a scope that does not reach every property, is a 404 <c>thread_not_found</c> and
/// reveals nothing about itself.
/// </summary>
internal static class MessageConversationLookup
{
    public static async Task<ConversationFacts> FindAsync(AppDbContext db, HostScope scope, MessageThreadKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == scope.OrgId)
            .Select(o => new { o.HostName, o.DisplayName })
            .FirstOrDefaultAsync(ct)
            ?? throw ThreadNotFound();

        return key.Source == MessageThreadSource.Ota
            ? await FindOtaAsync(db, scope, key, org.HostName, org.DisplayName, ct)
            : await FindDirectAsync(db, scope, key, org.HostName, org.DisplayName, ct);
    }

    private static async Task<ConversationFacts> FindOtaAsync(
        AppDbContext db,
        HostScope scope,
        MessageThreadKey key,
        string? hostName,
        string orgDisplayName,
        CancellationToken ct)
    {
        var thread = await db.OtaMessageThreads
            .AsNoTracking()
            .Where(t => t.OrgId == scope.OrgId && t.Id == key.Id)
            .InScope(scope)
            .Select(t => new
            {
                t.Id,
                t.PropertyId,
                t.BookingId,
                t.GuestDisplayName,
                t.OctorateThreadId,
                t.OctorateAccommodationId,
                Anonymized = t.AnonymizedAt != null,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw ThreadNotFound();

        var property = thread.PropertyId is { } propertyId ? await PropertyOfAsync(db, scope.OrgId, propertyId, ct) : null;
        var booking = thread.BookingId is { } bookingId ? await BookingOfAsync(db, scope.OrgId, bookingId, ct) : null;

        // A booking of another house than the conversation's is not the stay it is about (the hub applies the same rule to its card).
        if (booking is not null && thread.PropertyId is { } threadProperty && booking.PropertyId != threadProperty)
            booking = null;

        // The property of a conversation with no property is the one of its booking, only for a scope that reaches every property (it is the one that sees it).
        property ??= booking is null ? null : await PropertyOfAsync(db, scope.OrgId, booking.PropertyId, ct);

        return Build(scope.OrgId, key, thread.Id, thread.OctorateThreadId, thread.OctorateAccommodationId, property, booking, thread.GuestDisplayName, thread.Anonymized, hostName, orgDisplayName);
    }

    private static async Task<ConversationFacts> FindDirectAsync(
        AppDbContext db,
        HostScope scope,
        MessageThreadKey key,
        string? hostName,
        string orgDisplayName,
        CancellationToken ct)
    {
        var bookingId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == key.Id)
            .InScope(scope)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw ThreadNotFound();

        var booking = await BookingOfAsync(db, scope.OrgId, bookingId, ct) ?? throw ThreadNotFound();
        var property = await PropertyOfAsync(db, scope.OrgId, booking.PropertyId, ct);
        return Build(scope.OrgId, key, null, null, null, property, booking, null, false, hostName, orgDisplayName);
    }

    private static ConversationFacts Build(
        Guid orgId,
        MessageThreadKey key,
        Guid? otaThreadId,
        string? octorateThreadId,
        string? octorateAccommodationId,
        PropertyFacts? property,
        BookingFacts? booking,
        string? threadGuestName,
        bool anonymized,
        string? hostName,
        string orgDisplayName) =>
        new(
            orgId,
            key,
            otaThreadId,
            octorateThreadId,
            octorateAccommodationId,
            property?.Id,
            property?.Name,
            property?.CheckInFrom,
            property?.CheckOutBy,
            booking?.Id,
            booking?.BookingCode,
            booking?.CheckInDate,
            booking?.CheckOutDate,
            booking?.GuestFirstName,
            booking?.GuestLastName,
            anonymized ? null : threadGuestName,
            anonymized || booking?.GuestErased == true,
            hostName,
            orgDisplayName);

    private static Task<PropertyFacts?> PropertyOfAsync(AppDbContext db, Guid orgId, Guid propertyId, CancellationToken ct) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .Select(p => new PropertyFacts(p.Id, p.Name, p.CheckInFrom, p.CheckOutBy))
            .FirstOrDefaultAsync(ct);

    private static Task<BookingFacts?> BookingOfAsync(AppDbContext db, Guid orgId, Guid bookingId, CancellationToken ct) =>
        db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Select(b => new BookingFacts(
                b.Id,
                b.PropertyId,
                b.BookingCode,
                b.CheckInDate,
                b.CheckOutDate,
                b.Guest.FirstName,
                b.Guest.LastName,
                b.Guest.DataAnonymizedDate != null || b.Guest.IsDeleted || b.Guest.ErasureRequested))
            .FirstOrDefaultAsync(ct);

    private static NotFoundException ThreadNotFound() =>
        new("The message thread was not found or is not within reach") { Code = MessageHubService.ThreadNotFoundCode, MessageKey = "MessageThreadNotFound" };

    private sealed record PropertyFacts(Guid Id, string Name, TimeOnly? CheckInFrom, TimeOnly? CheckOutBy);

    private sealed record BookingFacts(
        Guid Id,
        Guid PropertyId,
        string BookingCode,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        string GuestFirstName,
        string GuestLastName,
        bool GuestErased);
}
