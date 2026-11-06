using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.DTOs.Messages;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US02/T01: the write side of the hub, on the real service over a database. The proofs the brief asks for (a send with one key reaches Octorate
/// once; an outcome nobody knows is never sent again by the call; the address and the number of a guest are in no answer, no log and no row) and the rules
/// that cost data or trust if they are wrong: a conversation the caller does not reach is a 404 and nothing leaves, a listing nobody decided is never written
/// to, the first contact of Airbnb is kept inside Airbnb.
/// </summary>
public class MessageReplyProofTests : IAsyncLifetime
{
    private const string Key = "key-1234abcd";
    private const string UserId = "auth0|anna";

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static MessageThreadKey Ota(OtaMessageThread thread) => MessageThreadKey.ForOta(thread.Id);

    private Task<MessageReplyResult> ReplyAsync(
        MessageReplyRig rig,
        OtaMessageThread thread,
        string text = "Benvenuti, vi aspettiamo",
        string key = Key,
        HostScope? scope = null) =>
        rig.Service.ReplyAsync(scope ?? _world.Scope.OrgWide, Ota(thread), text, key, UserId, MessageOrigin.Person, null);

    private Task<int> RowsWithKeyAsync(string key = Key) =>
        _db.OtaMessages.AsNoTracking().CountAsync(m => m.IdempotencyKey == key);

    // ---- The proofs of the brief --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (OCT-10, AC-NF63-6). Two sends with the same <c>Idempotency-Key</c> reach Octorate once: the second answers the first message,
    /// <c>Replayed</c>, with the state it reached, and writes nothing. A guest never gets the same words twice because a person clicked twice.
    /// </summary>
    [Fact]
    public async Task OtaReply_SameIdempotencyKey_SendsOnce_Test()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts("90001");

        var first = await ReplyAsync(rig, _world.GrantedReservation);
        var second = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Message!.Id, second.Message!.Id);
        Assert.Equal(OtaMessageDeliveryState.Sent, first.State);
        Assert.Equal(OtaMessageDeliveryState.Sent, second.State);
        Assert.Equal("Benvenuti, vi aspettiamo", second.Message.Text);
        rig.Client.Verify(
            c => c.SendChatMessageAsync("ACC-1", _world.GrantedReservation.OctorateThreadId, "Benvenuti, vi aspettiamo", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(1, await RowsWithKeyAsync());

        // The same key with other words is still the first message: nothing else leaves.
        var other = await ReplyAsync(rig, _world.GrantedReservation, text: "Altre parole");
        Assert.True(other.Replayed);
        Assert.Equal("Benvenuti, vi aspettiamo", other.Message!.Text);
        rig.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-10, OCT-13). A send whose outcome nobody knows (no answer, a 5xx, an answer that cannot be read) is stored as
    /// <c>ManualVerify</c> and answers without an error; the call never sends it again, and neither does a second call with the same key: only the copy of the
    /// conversation, or a person on the channel, settles it.
    /// </summary>
    [Fact]
    public async Task OtaReply_AmbiguousTimeout_NeverSentAgain_Test()
    {
        var rig = new MessageReplyRig(_db);
        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(null));

        var first = await ReplyAsync(rig, _world.GrantedReservation);
        var second = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.Equal(OtaMessageDeliveryState.ManualVerify, first.State);
        Assert.Null(first.ErrorCode);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, second.State);
        rig.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var row = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == Key);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, row.DeliveryState);
        Assert.Null(row.OctorateMessageId);

        // The conversation does not count as answered: nothing is known to have arrived.
        var thread = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == _world.GrantedReservation.Id);
        Assert.Equal(OtaMessageDirection.Outbound, thread.LastMessageDirection);
        Assert.Equal(_world.GrantedReservation.LastMessageDirection, thread.LastMessageDirection);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (S7, D24, NF-63). The e-mail address and the phone number the channel manager gives for ONE booking are in no answer of the
    /// API, no log line, no row of the database and no line of the activity log: they reach the queue of the e-mails and nothing else. The read is recorded as
    /// <c>ContactInfoRead</c> with the booking, the person and the purpose; the options of the channel carry flags, never the values.
    /// </summary>
    [Fact]
    public async Task ContactInfo_NeverReturnedOrLogged_Test()
    {
        var rig = new MessageReplyRig(_db);
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Mei", "Tanaka", country: "JP");
        var booking = await MessageReplyScenario.AddBookingAsync(_db, _world, _world.Scope.Granted.Id, guest, BookingSource.Agoda, "RES-AGODA-1");
        rig.Targets.Setup(t => t.AccommodationOfAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync("ACC-1");
        rig.Client
            .Setup(c => c.GetReservationAsync("ACC-1", "RES-AGODA-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = "RES-AGODA-1", Refer = "REF-77", GuestEmailAvailable = true, GuestPhoneAvailable = true });
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync("ACC-1", "REF-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(MessageReplyScenario.GuestEmail, MessageReplyScenario.GuestPhone));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // The options say that an address and a number exist, and not what they are.
        var options = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, booking.Id);
        var optionsJson = JsonSerializer.Serialize(MessageContactOptionsDto.From(options), web);
        Assert.Equal(MessageContactChannel.Email, options.Channel);
        Assert.True(options.EmailAvailable);
        Assert.True(options.PhoneAvailable);

        var sent = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno, vi aspettiamo il 20 ottobre", Key, UserId);
        var sentJson = JsonSerializer.Serialize(MessageReplyDto.From(sent), web);

        Assert.Equal(MessageContactChannel.Email, sent.Channel);
        Assert.Equal(OtaMessageDeliveryState.Sent, sent.State);
        var mail = Assert.Single(rig.Emails.Sent);
        Assert.Equal(MessageReplyScenario.GuestEmail, mail.To);
        Assert.Equal(EmailTemplatesNames.HostGuestMessage, mail.Template);
        Assert.Contains("Buongiorno, vi aspettiamo il 20 ottobre", mail.Content.HtmlBody, StringComparison.Ordinal);

        // Everything a client, a log or a table could hold: not a trace of the address or the number.
        var everything = new[]
        {
            optionsJson,
            sentJson,
            rig.Log.AllOutput,
            mail.Content.HtmlBody,
            mail.Content.Subject,
            mail.Content.ReplyTo ?? string.Empty,
            JsonSerializer.Serialize(await _db.OrgActivityEntries.AsNoTracking().ToListAsync(), web),
            JsonSerializer.Serialize(await _db.OtaMessages.AsNoTracking().Select(m => new { m.Id, m.SenderName, m.OctorateMessageId, m.IdempotencyKey }).ToListAsync(), web),
        };
        foreach (var text in everything)
        {
            Assert.DoesNotContain("tanaka", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-mail", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1234 5678", text, StringComparison.Ordinal);
        }

        // The address is the destination of the e-mail and nothing else; the text of the host is not in the log.
        Assert.DoesNotContain("Buongiorno", rig.Log.AllOutput, StringComparison.Ordinal);

        // One line of the activity log: the booking, the person, the purpose.
        var line = await _db.OrgActivityEntries.AsNoTracking().SingleAsync();
        Assert.Equal(OrgActivityType.ContactInfoRead, line.Type);
        Assert.Equal(OrgActivitySubjectType.Booking, line.SubjectType);
        Assert.Equal(booking.Id.ToString(), line.SubjectId);
        Assert.Equal(UserId, line.ActorUserId);
        Assert.Equal(_world.Scope.OrgId, line.OrgId);
        Assert.Equal("{\"purpose\":\"message_to_guest\"}", line.DetailsJson);
    }

    // ---- The conversation must be one the caller reaches, and one that may be written to ------------------------------------------------

    /// <summary>
    /// PROOF TEST: permission rule. A collaborator limited to some houses writes to the conversations of the houses it was given and to no other: the
    /// conversation of another house, the one with no house, the one of another org and the direct one of a stay it does not reach are all a 404, and nothing
    /// leaves (not to Octorate, not to the direct service).
    /// </summary>
    [Fact]
    public async Task OtaReply_Collaborator_ReachesOnlyTheConversationsOfItsHouses_Test()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts();
        var collaborator = _world.Scope.Restricted;

        foreach (var thread in new[] { _world.HiddenReservation, _world.Unassigned, _world.OtherOrg })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => ReplyAsync(rig, thread, scope: collaborator));
            Assert.Equal("thread_not_found", error.Code);
        }

        var hiddenDirect = await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.ReplyAsync(
            collaborator, MessageThreadKey.ForDirect(_world.HiddenDirectBooking.Id), "Ciao", Key, UserId, MessageOrigin.Person, null));
        Assert.Equal("thread_not_found", hiddenDirect.Code);

        rig.NothingWasSent();
        rig.Direct.Verify(
            g => g.ReplyAsHostAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, await RowsWithKeyAsync());

        // The conversation of a house it was given is open to it.
        var granted = await ReplyAsync(rig, _world.GrantedReservation, scope: collaborator);
        Assert.Equal(OtaMessageDeliveryState.Sent, granted.State);
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-04). A conversation with no property, or whose property sits in a row of the mapping nobody decided, is not written to:
    /// 409 <c>mapping_not_confirmed</c>, nothing is sent and no row is written. Even whoever reaches every property is refused for the unassigned one.
    /// </summary>
    [Fact]
    public async Task OtaReply_UnmappedOrUndecidedListing_IsRefused_AndNothingIsSent_Test()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts();

        var unassigned = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(rig, _world.Unassigned));
        Assert.Equal("mapping_not_confirmed", unassigned.Code);

        rig.Mapping.Setup(m => m.IsWriteBlockedAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var undecided = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(rig, _world.GrantedReservation));
        Assert.Equal("mapping_not_confirmed", undecided.Code);

        rig.NothingWasSent();
        Assert.Equal(0, await RowsWithKeyAsync());
    }

    /// <summary>
    /// The connector is the switch of the whole module: with it off nothing leaves and no row is written, however the thread was copied before.
    /// </summary>
    [Fact]
    public async Task OtaReply_ConnectorOff_SendsNothing()
    {
        var rig = new MessageReplyRig(_db);
        rig.Flags.Set(FeatureFlags.OctorateConnector, false);
        rig.ChannelManagerAccepts();

        var error = await Assert.ThrowsAsync<OctorateAuthException>(() => ReplyAsync(rig, _world.GrantedReservation));

        Assert.Equal(OctorateAuthException.NotConnectedCode, error.Code);
        rig.NothingWasSent();
        Assert.Equal(0, await RowsWithKeyAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-11). On an Airbnb conversation that is still a request, a text with a link, an e-mail address or a phone number is refused
    /// with 422 <c>channel_rule_violation</c> and nothing is stored or sent; a plain text goes; and the same words are fine on a conversation about a booking.
    /// </summary>
    [Theory]
    [InlineData("Scrivimi a anna@example.com")]
    [InlineData("Chiamami al +39 333 1234567")]
    [InlineData("Guarda https://example.com/casa")]
    [InlineData("Trovi tutto su www.casa-bella.it")]
    public async Task OtaReply_AirbnbRequest_RefusesContactDetails_Test(string text)
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(rig, _world.GrantedRequest, text));

        Assert.Equal("channel_rule_violation", error.Code);
        rig.NothingWasSent();
        Assert.Equal(0, await RowsWithKeyAsync());

        var plain = await ReplyAsync(rig, _world.GrantedRequest, "Sì, la casa è libera ad agosto");
        Assert.Equal(OtaMessageDeliveryState.Sent, plain.State);

        // A conversation about a booking (Booking.com here) takes the same words.
        var booking = await ReplyAsync(rig, _world.GrantedReservation, text, key: "key-other-12");
        Assert.Equal(OtaMessageDeliveryState.Sent, booking.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OtaReply_AnEmptyText_Is422_AndNothingIsWritten(string? text)
    {
        var rig = new MessageReplyRig(_db);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            rig.Service.ReplyAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation), text, Key, UserId, MessageOrigin.Person, null));

        Assert.Equal("message_text_invalid", error.Code);
        rig.NothingWasSent();
        Assert.Equal(0, await RowsWithKeyAsync());
    }

    [Fact]
    public async Task OtaReply_ATextOfMoreThan2000Characters_Is422()
    {
        var rig = new MessageReplyRig(_db);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(rig, _world.GrantedReservation, new string('a', GuestMessageRules.TextMaxLength + 1)));

        Assert.Equal("message_text_invalid", error.Code);
        rig.NothingWasSent();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in it 12345")]
    public async Task OtaReply_ABadKey_Is422_AndNothingIsWritten(string? key)
    {
        var rig = new MessageReplyRig(_db);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            rig.Service.ReplyAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation), "Ciao", key, UserId, MessageOrigin.Person, null));

        Assert.Equal("idempotency_key_invalid", error.Code);
        rig.NothingWasSent();
    }

    // ---- What a send does to the conversation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OtaReply_AMessageThatWent_MarksTheConversationAnsweredAndItsGuestMessagesRead()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts("90002", OtaMessageDeliveryState.WaitingAuthorization);

        var result = await ReplyAsync(rig, _world.GrantedRequest, "Sì, la casa è libera ad agosto");

        Assert.Equal(OtaMessageDeliveryState.WaitingAuthorization, result.State);
        var thread = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == _world.GrantedRequest.Id);
        Assert.Equal(0, thread.UnreadCount);
        Assert.Equal(OtaMessageDirection.Outbound, thread.LastMessageDirection);
        Assert.All(
            await _db.OtaMessages.AsNoTracking().Where(m => m.ThreadId == thread.Id && m.Direction == OtaMessageDirection.Inbound).ToListAsync(),
            m => Assert.NotNull(m.ReadAt));

        var row = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == Key);
        Assert.Equal("90002", row.OctorateMessageId);
        Assert.Equal(OtaMessageDirection.Outbound, row.Direction);
        Assert.Equal(MessageOrigin.Person, row.Origin);
        Assert.Equal(UserId, row.SentByUserId);
        Assert.Equal(_world.Scope.OrgId, row.OrgId);
    }

    [Fact]
    public async Task OtaReply_TheAssistant_LeavesItsRuleOnTheMessage()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts();

        var result = await rig.Service.ReplyAsync(
            _world.Scope.OrgWide, Ota(_world.GrantedReservation), "Il parcheggio è gratuito", Key, null, MessageOrigin.Assistant, "parking");

        Assert.Equal(MessageOrigin.Assistant, result.Message!.Origin);
        Assert.Equal("parking", result.Message.RuleCode);
        var row = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == Key);
        Assert.Equal("parking", row.RuleCode);
        Assert.Null(row.SentByUserId);
    }

    [Fact]
    public async Task OtaReply_TheGuestIsNeverTheAuthor_AndARuleCodeBelongsToTheAssistant()
    {
        var rig = new MessageReplyRig(_db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Service.ReplyAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation), "Ciao", Key, UserId, MessageOrigin.Guest, null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Service.ReplyAsync(_world.Scope.OrgWide, Ota(_world.GrantedReservation), "Ciao", Key, UserId, MessageOrigin.Person, "parking"));
        rig.NothingWasSent();
    }

    /// <summary>A refusal that has a type (a refused token, a chat module not in the license) is the state Error with its code, stored, and replayed with the key.</summary>
    [Fact]
    public async Task OtaReply_ATypedRefusal_IsTheStateError_WithItsCode()
    {
        var rig = new MessageReplyRig(_db);
        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ChatNotLicensed(403));

        var first = await ReplyAsync(rig, _world.GrantedReservation);
        var second = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.Equal(OtaMessageDeliveryState.Error, first.State);
        Assert.Equal("chat_not_licensed", first.ErrorCode);
        Assert.Equal(OtaMessageDeliveryState.Error, second.State);
        Assert.True(second.Replayed);
        rig.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // A message that did not go does not answer the guest.
        var thread = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == _world.GrantedReservation.Id);
        Assert.Equal(_world.GrantedReservation.LastMessageDirection, thread.LastMessageDirection);
    }

    /// <summary>
    /// A 429 (or the empty bucket of the org) means nothing happened: the row goes away, the error reaches the caller, and the same key can be sent when the
    /// limit is over.
    /// </summary>
    [Fact]
    public async Task OtaReply_RateLimited_LeavesNoRow_AndTheSameKeyCanBeSentAgain()
    {
        var rig = new MessageReplyRig(_db);
        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));

        await Assert.ThrowsAsync<OctorateRateLimitException>(() => ReplyAsync(rig, _world.GrantedReservation));
        Assert.Equal(0, await RowsWithKeyAsync());

        rig.ChannelManagerAccepts("90003");
        var again = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.False(again.Replayed);
        Assert.Equal(OtaMessageDeliveryState.Sent, again.State);
        Assert.Equal(1, await RowsWithKeyAsync());
    }

    /// <summary>The caller left while the message was on its way: just as unknown as a timeout. The row says so and the cancellation goes on.</summary>
    [Fact]
    public async Task OtaReply_TheCallerLeaves_TheMessageIsLeftToVerify()
    {
        var rig = new MessageReplyRig(_db);
        using var cancellation = new CancellationTokenSource();
        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ReplyAsync(
            _world.Scope.OrgWide, Ota(_world.GrantedReservation), "Ciao", Key, UserId, MessageOrigin.Person, null, cancellation.Token));

        var row = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == Key);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, row.DeliveryState);
    }

    /// <summary>
    /// If the copy of the conversation read the thread between the call and the save of the outcome, it stored the message once already, as a message of the
    /// host side: that is the same message, and the one that knows who wrote it (the key, the person) is the one that stays.
    /// </summary>
    [Fact]
    public async Task OtaReply_TheCopyAlreadyStoredTheMessage_LeavesOneRow()
    {
        var rig = new MessageReplyRig(_db);
        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                _db.OtaMessages.Add(new OtaMessage
                {
                    OrgId = _world.Scope.OrgId,
                    ThreadId = _world.GrantedReservation.Id,
                    OctorateMessageId = "90004",
                    Direction = OtaMessageDirection.Outbound,
                    Text = "Benvenuti, vi aspettiamo",
                    SentAt = MessageHubTestKit.Now.UtcDateTime,
                    DeliveryState = OtaMessageDeliveryState.Sent,
                    Origin = MessageOrigin.Person,
                    CreatedAt = MessageHubTestKit.Now.UtcDateTime,
                });
                _db.SaveChanges();
                return Task.FromResult(new OctorateChatSendResult("90004", OtaMessageDeliveryState.Sent));
            });

        var result = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.Equal(OtaMessageDeliveryState.Sent, result.State);
        var rows = await _db.OtaMessages.AsNoTracking().Where(m => m.OctorateMessageId == "90004").ToListAsync();
        var kept = Assert.Single(rows);
        Assert.Equal(Key, kept.IdempotencyKey);
    }

    /// <summary>
    /// A row written and never sent (the process stopped between the two) is found by a second send with the same key after the window: it may or may not
    /// have left, so it is turned into a message to verify, never shown as sent, and nothing is sent by this call.
    /// </summary>
    [Fact]
    public async Task OtaReply_ARowThatWasWrittenAndNeverSent_BecomesAMessageToVerify()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts();
        var now = MessageHubTestKit.Now.UtcDateTime;
        _db.OtaMessages.Add(new OtaMessage
        {
            OrgId = _world.Scope.OrgId,
            ThreadId = _world.GrantedReservation.Id,
            Direction = OtaMessageDirection.Outbound,
            Text = "Benvenuti, vi aspettiamo",
            SentAt = now.AddMinutes(-5),
            DeliveryState = OtaMessageDeliveryState.Created,
            IdempotencyKey = Key,
            Origin = MessageOrigin.Person,
            CreatedAt = now.AddMinutes(-5),
        });
        await _db.SaveChangesAsync();

        var result = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.True(result.Replayed);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, result.State);
        rig.NothingWasSent();
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, (await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == Key)).DeliveryState);
    }

    /// <summary>A row written a moment ago, with the call to Octorate still running, is not touched by a second send: it is only replayed as it is.</summary>
    [Fact]
    public async Task OtaReply_ARowThatIsStillBeingSent_IsReplayedAsItIs()
    {
        var rig = new MessageReplyRig(_db);
        var now = MessageHubTestKit.Now.UtcDateTime;
        _db.OtaMessages.Add(new OtaMessage
        {
            OrgId = _world.Scope.OrgId,
            ThreadId = _world.GrantedReservation.Id,
            Direction = OtaMessageDirection.Outbound,
            Text = "Benvenuti, vi aspettiamo",
            SentAt = now.AddSeconds(-5),
            DeliveryState = OtaMessageDeliveryState.Created,
            IdempotencyKey = Key,
            Origin = MessageOrigin.Person,
            CreatedAt = now.AddSeconds(-5),
        });
        await _db.SaveChangesAsync();

        var result = await ReplyAsync(rig, _world.GrantedReservation);

        Assert.True(result.Replayed);
        Assert.Equal(OtaMessageDeliveryState.Created, result.State);
        rig.NothingWasSent();
    }

    // ---- A conversation with the guest of a direct booking ---------------------------------------------------------------------------------

    /// <summary>A reply to a direct conversation goes through the existing service, unchanged, and writes nothing in the conversations of the channels.</summary>
    [Fact]
    public async Task DirectReply_GoesThroughTheDirectService_Unchanged()
    {
        var rig = new MessageReplyRig(_db);
        var written = new SentGuestMessage(
            new GuestThreadMessage(Guid.NewGuid(), GuestMessageSender.Host, "Benvenuti", MessageHubTestKit.Now.UtcDateTime), false, false);
        rig.Direct
            .Setup(g => g.ReplyAsHostAsync(_world.GrantedDirectBooking.Id, "Benvenuti", Key, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(written);

        var result = await rig.Service.ReplyAsync(
            _world.Scope.OrgWide, MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), "Benvenuti", Key, UserId, MessageOrigin.Person, null);

        Assert.Equal(MessageContactChannel.Direct, result.Channel);
        Assert.Equal(OtaMessageDeliveryState.Sent, result.State);
        Assert.False(result.Replayed);
        Assert.Equal(written.Message.Id, result.Message!.Id);
        Assert.Equal("Benvenuti", result.Message.Text);
        rig.Direct.VerifyAll();
        rig.NothingWasSent();
        Assert.Equal(0, await RowsWithKeyAsync());
    }

    // ---- Through which channel a guest is reached --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChooseChannel_ABookingOfAChannel_ChoosesItsConversation_UnlessItsListingIsUndecided()
    {
        var rig = new MessageReplyRig(_db);

        var chosen = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, _world.GrantedChannelBooking.Id);
        Assert.Equal(MessageContactChannel.Ota, chosen.Channel);
        Assert.Equal(Ota(_world.GrantedReservation), chosen.ThreadKey);
        Assert.Null(chosen.Reason);

        rig.Mapping.Setup(m => m.IsWriteBlockedAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var blocked = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, _world.GrantedChannelBooking.Id);
        Assert.Equal(MessageContactChannel.None, blocked.Channel);
        Assert.Null(blocked.ThreadKey);
        Assert.Equal("mapping_not_confirmed", blocked.Reason);

        rig.Mapping.Setup(m => m.IsWriteBlockedAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        rig.Flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, _world.GrantedChannelBooking.Id);
        Assert.Equal(MessageContactChannel.None, off.Channel);
        Assert.Equal("octorate_not_connected", off.Reason);
    }

    [Fact]
    public async Task ChooseChannel_ADirectBooking_IsTheDirectConversationWhileItIsOpen()
    {
        var rig = new MessageReplyRig(_db);

        var open = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, _world.GrantedDirectBooking.Id);

        Assert.Equal(MessageContactChannel.Direct, open.Channel);
        Assert.Equal(MessageThreadKey.ForDirect(_world.GrantedDirectBooking.Id), open.ThreadKey);
        Assert.True(open.EmailAvailable);
        Assert.False(open.PhoneAvailable);

        // Past the seventh day after the check-out the conversation is closed, and so is the way to it.
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Carla", "Neri");
        var old = await MessageReplyScenario.AddBookingAsync(
            _db, _world, _world.Scope.Granted.Id, guest, BookingSource.Direct, null, checkOut: HostScopeScenario.Day(2026, 9, 1));
        var closed = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, old.Id);
        Assert.Equal(MessageContactChannel.None, closed.Channel);
        Assert.Equal("thread_closed", closed.Reason);
        rig.NothingWasSent();
    }

    [Fact]
    public async Task ChooseChannel_ABookingOfAChannelWithAChatAndNoConversation_AndAStayEnteredByHand_HaveNoWayToWrite()
    {
        var rig = new MessageReplyRig(_db);
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Carla", "Neri");
        var airbnb = await MessageReplyScenario.AddBookingAsync(_db, _world, _world.Scope.Granted.Id, guest, BookingSource.Airbnb, "RES-9");
        var manual = await MessageReplyScenario.AddBookingAsync(_db, _world, _world.Scope.Granted.Id, guest, BookingSource.Manual, null);

        var noThread = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, airbnb.Id);
        var entered = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, manual.Id);

        Assert.Equal((MessageContactChannel.None, "no_conversation"), (noThread.Channel, noThread.Reason));
        Assert.Equal((MessageContactChannel.None, "no_conversation"), (entered.Channel, entered.Reason));
        rig.Client.VerifyNoOtherCalls();
    }

    /// <summary>A collaborator limited to some houses asks about the guest of a stay it does not reach: a 404, and nothing is asked of the channel manager.</summary>
    [Fact]
    public async Task ChooseChannel_Collaborator_CannotAskAboutAStayOfAnotherHouse()
    {
        var rig = new MessageReplyRig(_db);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.ChooseChannelAsync(_world.Scope.Restricted, _world.HiddenDirectBooking.Id));
        Assert.Equal("booking_not_found", error.Code);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.Restricted, _world.HiddenDirectBooking.Id, "Ciao", Key, UserId));

        rig.Client.VerifyNoOtherCalls();
        Assert.Empty(rig.Emails.Sent);
    }

    // ---- The first message to a guest who has no conversation ---------------------------------------------------------------------------------

    private async Task<(MessageReplyRig Rig, Booking Booking)> EmailRigAsync(bool emailAvailable = true)
    {
        var rig = new MessageReplyRig(_db);
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Mei", "Tanaka", country: "JP");
        var booking = await MessageReplyScenario.AddBookingAsync(_db, _world, _world.Scope.Granted.Id, guest, BookingSource.Agoda, "RES-AGODA-1");
        rig.Targets.Setup(t => t.AccommodationOfAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync("ACC-1");
        rig.Client
            .Setup(c => c.GetReservationAsync("ACC-1", "RES-AGODA-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = "RES-AGODA-1", Refer = "REF-77", GuestEmailAvailable = emailAvailable, GuestPhoneAvailable = false });
        return (rig, booking);
    }

    /// <summary>
    /// PROOF TEST: permission and privacy rule (S7, D24, AC-NF63-4). Until CasaZen's static IP addresses are registered with Octorate the contact is refused:
    /// the answer is 409 <c>contact_info_unavailable</c>, nothing is queued, and the activity log has no line, because nothing was released.
    /// </summary>
    [Fact]
    public async Task StartConversation_EmailPath_ContactRefused_Is409_AndNothingIsQueued_Test()
    {
        var (rig, booking) = await EmailRigAsync();
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateSensitiveDataNotAllowedException(403));

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId));

        Assert.Equal("contact_info_unavailable", error.Code);
        Assert.Empty(rig.Emails.Sent);
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
    }

    /// <summary>The stay in the e-mail is the stay in Rome: a booking that starts at midnight in Rome is stored as the instant before it in UTC.</summary>
    [Fact]
    public async Task StartConversation_EmailPath_NamesTheStayWithItsDaysInRome()
    {
        var rig = new MessageReplyRig(_db);
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Mei", "Tanaka", country: "JP");
        var checkIn = new DateTime(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc);
        var booking = await MessageReplyScenario.AddBookingAsync(
            _db, _world, _world.Scope.Granted.Id, guest, BookingSource.Agoda, "RES-AGODA-2", checkOut: checkIn.AddDays(3), checkInAt: checkIn);
        rig.Targets.Setup(t => t.AccommodationOfAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync("ACC-1");
        rig.Client
            .Setup(c => c.GetReservationAsync("ACC-1", "RES-AGODA-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservation { Id = "RES-AGODA-2", Refer = "REF-78", GuestEmailAvailable = true });
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync("ACC-1", "REF-78", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(MessageReplyScenario.GuestEmail, null));

        await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId);

        var body = Assert.Single(rig.Emails.Sent).Content.HtmlBody;
        Assert.Contains("from 20 October 2026 to 23 October 2026", body, StringComparison.Ordinal);
        Assert.DoesNotContain("19 October", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartConversation_ABadTextOrKey_IsRefusedBeforeTheChannelManagerIsAsked()
    {
        var (rig, booking) = await EmailRigAsync();

        var text = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "  ", Key, UserId));
        var key = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Ciao", "x", UserId));

        Assert.Equal("message_text_invalid", text.Code);
        Assert.Equal("idempotency_key_invalid", key.Code);
        rig.Client.Verify(c => c.GetReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(rig.Emails.Sent);
    }

    [Fact]
    public async Task StartConversation_EmailPath_ANoticeWithoutUsableAddress_IsRecordedAsARead_ButNothingIsQueued()
    {
        var (rig, booking) = await EmailRigAsync();
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo("not an address", null));

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId));

        Assert.Equal("contact_info_unavailable", error.Code);
        Assert.Empty(rig.Emails.Sent);
        // The read happened: Octorate recorded it, and so did CasaZen.
        Assert.Equal(OrgActivityType.ContactInfoRead, Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync()).Type);
    }

    [Fact]
    public async Task StartConversation_EmailPath_SameKeyTwice_QueuesOneEmail_AndReadsTheContactOnce()
    {
        var (rig, booking) = await EmailRigAsync();
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync("ACC-1", "REF-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(MessageReplyScenario.GuestEmail, null));

        var first = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId);
        var second = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(OtaMessageDeliveryState.Sent, second.State);
        Assert.Single(rig.Emails.Sent);
        rig.Client.Verify(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StartConversation_EmailPath_AQueueThatCannotTakeTheEmail_IsTheStateError_AndTheKeyCanBeSentAgain()
    {
        var (rig, booking) = await EmailRigAsync();
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync("ACC-1", "REF-77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateGuestContactInfo(MessageReplyScenario.GuestEmail, null));
        rig.Emails.Accepts = false;

        var failed = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId);
        Assert.Equal(OtaMessageDeliveryState.Error, failed.State);
        Assert.Equal("email_not_queued", failed.ErrorCode);

        rig.Emails.Accepts = true;
        var again = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId);
        Assert.Equal(OtaMessageDeliveryState.Sent, again.State);
        Assert.False(again.Replayed);
    }

    [Fact]
    public async Task StartConversation_EmailPath_AnOutageOfTheChannelManager_IsNotRecordedAndNothingIsQueued()
    {
        var (rig, booking) = await EmailRigAsync();
        rig.Client
            .Setup(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));

        await Assert.ThrowsAsync<OctorateServerException>(() => rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId));

        Assert.Empty(rig.Emails.Sent);
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StartConversation_AGuestWithoutAnAddressOnTheChannelManager_HasNoWayToWrite()
    {
        var (rig, booking) = await EmailRigAsync(emailAvailable: false);

        var options = await rig.Service.ChooseChannelAsync(_world.Scope.OrgWide, booking.Id);
        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.OrgWide, booking.Id, "Buongiorno", Key, UserId));

        Assert.Equal((MessageContactChannel.None, "contact_info_unavailable", false), (options.Channel, options.Reason, options.EmailAvailable));
        Assert.Equal("contact_info_unavailable", error.Code);
        rig.Client.Verify(c => c.GetReservationContactInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(rig.Emails.Sent);
    }

    [Fact]
    public async Task StartConversation_OnABookingWithItsConversation_WritesInTheConversation()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts("90010");

        var result = await rig.Service.StartConversationAsync(_world.Scope.OrgWide, _world.GrantedChannelBooking.Id, "Buongiorno Jonas", Key, UserId);

        Assert.Equal(MessageContactChannel.Ota, result.Channel);
        Assert.Equal(Ota(_world.GrantedReservation), result.ThreadKey);
        Assert.Equal(OtaMessageDeliveryState.Sent, result.State);
        rig.Client.Verify(
            c => c.SendChatMessageAsync("ACC-1", _world.GrantedReservation.OctorateThreadId, "Buongiorno Jonas", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartConversation_WhenThereIsNoWayToWrite_IsA409_WithTheCodeOfTheReason()
    {
        var rig = new MessageReplyRig(_db);
        rig.Mapping.Setup(m => m.IsWriteBlockedAsync(_world.Scope.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var guest = await MessageReplyScenario.AddGuestAsync(_db, _world.Scope.OrgId, "Carla", "Neri");
        var manual = await MessageReplyScenario.AddBookingAsync(_db, _world, _world.Scope.Granted.Id, guest, BookingSource.Manual, null);

        var blocked = await Assert.ThrowsAsync<DomainConflictException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.OrgWide, _world.GrantedChannelBooking.Id, "Ciao", Key, UserId));
        var nothing = await Assert.ThrowsAsync<DomainConflictException>(() =>
            rig.Service.StartConversationAsync(_world.Scope.OrgWide, manual.Id, "Ciao", Key, UserId));

        Assert.Equal("mapping_not_confirmed", blocked.Code);
        Assert.Equal("no_contact_channel", nothing.Code);
        rig.NothingWasSent();
    }
}

/// <summary>The template names the reply tests look for, so a test reads the name and not a string of the production code.</summary>
internal static class EmailTemplatesNames
{
    public const string HostGuestMessage = Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.HostGuestMessage;
}
