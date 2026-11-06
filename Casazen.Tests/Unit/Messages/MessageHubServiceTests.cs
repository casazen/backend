using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.DTOs.Messages;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US01/T01 (NF-61, NF-64): the list, the thread detail, the read mark and the badges of the hub, on the in-memory provider. What only the
/// real SQL can show (that the one statement translates and runs) is proved by <see cref="MessageHubNpgsqlTranslationTests"/> and by CI.
/// </summary>
public class MessageHubServiceTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private HostScope OrgWide => _world.Scope.OrgWide;

    private MessageHubService Service(
        IGuestMessageService? guestMessages = null,
        IChannelMappingService? channelMapping = null,
        IOtaAttachmentFetcher? attachments = null) =>
        MessageHubTestKit.Service(_db, guestMessages, channelMapping, attachments);

    private static MessageThreadKey Ota(OtaMessageThread thread) => MessageThreadKey.ForOta(thread.Id);

    // ---- The list -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_PutsBothSourcesInOneList_TheLatestMessageFirst_AndNeverTheOtherOrg()
    {
        var page = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        // The other org's conversation is not there; the six of this org are, by the time of their last message.
        Assert.Equal(6, page.TotalCount);
        Assert.Equal(
            [
                MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id),
                Ota(_world.Unassigned),
                Ota(_world.HiddenReservation),
                Ota(_world.GrantedRequest),
                MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id),
                Ota(_world.GrantedReservation),
            ],
            page.Items.Select(i => i.Key));
        Assert.DoesNotContain(page.Items, i => i.Key == Ota(_world.OtherOrg));
        Assert.Equal(page.Items.Select(i => i.LastMessageAt).OrderDescending(), page.Items.Select(i => i.LastMessageAt));
    }

    [Fact]
    public async Task List_IsPaged()
    {
        var service = Service();
        var first = await service.ListAsync(OrgWide, new MessageHubFilter(), 1, 4);
        var second = await service.ListAsync(OrgWide, new MessageHubFilter(), 2, 4);
        var beyond = await service.ListAsync(OrgWide, new MessageHubFilter(), 3, 4);

        Assert.Equal(new[] { 4, 2, 0 }, new[] { first.Items.Count, second.Items.Count, beyond.Items.Count });
        Assert.All(new[] { first, second, beyond }, p => Assert.Equal(6, p.TotalCount));
        Assert.Equal(4, first.PageSize);
        Assert.Equal(2, second.Page);
        Assert.Empty(first.Items.Select(i => i.Key).Intersect(second.Items.Select(i => i.Key)));
    }

    [Fact]
    public async Task List_ClampsThePageSizeAndThePageNumber()
    {
        var page = await Service().ListAsync(OrgWide, new MessageHubFilter(), -3, 5000);

        Assert.Equal(1, page.Page);
        Assert.Equal(GuestMessageRules.MaxPageSize, page.PageSize);
    }

    [Fact]
    public async Task List_NeedsReply_KeepsTheOpenThreadsWhoseLastMessageIsTheGuests_OfBothSources()
    {
        var service = Service();

        var toAnswer = await service.ListAsync(OrgWide, new MessageHubFilter(NeedsReply: true), 1, 20);
        var others = await service.ListAsync(OrgWide, new MessageHubFilter(NeedsReply: false), 1, 20);

        Assert.Equal(
            [
                MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id),
                Ota(_world.Unassigned),
                Ota(_world.HiddenReservation),
                Ota(_world.GrantedRequest),
            ],
            toAnswer.Items.Select(i => i.Key));
        Assert.All(toAnswer.Items, i => Assert.True(i.NeedsReply));
        Assert.Equal(
            [MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id), Ota(_world.GrantedReservation)],
            others.Items.Select(i => i.Key));
        Assert.All(others.Items, i => Assert.False(i.NeedsReply));
    }

    [Fact]
    public async Task List_AnArchivedConversationOfAChannel_DoesNotNeedAnAnswer()
    {
        var archived = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedRequest.Id);
        archived.IsArchived = true;
        await _db.SaveChangesAsync();

        var toAnswer = await Service().ListAsync(OrgWide, new MessageHubFilter(NeedsReply: true), 1, 20);
        var all = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        Assert.DoesNotContain(toAnswer.Items, i => i.Key == Ota(_world.GrantedRequest));
        var row = Assert.Single(all.Items, i => i.Key == Ota(_world.GrantedRequest));
        Assert.Equal(MessageHubThreadState.Archived, row.State);
        Assert.False(row.NeedsReply);
    }

    [Fact]
    public async Task List_ADirectThreadPastTheSeventhDayAfterTheCheckOut_IsClosed_AndNeedsNoAnswer()
    {
        var booking = await _db.Bookings.FirstAsync(b => b.Id == _world.GrantedDirectBooking.Id);
        booking.CheckInDate = HostScopeScenario.Day(2026, 9, 1);
        booking.CheckOutDate = HostScopeScenario.Day(2026, 9, 4);
        await _db.SaveChangesAsync();

        var all = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        var row = Assert.Single(all.Items, i => i.Key == MessageThreadKey.ForDirect(booking.Id));
        Assert.Equal(MessageHubThreadState.Closed, row.State);
        Assert.False(row.NeedsReply);
    }

    [Fact]
    public async Task List_Filters_ByProperty_Channel_Booking_Unread_AndKind()
    {
        var service = Service();
        var grantedId = _world.Scope.Granted.Id;

        var byProperty = await service.ListAsync(OrgWide, new MessageHubFilter(PropertyId: grantedId), 1, 20);
        Assert.Equal(
            [MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), Ota(_world.GrantedRequest), Ota(_world.GrantedReservation)],
            byProperty.Items.Select(i => i.Key));

        var airbnb = await service.ListAsync(OrgWide, new MessageHubFilter(Channel: MessageHubChannel.Airbnb), 1, 20);
        Assert.Equal([Ota(_world.GrantedRequest)], airbnb.Items.Select(i => i.Key));

        var direct = await service.ListAsync(OrgWide, new MessageHubFilter(Channel: MessageHubChannel.Direct), 1, 20);
        Assert.All(direct.Items, i => Assert.Equal(MessageThreadSource.Direct, i.Source));
        Assert.Equal(2, direct.TotalCount);

        var byBooking = await service.ListAsync(OrgWide, new MessageHubFilter(BookingId: _world.GrantedChannelBooking.Id), 1, 20);
        Assert.Equal([Ota(_world.GrantedReservation)], byBooking.Items.Select(i => i.Key));

        var unread = await service.ListAsync(OrgWide, new MessageHubFilter(Unread: true), 1, 20);
        Assert.All(unread.Items, i => Assert.True(i.UnreadCount > 0));
        Assert.DoesNotContain(unread.Items, i => i.Key == Ota(_world.GrantedReservation));
        var read = await service.ListAsync(OrgWide, new MessageHubFilter(Unread: false), 1, 20);
        Assert.Equal([MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id), Ota(_world.GrantedReservation)], read.Items.Select(i => i.Key));

        var requests = await service.ListAsync(OrgWide, new MessageHubFilter(Kind: MessageHubThreadKind.Request), 1, 20);
        Assert.Equal([Ota(_world.Unassigned), Ota(_world.GrantedRequest)], requests.Items.Select(i => i.Key));
        var reservations = await service.ListAsync(OrgWide, new MessageHubFilter(Kind: MessageHubThreadKind.Reservation), 1, 20);
        Assert.Equal(4, reservations.TotalCount);
    }

    [Fact]
    public async Task List_Query_MatchesTheNameOfTheGuest_TheCodeOfTheBooking_AndTheNameOfTheProperty_AnyCase()
    {
        var service = Service();

        var byGuest = await service.ListAsync(OrgWide, new MessageHubFilter(Query: "mArTa"), 1, 20);
        Assert.Equal([Ota(_world.GrantedRequest)], byGuest.Items.Select(i => i.Key));

        var byBookingGuest = await service.ListAsync(OrgWide, new MessageHubFilter(Query: "laura conti"), 1, 20);
        Assert.Equal([MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id)], byBookingGuest.Items.Select(i => i.Key));

        var code = _world.GrantedChannelBooking.BookingCode;
        var byCode = await service.ListAsync(OrgWide, new MessageHubFilter(Query: BookingCodes.Format(code).ToLowerInvariant()), 1, 20);
        Assert.Contains(byCode.Items, i => i.Key == Ota(_world.GrantedReservation));

        var byProperty = await service.ListAsync(OrgWide, new MessageHubFilter(Query: "casa bianca"), 1, 20);
        Assert.Equal(
            [Ota(_world.HiddenReservation), MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id)],
            byProperty.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task List_Query_DoesNotSearchTheWordsOfTheMessages()
    {
        // The text of a message is encrypted at rest: a word that is only in a message finds nothing.
        var byWord = await Service().ListAsync(OrgWide, new MessageHubFilter(Query: "parking"), 1, 20);

        Assert.Empty(byWord.Items);
    }

    [Fact]
    public async Task List_Rows_CarryTheShapeOfTheHub()
    {
        var page = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        var request = Assert.Single(page.Items, i => i.Key == Ota(_world.GrantedRequest));
        Assert.Equal(MessageThreadSource.Ota, request.Source);
        Assert.Equal(MessageHubChannel.Airbnb, request.Channel);
        Assert.Equal(MessageHubThreadKind.Request, request.Kind);
        Assert.Equal(MessageHubThreadState.Open, request.State);
        Assert.Equal(_world.Scope.Granted.Id, request.PropertyId);
        Assert.Equal("Trullo", request.PropertyName);
        Assert.False(request.Unassigned);
        Assert.Null(request.BookingId);
        Assert.Null(request.BookingCode);
        Assert.Equal("Marta R. (TEST)", request.GuestName);
        Assert.Null(request.CheckInDate);
        Assert.Equal(MessageOrigin.Guest, request.LastMessageOrigin);
        Assert.Equal("Is the house free in August? (TEST)", request.Preview);
        Assert.Equal(1, request.UnreadCount);
        Assert.True(request.NeedsReply);

        var reservation = Assert.Single(page.Items, i => i.Key == Ota(_world.GrantedReservation));
        Assert.Equal(MessageHubChannel.Booking, reservation.Channel);
        Assert.Equal(MessageHubThreadKind.Reservation, reservation.Kind);
        Assert.Equal(_world.GrantedChannelBooking.Id, reservation.BookingId);
        Assert.Equal(BookingCodes.Format(_world.GrantedChannelBooking.BookingCode), reservation.BookingCode);
        Assert.Equal("Jonas B. (TEST)", reservation.GuestName);
        Assert.Equal(_world.GrantedChannelBooking.CheckInDate, reservation.CheckInDate);
        Assert.Equal(_world.GrantedChannelBooking.CheckOutDate, reservation.CheckOutDate);
        Assert.Equal(MessageOrigin.Assistant, reservation.LastMessageOrigin);
        Assert.Equal("Parking is free (TEST)", reservation.Preview);
        Assert.Equal(0, reservation.UnreadCount);

        var direct = Assert.Single(page.Items, i => i.Key == MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id));
        Assert.Equal(MessageThreadSource.Direct, direct.Source);
        Assert.Equal(MessageHubChannel.Direct, direct.Channel);
        Assert.Equal(MessageHubThreadKind.Reservation, direct.Kind);
        Assert.Equal("Laura Conti", direct.GuestName);
        Assert.Equal(_world.GrantedDirectBooking.Id, direct.BookingId);
        Assert.Equal(MessageOrigin.Guest, direct.LastMessageOrigin);
        Assert.Equal("Direct question (TEST)", direct.Preview);
        Assert.Equal(1, direct.UnreadCount);

        var answered = Assert.Single(page.Items, i => i.Key == MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id));
        Assert.Equal(MessageOrigin.Person, answered.LastMessageOrigin);
        Assert.Equal("Direct answer (TEST)", answered.Preview);
        Assert.Equal(0, answered.UnreadCount);
    }

    [Fact]
    public async Task List_AMessageThatIsOnlyAFile_HasNoPreview()
    {
        var onlyFile = await _db.OtaMessages.FirstAsync(m => m.Id == _world.GrantedAttachment.Id);
        onlyFile.SentAt = MessageHubTestKit.Ago(10);
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedReservation.Id);
        thread.LastMessageAt = onlyFile.SentAt;
        thread.LastMessageDirection = OtaMessageDirection.Inbound;
        await _db.SaveChangesAsync();

        var page = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        var row = Assert.Single(page.Items, i => i.Key == Ota(_world.GrantedReservation));
        Assert.Null(row.Preview);
        Assert.Equal(MessageOrigin.Guest, row.LastMessageOrigin);
    }

    [Fact]
    public async Task List_AnUnassignedConversation_IsListedToAnOrgWideScope_ShownUnderNoProperty()
    {
        var page = await Service().ListAsync(OrgWide, new MessageHubFilter(), 1, 20);

        var row = Assert.Single(page.Items, i => i.Key == Ota(_world.Unassigned));
        Assert.True(row.Unassigned);
        Assert.Null(row.PropertyId);
        Assert.Null(row.PropertyName);
    }

    [Fact]
    public async Task List_OfTheAccountInNoTeam_ReachesTheHousesItCreated_AndNothingWithoutAProperty()
    {
        // The account in no org team created the hidden house only (the old rule): it sees that house and no unassigned conversation.
        var page = await Service().ListAsync(_world.Scope.OwnedByOwner, new MessageHubFilter(), 1, 20);

        Assert.DoesNotContain(page.Items, i => i.Unassigned);
        Assert.All(page.Items.Where(i => i.Source == MessageThreadSource.Ota), i => Assert.NotNull(i.PropertyId));
    }

    // ---- The thread detail ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetThread_OfAChannel_CarriesTheMessagesOldestFirst_TheBookingCard_TheCustomerLink_AndTheActions()
    {
        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: true);

        Assert.Equal(MessageThreadSource.Ota, view.Source);
        Assert.Equal(MessageHubChannel.Booking, view.Channel);
        Assert.Equal(MessageHubThreadKind.Reservation, view.Kind);
        Assert.Equal("Trullo", view.PropertyName);
        Assert.False(view.Unassigned);
        Assert.Equal(
            ["What time is check-in? (TEST)", string.Empty, "From 3 pm (TEST)", "Parking is free (TEST)"],
            view.Messages.Select(m => m.Text));
        Assert.Equal(view.Messages.Select(m => m.SentAt).Order(), view.Messages.Select(m => m.SentAt));
        Assert.Equal(
            [MessageOrigin.Guest, MessageOrigin.Guest, MessageOrigin.Person, MessageOrigin.Assistant],
            view.Messages.Select(m => m.Origin));
        Assert.Equal("parking", view.Messages[^1].RuleCode);
        Assert.Null(view.Messages[0].RuleCode);
        Assert.Equal([null, null, OtaMessageDeliveryState.Sent, OtaMessageDeliveryState.Sent], view.Messages.Select(m => m.DeliveryState));
        Assert.Equal("image/jpeg", view.Messages[1].AttachmentContentType);
        Assert.Null(view.Messages[0].AttachmentContentType);
        Assert.False(view.HasEarlierMessages);

        // The booking and the customer: ADR-032-R8, AC-NF61-3, AC-NF61-4, AC-NF64-1, AC-NF64-3.
        Assert.NotNull(view.Booking);
        Assert.Equal(_world.GrantedChannelBooking.Id, view.Booking.BookingId);
        Assert.Equal(BookingCodes.Format(_world.GrantedChannelBooking.BookingCode), view.Booking.BookingCode);
        Assert.Equal("Jonas Berg", view.Booking.GuestName);
        Assert.Equal(3, view.Booking.Guests);
        Assert.Equal(2, view.Booking.Adults);
        Assert.Equal(1, view.Booking.Children);
        Assert.Equal(BookingStatus.Confirmed, view.Booking.Status);
        Assert.Equal(BookingSource.Airbnb, view.Booking.Source);
        Assert.Equal("EUR", view.Booking.Currency);
        Assert.Equal(_world.GrantedChannelBooking.GuestId, view.CustomerGuestId);

        // The four figures the channel gave (null stays null) and the total.
        Assert.Equal(new MessageBookingAmounts(350m, 100m, 24.50m, null, null), view.Booking.Amounts);

        // The actions of the channel: it can answer, with contact details, nothing else is confirmed.
        Assert.Equal(new MessageThreadActions(true, true, false, false, null), view.Actions);
    }

    [Fact]
    public async Task GetThread_AConversationWithNoBooking_IsARequest_WithNoBookingAndNoCustomer()
    {
        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedRequest), includeAmounts: true);

        Assert.Equal(MessageHubThreadKind.Request, view.Kind);
        Assert.Null(view.Booking);
        Assert.Null(view.CustomerGuestId);
        Assert.Equal("Marta R. (TEST)", view.GuestName);
        Assert.True(view.NeedsReply);
        Assert.Equal(1, view.UnreadCount);

        // An Airbnb request: it can be answered, but no link, address or number may be written before the booking (OCT-11).
        Assert.Equal(new MessageThreadActions(true, false, false, false, null), view.Actions);
    }

    [Fact]
    public async Task GetThread_WithoutPaymentRead_CarriesNoMoney()
    {
        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: false);

        Assert.NotNull(view.Booking);
        Assert.Null(view.Booking.Amounts);
        Assert.Equal("Jonas Berg", view.Booking.GuestName);

        // On the wire the card has no amounts at all, not even the names of the figures.
        var json = JsonSerializer.Serialize(MessageThreadDto.From(view), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("booking").TryGetProperty("amounts", out _));
        Assert.DoesNotContain("commission", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetThread_OnTheWire_TheAmountsOfABookingOfAChannelOmitTheFiguresThatAreNotAvailable()
    {
        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: true);

        var json = JsonSerializer.Serialize(MessageThreadDto.From(view), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var amounts = document.RootElement.GetProperty("booking").GetProperty("amounts");
        Assert.Equal(350m, amounts.GetProperty("total").GetDecimal());
        Assert.Equal(100m, amounts.GetProperty("paid").GetDecimal());
        Assert.Equal(24.50m, amounts.GetProperty("commission").GetDecimal());
        Assert.False(amounts.TryGetProperty("net", out _));
        Assert.False(amounts.TryGetProperty("balance", out _));
        Assert.Equal("EUR", document.RootElement.GetProperty("booking").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task GetThread_AnUnassignedConversation_CanBeReadByAnOrgWideScope_ButNotAnswered()
    {
        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.Unassigned), includeAmounts: true);

        Assert.True(view.Unassigned);
        Assert.Null(view.PropertyId);
        Assert.Null(view.PropertyName);
        Assert.Equal(["Unassigned question (TEST)"], view.Messages.Select(m => m.Text));
        Assert.Equal(MessageThreadActions.Blocked("mapping_not_confirmed"), view.Actions);
        Assert.False(view.Actions.CanReply);
    }

    [Fact]
    public async Task GetThread_APropertyInAnUndecidedRowOfTheMapping_IsReadOnly()
    {
        var mapping = new Mock<IChannelMappingService>();
        mapping.Setup(m => m.IsWriteBlockedAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var view = await Service(channelMapping: mapping.Object).GetThreadAsync(OrgWide, Ota(_world.GrantedRequest), includeAmounts: true);

        Assert.Equal(MessageThreadActions.Blocked(MessageThreadActions.MappingNotConfirmedReason), view.Actions);
        Assert.Single(view.Messages);
    }

    [Fact]
    public async Task GetThread_ABookingOfAnotherPropertyThanTheThreads_IsNotShown()
    {
        // A stray link between a conversation and a stay at another house must not show that stay under this house.
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedReservation.Id);
        thread.BookingId = _world.HiddenDirectBooking.Id;
        await _db.SaveChangesAsync();

        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: true);

        Assert.Null(view.Booking);
        Assert.Null(view.CustomerGuestId);
    }

    [Fact]
    public async Task GetThread_AnonymizedConversation_SaysSo()
    {
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedRequest.Id);
        thread.AnonymizedAt = MessageHubTestKit.Ago(5);
        await _db.SaveChangesAsync();

        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedRequest), includeAmounts: true);

        Assert.True(view.Anonymized);
    }

    [Fact]
    public async Task GetThread_AConversationAnonymizedByTheRealCode_ShowsNoWords_NoName_NoPreview_AndNoFile()
    {
        // Anonymized by the code that erases a guest (the one the retention and the sync of the channel manager call too), not by hand.
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedReservation.Id);
        var messages = await _db.OtaMessages.Where(m => m.ThreadId == thread.Id).ToListAsync();
        GuestDataEraser.AnonymizeOtaThread(thread, messages, MessageHubTestKit.Now.UtcDateTime);
        await _db.SaveChangesAsync();
        var fetcher = new Mock<IOtaAttachmentFetcher>();
        var service = Service(attachments: fetcher.Object);

        var page = await service.ListAsync(OrgWide, new MessageHubFilter(), 1, 20);
        var view = await service.GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: true);
        var error = await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentAsync(OrgWide, _world.GrantedAttachment.Id));

        var row = Assert.Single(page.Items, i => i.Key == Ota(_world.GrantedReservation));
        Assert.Null(row.Preview);
        Assert.Equal(GuestDataEraser.AnonymizedName, row.GuestName);
        Assert.True(view.Anonymized);
        Assert.Equal(GuestDataEraser.AnonymizedName, view.GuestName);
        Assert.NotEmpty(view.Messages);
        Assert.All(view.Messages, m =>
        {
            Assert.Equal(string.Empty, m.Text);
            Assert.Equal(GuestDataEraser.AnonymizedName, m.SenderName);
            Assert.Null(m.AttachmentContentType);
        });
        Assert.Equal("attachment_unavailable", error.Code);
        fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetThread_ALongConversation_CarriesTheLatestMessagesAndSaysThereAreEarlierOnes()
    {
        for (var i = 0; i < GuestMessageRules.TranscriptMaxMessages; i++)
        {
            _db.OtaMessages.Add(new OtaMessage
            {
                OrgId = _world.GrantedRequest.OrgId,
                ThreadId = _world.GrantedRequest.Id,
                OctorateMessageId = $"LONG-{i}",
                Direction = OtaMessageDirection.Outbound,
                Text = $"Message {i}",
                SentAt = MessageHubTestKit.Ago(399).AddSeconds(i),
                Origin = MessageOrigin.Person,
                DeliveryState = OtaMessageDeliveryState.Sent,
                CreatedAt = MessageHubTestKit.Ago(399),
            });
        }

        await _db.SaveChangesAsync();

        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedRequest), includeAmounts: true);

        Assert.Equal(GuestMessageRules.TranscriptMaxMessages, view.Messages.Count);
        Assert.True(view.HasEarlierMessages);
        Assert.Equal($"Message {GuestMessageRules.TranscriptMaxMessages - 1}", view.Messages[^1].Text);
        Assert.DoesNotContain(view.Messages, m => m.Text.StartsWith("Is the house free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetThread_AThreadOfAnotherOrg_OrOneThatDoesNotExist_IsNotFound()
    {
        var service = Service();

        var other = await Assert.ThrowsAsync<NotFoundException>(() => service.GetThreadAsync(OrgWide, Ota(_world.OtherOrg), true));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.GetThreadAsync(OrgWide, MessageThreadKey.ForOta(Guid.NewGuid()), true));
        var directMissing = await Assert.ThrowsAsync<NotFoundException>(() => service.GetThreadAsync(OrgWide, MessageThreadKey.ForDirect(Guid.NewGuid()), true));

        Assert.All(new[] { other, missing, directMissing }, e => Assert.Equal("thread_not_found", e.Code));
    }

    [Fact]
    public async Task GetThread_Direct_ReadsTheThreadThroughTheExistingService_AndTheCardOfItsBooking()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        var sentAt = MessageHubTestKit.Ago(150);
        guestMessages.Setup(g => g.GetForHostAsync(_world.GrantedDirectBooking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostThreadView(
                GuestThreadState.Open,
                sentAt.AddDays(30),
                1,
                true,
                [
                    new GuestThreadMessage(Guid.NewGuid(), GuestMessageSender.Guest, "Direct question (TEST)", sentAt),
                    new GuestThreadMessage(Guid.NewGuid(), GuestMessageSender.Host, "Direct answer (TEST)", sentAt.AddMinutes(5)),
                ]));

        var view = await Service(guestMessages.Object).GetThreadAsync(OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), includeAmounts: true);

        Assert.Equal(MessageThreadSource.Direct, view.Source);
        Assert.Equal(MessageHubChannel.Direct, view.Channel);
        Assert.Equal(MessageHubThreadKind.Reservation, view.Kind);
        Assert.Equal(MessageHubThreadState.Open, view.State);
        Assert.Equal("Trullo", view.PropertyName);
        Assert.Equal("Laura Conti", view.GuestName);
        Assert.Equal(1, view.UnreadCount);
        Assert.True(view.NeedsReply);
        Assert.Equal(
            [(OtaMessageDirection.Inbound, MessageOrigin.Guest), (OtaMessageDirection.Outbound, MessageOrigin.Person)],
            view.Messages.Select(m => (m.Direction, m.Origin)));
        Assert.All(view.Messages, m =>
        {
            Assert.Null(m.RuleCode);
            Assert.Null(m.DeliveryState);
            Assert.Null(m.AttachmentContentType);
        });
        Assert.Equal(_world.GrantedDirectBooking.GuestId, view.CustomerGuestId);
        Assert.Equal(new MessageThreadActions(true, true, false, false, null), view.Actions);

        // A direct booking: the total and what came in, from its payments.
        Assert.Equal(new MessageBookingAmounts(300m, 120m, null, null, null), view.Booking!.Amounts);
        Assert.Equal(BookingSource.Direct, view.Booking.Source);
    }

    [Fact]
    public async Task GetThread_ADirectBookingCasaZenSentToOctorate_KeepsItsOwnMoney_TheTotalAndWhatCameInFromItsPayments()
    {
        // E08: the push of a booking of CasaZen saves the id of its reservation on the booking (OctorateReservationId), and the row of the mirror.
        // The id does not make its money the channel manager's: the card must still show what the payments hold, not the four figures of a channel.
        var booking = await _db.Bookings.FirstAsync(b => b.Id == _world.GrantedDirectBooking.Id);
        booking.OctorateReservationId = "RES-PUSHED-1";
        _db.OctorateBookingSyncs.Add(new OctorateBookingSync
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            PropertyId = booking.PropertyId,
            State = OctorateSyncState.Synced,
        });
        await _db.SaveChangesAsync();
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.GetForHostAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostThreadView(GuestThreadState.Open, null, 1, true, []));

        var view = await Service(guestMessages.Object).GetThreadAsync(OrgWide, MessageThreadKey.ForDirect(booking.Id), includeAmounts: true);

        Assert.Equal(new MessageBookingAmounts(300m, 120m, null, null, null), view.Booking!.Amounts);
    }

    [Fact]
    public async Task GetThread_AReservationOfTheBookingEngineOfTheAgency_HasTheMoneyOfTheChannelManager()
    {
        // A reservation of the booking engine of the agency that the import made counts as a direct booking, has a reservation on Octorate and no row
        // of the mirror (CasaZen never pushed it): its money is Octorate's, as stored, and the payments of CasaZen are not read.
        var booking = await _db.Bookings.FirstAsync(b => b.Id == _world.GrantedDirectBooking.Id);
        booking.OctorateReservationId = "RES-ENGINE-1";
        booking.OtaPaidAmount = 80m;
        var thread = await _db.OtaMessageThreads.FirstAsync(t => t.Id == _world.GrantedReservation.Id);
        thread.BookingId = booking.Id;
        await _db.SaveChangesAsync();

        var view = await Service().GetThreadAsync(OrgWide, Ota(_world.GrantedReservation), includeAmounts: true);

        Assert.Equal(new MessageBookingAmounts(300m, 80m, null, null, null), view.Booking!.Amounts);
    }

    [Fact]
    public async Task GetThread_Direct_AClosedThread_CannotBeAnswered()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.GetForHostAsync(_world.GrantedDirectBooking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostThreadView(GuestThreadState.Closed, null, 0, false, []));

        var view = await Service(guestMessages.Object).GetThreadAsync(OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), true);

        Assert.Equal(MessageHubThreadState.Closed, view.State);
        Assert.Equal(MessageThreadActions.Blocked(MessageThreadActions.ThreadClosedReason), view.Actions);
    }

    [Fact]
    public async Task GetThread_Direct_ABookingWithNoThread_IsNotFound()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.GetForHostAsync(_world.GrantedChannelBooking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostThreadView(GuestThreadState.Unavailable, null, 0, false, []));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Service(guestMessages.Object).GetThreadAsync(OrgWide, MessageThreadKey.ForDirect(_world.GrantedChannelBooking.Id), true));
    }

    // ---- Read marks and badges ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MarkRead_OfAChannel_SetsReadAtOnTheInboundMessages_AndZeroesTheCounter()
    {
        var result = await Service().MarkReadAsync(OrgWide, Ota(_world.HiddenReservation), "auth0|titolare");

        Assert.Equal(new MessageHubReadResult(Ota(_world.HiddenReservation), 0), result);
        var messages = await _db.OtaMessages.AsNoTracking().Where(m => m.ThreadId == _world.HiddenReservation.Id).ToListAsync();
        Assert.All(messages, m => Assert.NotNull(m.ReadAt));
        Assert.Equal(0, (await _db.OtaMessageThreads.AsNoTracking().FirstAsync(t => t.Id == _world.HiddenReservation.Id)).UnreadCount);

        // The other conversations are untouched.
        Assert.Equal(1, (await _db.OtaMessageThreads.AsNoTracking().FirstAsync(t => t.Id == _world.GrantedRequest.Id)).UnreadCount);
    }

    [Fact]
    public async Task MarkRead_OfAThreadOfAnotherOrg_IsNotFound_AndChangesNothing()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service().MarkReadAsync(OrgWide, Ota(_world.OtherOrg), "auth0|titolare"));

        Assert.Equal(1, (await _db.OtaMessageThreads.AsNoTracking().FirstAsync(t => t.Id == _world.OtherOrg.Id)).UnreadCount);
    }

    [Fact]
    public async Task MarkRead_Direct_GoesThroughTheExistingService_OnlyForAThreadWithinReach()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.MarkReadAsync(_world.GrantedDirectBooking.Id, "auth0|titolare", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostThreadView(GuestThreadState.Open, null, 0, false, []));

        var result = await Service(guestMessages.Object).MarkReadAsync(OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), "auth0|titolare");
        Assert.Equal(0, result.UnreadCount);

        // A collaborator who was not given that house never gets as far as the existing service.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Service(guestMessages.Object).MarkReadAsync(_world.Scope.Restricted, MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id), "auth0|collab"));
        guestMessages.Verify(g => g.MarkReadAsync(_world.HiddenDirectBooking.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListUnread_GivesTheCountersOfBothSources_AndTheOtaPartOnlyWhenAsked()
    {
        var guestMessages = new Mock<IGuestMessageService>();
        guestMessages.Setup(g => g.ListUnreadForHostAsync(OrgWide, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HostUnreadThread(_world.GrantedDirectBooking.Id, 2)]);
        var service = Service(guestMessages.Object);

        var directOnly = await service.ListUnreadAsync(OrgWide, includeOta: false);
        var both = await service.ListUnreadAsync(OrgWide, includeOta: true);

        Assert.Equal([new MessageHubUnreadThread(MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), _world.GrantedDirectBooking.Id, 2)], directOnly);
        Assert.Equal(directOnly, both.Take(1));
        Assert.Equal(
            [Ota(_world.Unassigned), Ota(_world.HiddenReservation), Ota(_world.GrantedRequest)],
            both.Skip(1).Select(t => t.Key));
        Assert.All(both, t => Assert.True(t.UnreadCount > 0));
        Assert.DoesNotContain(both, t => t.Key == Ota(_world.OtherOrg));
        Assert.DoesNotContain(both, t => t.Key == Ota(_world.GrantedReservation));
    }
}
