using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// The data every test of the hub of messages (E05/F02/US01/T01) runs on, on top of the org of <see cref="HostScopeScenario"/> (two houses, the
/// collaborator «Solo alcuni» who was given only the first, a second org): conversations of the channels on each house, one with no property, one of the
/// other org, and the direct conversations of two stays. Times are counted back from <see cref="HostScopeScenario.Now"/>.
/// </summary>
internal sealed record MessageHubWorld(
    HostScopeWorld Scope,
    OtaMessageThread GrantedRequest,
    OtaMessageThread GrantedReservation,
    OtaMessageThread HiddenReservation,
    OtaMessageThread Unassigned,
    OtaMessageThread OtherOrg,
    Booking GrantedChannelBooking,
    Booking GrantedDirectBooking,
    Booking HiddenDirectBooking,
    OtaMessage GrantedAttachment,
    OtaMessage HiddenAttachment,
    OtaMessage OtherOrgAttachment)
{
    /// <summary>The address the channel manager gave for the files: the tests assert it never reaches a client.</summary>
    public const string SecretAddress = "https://files.octorate.example/attachments/SECRET-TOKEN-5f3a/photo.jpg?sig=SECRET-SIGNATURE";
}

internal static class MessageHubTestKit
{
    public static readonly DateTimeOffset Now = HostScopeScenario.Now;

    public static DateTime Ago(int minutes) => Now.UtcDateTime.AddMinutes(-minutes);

    public static async Task<MessageHubWorld> SeedAsync(AppDbContext db)
    {
        var scope = await HostScopeScenario.SeedAsync(db);
        var orgId = scope.OrgId;

        // The stays: a booking that came from a channel on the granted house, and a booking of the public checkout on each house.
        var channelGuest = NewGuest(orgId, "Jonas", "Berg");
        var directGuest = NewGuest(orgId, "Laura", "Conti");
        var hiddenGuest = NewGuest(orgId, "Pietro", "Gallo");
        db.Guests.AddRange(channelGuest, directGuest, hiddenGuest);
        var channelBooking = NewBooking(orgId, scope.Granted.Id, channelGuest.Id, BookingSource.Airbnb, 30);
        channelBooking.OctorateReservationId = "RES-1";
        channelBooking.OtaCommission = 24.50m;
        channelBooking.OtaPaidAmount = 100m;
        channelBooking.TotalPrice = 350m;
        var grantedDirect = NewBooking(orgId, scope.Granted.Id, directGuest.Id, BookingSource.Direct, 20);
        var hiddenDirect = NewBooking(orgId, scope.Hidden.Id, hiddenGuest.Id, BookingSource.Direct, 21);
        db.Bookings.AddRange(channelBooking, grantedDirect, hiddenDirect);
        db.Payments.Add(new Payment
        {
            OrgId = orgId,
            BookingId = grantedDirect.Id,
            Amount = 120m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = $"tx-{Guid.NewGuid():N}",
            ProcessedAt = Ago(5000),
            CreatedAt = Ago(5000),
            UpdatedAt = Ago(5000),
        });

        // The conversations of the channels.
        var grantedRequest = NewOtaThread(orgId, scope.Granted.Id, null, OtaChannel.Airbnb, OtaThreadKind.Request, "Marta R. (TEST)");
        var grantedReservation = NewOtaThread(orgId, scope.Granted.Id, channelBooking.Id, OtaChannel.Booking, OtaThreadKind.Reservation, "Jonas B. (TEST)");
        var hiddenReservation = NewOtaThread(orgId, scope.Hidden.Id, null, OtaChannel.Vrbo, OtaThreadKind.Reservation, "Anna S. (TEST)");
        var unassigned = NewOtaThread(orgId, null, null, OtaChannel.Expedia, OtaThreadKind.Request, "Luca V. (TEST)");
        var otherOrg = NewOtaThread(scope.OtherOrgId, scope.OtherOrgProperty.Id, null, OtaChannel.Airbnb, OtaThreadKind.Request, "Ospite Altrove (TEST)");
        db.OtaMessageThreads.AddRange(grantedRequest, grantedReservation, hiddenReservation, unassigned, otherOrg);

        // Messages: the request waits for an answer (the guest wrote last, one unread); the reservation was read and answered, by a person and then by
        // the assistant (its last message is outbound); the hidden one and the unassigned one wait for an answer. A message of each of the three
        // threads that matter has a file.
        Add(db, grantedRequest, OtaMessageDirection.Inbound, MessageOrigin.Guest, "Is the house free in August? (TEST)", 400, read: false);
        Add(db, grantedReservation, OtaMessageDirection.Inbound, MessageOrigin.Guest, "What time is check-in? (TEST)", 900, read: true);
        var grantedAttachment = Add(db, grantedReservation, OtaMessageDirection.Inbound, MessageOrigin.Guest, string.Empty, 880, read: true);
        grantedAttachment.AttachmentUrl = MessageHubWorld.SecretAddress;
        grantedAttachment.AttachmentContentType = "image/jpeg";
        Add(db, grantedReservation, OtaMessageDirection.Outbound, MessageOrigin.Person, "From 3 pm (TEST)", 800, read: false);
        var assistant = Add(db, grantedReservation, OtaMessageDirection.Outbound, MessageOrigin.Assistant, "Parking is free (TEST)", 700, read: false);
        assistant.RuleCode = "parking";
        Add(db, hiddenReservation, OtaMessageDirection.Inbound, MessageOrigin.Guest, "Hidden house question (TEST)", 300, read: false);
        var hiddenAttachment = Add(db, hiddenReservation, OtaMessageDirection.Inbound, MessageOrigin.Guest, string.Empty, 290, read: false);
        hiddenAttachment.AttachmentUrl = MessageHubWorld.SecretAddress;
        hiddenAttachment.AttachmentContentType = "image/png";
        Add(db, unassigned, OtaMessageDirection.Inbound, MessageOrigin.Guest, "Unassigned question (TEST)", 200, read: false);
        var otherAttachment = Add(db, otherOrg, OtaMessageDirection.Inbound, MessageOrigin.Guest, string.Empty, 100, read: false);
        otherAttachment.AttachmentUrl = MessageHubWorld.SecretAddress;
        otherAttachment.AttachmentContentType = "image/jpeg";
        Tidy(db, grantedRequest, grantedReservation, hiddenReservation, unassigned, otherOrg);

        // The direct conversations: the granted stay's guest waits for an answer, the hidden one was answered.
        AddDirectThread(db, grantedDirect, guestWroteAgo: 150, hostWroteAgo: null);
        AddDirectThread(db, hiddenDirect, guestWroteAgo: 500, hostWroteAgo: 450);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new MessageHubWorld(
            scope, grantedRequest, grantedReservation, hiddenReservation, unassigned, otherOrg,
            channelBooking, grantedDirect, hiddenDirect, grantedAttachment, hiddenAttachment, otherAttachment);
    }

    public static MessageHubService Service(
        AppDbContext db,
        IGuestMessageService? guestMessages = null,
        IChannelMappingService? channelMapping = null,
        IOtaAttachmentFetcher? attachments = null) =>
        new(
            db,
            guestMessages ?? Mock.Of<IGuestMessageService>(),
            channelMapping ?? Mock.Of<IChannelMappingService>(),
            attachments ?? Mock.Of<IOtaAttachmentFetcher>(),
            NullLogger<MessageHubService>.Instance,
            new FixedTimeProvider(Now));

    private static Guest NewGuest(Guid orgId, string first, string last) => new()
    {
        OrgId = orgId,
        FirstName = first,
        LastName = last,
        Email = $"{Guid.NewGuid():N}@example.com",
    };

    private static Booking NewBooking(Guid orgId, Guid propertyId, Guid guestId, BookingSource source, int daysAhead) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        GuestId = guestId,
        CheckInDate = HostScopeScenario.Day(2026, 10, 9).AddDays(daysAhead),
        CheckOutDate = HostScopeScenario.Day(2026, 10, 9).AddDays(daysAhead + 3),
        NumberOfGuests = 3,
        NumberOfAdults = 2,
        NumberOfChildren = 1,
        Status = BookingStatus.Confirmed,
        Source = source,
        BasePrice = 300m,
        TotalPrice = 300m,
    };

    private static OtaMessageThread NewOtaThread(
        Guid orgId, Guid? propertyId, Guid? bookingId, OtaChannel channel, OtaThreadKind kind, string guestName)
    {
        return new OtaMessageThread
        {
            OrgId = orgId,
            PropertyId = propertyId,
            BookingId = bookingId,
            OctorateThreadId = $"T-{Guid.NewGuid():N}"[..20],
            OctorateAccommodationId = "ACC-1",
            ChannelSource = channel,
            Kind = kind,
            GuestDisplayName = guestName,
            LastMessageAt = Ago(1000),
            CreatedAt = Ago(2000),
            UpdatedAt = Ago(2000),
        };
    }

    private static OtaMessage Add(AppDbContext db, OtaMessageThread thread, OtaMessageDirection direction, MessageOrigin origin, string text, int minutesAgo, bool read)
    {
        var message = new OtaMessage
        {
            OrgId = thread.OrgId,
            ThreadId = thread.Id,
            OctorateMessageId = $"M-{Guid.NewGuid():N}"[..20],
            Direction = direction,
            SenderName = direction == OtaMessageDirection.Inbound ? thread.GuestDisplayName ?? string.Empty : "Host (TEST)",
            Text = text,
            SentAt = Ago(minutesAgo),
            ReadAt = read ? Ago(minutesAgo - 1) : null,
            DeliveryState = OtaMessageDeliveryState.Sent,
            Origin = origin,
            CreatedAt = Ago(minutesAgo),
        };
        db.OtaMessages.Add(message);
        return message;
    }

    /// <summary>Sets the counters of each thread from its messages, as the sync does.</summary>
    private static void Tidy(AppDbContext db, params OtaMessageThread[] threads)
    {
        foreach (var thread in threads)
        {
            var messages = db.ChangeTracker.Entries<OtaMessage>().Select(e => e.Entity).Where(m => m.ThreadId == thread.Id).ToList();
            var newest = messages.OrderBy(m => m.SentAt).Last();
            thread.LastMessageAt = newest.SentAt;
            thread.LastMessageDirection = newest.Direction;
            thread.UnreadCount = messages.Count(m => m.Direction == OtaMessageDirection.Inbound && m.ReadAt is null);
        }
    }

    private static void AddDirectThread(AppDbContext db, Booking booking, int guestWroteAgo, int? hostWroteAgo)
    {
        var thread = new GuestThread
        {
            OrgId = booking.OrgId,
            PropertyId = booking.PropertyId,
            BookingId = booking.Id,
            CreatedAt = Ago(guestWroteAgo),
            LastMessageAt = Ago(hostWroteAgo ?? guestWroteAgo),
            LastGuestMessageAt = Ago(guestWroteAgo),
            LastHostMessageAt = hostWroteAgo is { } host ? Ago(host) : null,
            HostReadAt = hostWroteAgo is { } ? Ago(guestWroteAgo) : null,
        };
        db.GuestThreads.Add(thread);
        db.GuestMessages.Add(new GuestMessage
        {
            OrgId = booking.OrgId,
            ThreadId = thread.Id,
            Sender = GuestMessageSender.Guest,
            Text = "Direct question (TEST)",
            CreatedAt = Ago(guestWroteAgo),
        });
        if (hostWroteAgo is { } hostAgo)
        {
            db.GuestMessages.Add(new GuestMessage
            {
                OrgId = booking.OrgId,
                ThreadId = thread.Id,
                Sender = GuestMessageSender.Host,
                Text = "Direct answer (TEST)",
                CreatedAt = Ago(hostAgo),
                AuthorUserId = "auth0|titolare",
            });
        }
    }
}
