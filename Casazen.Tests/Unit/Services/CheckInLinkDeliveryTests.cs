using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Messages;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E23/F01/US01/T01, T02 (NF-122, NF-63): the delivery of the check-in link of a stay that has no e-mail address, on the real service over a database. The proofs the briefs
/// ask for: the link goes in the chat once (one message for one key, whatever the number of attempts), a chat that is not licensed makes the link wait and the host hears of it
/// (a «To do», closed when the link is sent), the link reaches only a caller who can copy it and never across orgs, the contact of the guest is asked once and a refusal is not
/// asked again within the day; and the rules that cost trust if they are wrong: an automatic message is never a person's and answers nobody, an outcome nobody knows is never
/// sent again, and with both flags off nothing is added to what the host sees.
/// </summary>
public class CheckInLinkDeliveryTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(MessageHubTestKit.Now);
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ---- The proofs of the brief -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (OCT-10, AC-NF122-1). The link of a portal stay with no address goes in the reservation thread of the booking ONCE, as an automatic message with
    /// no user: a second delivery of the same link does nothing, and even a delivery whose record lost the outcome (the process stopped after the send) finds the message that
    /// exists and sends nothing again.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_SentOnceIdempotent_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.ChannelManagerAccepts("90001");

        var first = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        var second = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sent), (first.Channel, first.State));
        Assert.True(first.IsAutomatic);
        Assert.Equal(CheckInLinkDeliveryState.Sent, second.State);
        var message = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.ThreadId == stay.Thread!.Id && m.Origin == MessageOrigin.Automation);
        Assert.Null(message.SentByUserId);
        Assert.Null(message.RuleCode);
        Assert.Equal(CheckInLinkDeliveryRules.IdempotencyKey(stay.Booking.Id, stay.Token), message.IdempotencyKey);
        Assert.Contains($"https://public.test/checkin/{stay.Token}", message.Text, StringComparison.Ordinal);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(message.Id, row.OtaMessageId);
        Assert.NotNull(row.SentAt);
        Assert.Null(row.LinkToken);
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync("ACC-1", stay.Thread!.OctorateThreadId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The process stopped after the send and before the record was saved: the record says the link waits, the message is there. The next pass finds it.
        var lost = await _db.CheckInLinkDeliveries.SingleAsync();
        lost.State = CheckInLinkDeliveryState.WaitingThread;
        lost.SentAt = null;
        lost.OtaMessageId = null;
        lost.LinkToken = stay.Token;
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromHours(2));

        var tried = await rig.Service.RetryWaitingAsync(IssueNothing);

        Assert.Equal(1, tried);
        Assert.Equal(1, await _db.OtaMessages.CountAsync(m => m.ThreadId == stay.Thread!.Id && m.Origin == MessageOrigin.Automation));
        Assert.Equal(CheckInLinkDeliveryState.Sent, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).State);
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// PROOF TEST (E40/F01/US01/T03, NF-142): the chat message of an org whose verified domain serves its guest area, with «use my domain for links» on,
    /// carries the link on the agency's domain with the same token; without that the link is the CasaZen one (the test above).
    /// </summary>
    [Fact]
    public async Task LinkDelivery_OrgOnItsOwnDomain_TheChatCarriesTheAgencyLink_Test()
    {
        const string domain = "prenota.agenzia-chat.test";
        var org = await _db.Orgs.SingleAsync(o => o.Id == _world.Scope.OrgId);
        org.PlanTier = PlanTier.Pro;
        org.PublicHostMode = PublicHostMode.CustomDomain;
        org.CustomDomain = domain;
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        org.GuestAreaOnOwnDomain = true;
        org.UseOwnDomainForLinks = true;
        await _db.SaveChangesAsync();
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink, FeatureFlags.WhiteLabelHost);
        rig.Messages.ChannelManagerAccepts("90001");
        var platform = EmailTestHelpers.Links("https://public.test");
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns((OrgEntity o) => o.PlanTier);
        var siteUrls = new PublicOrgSiteUrls(platform, Microsoft.Extensions.Options.Options.Create(new PublicHostOptions()), entitlements.Object);
        var service = new CheckInLinkDeliveryService(
            _db,
            rig.Emails.Object,
            rig.Messages.Service,
            rig.Contacts.Object,
            rig.Messages.Targets.Object,
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock),
            rig.Messages.Flags,
            platform,
            NullLogger<CheckInLinkDeliveryService>.Instance,
            _clock,
            rig.Push.Object,
            new AgencyDomainLinks(_db, platform, siteUrls, rig.Messages.Flags, NullLogger<AgencyDomainLinks>.Instance));

        var view = await service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal(CheckInLinkDeliveryState.Sent, view.State);
        var message = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.ThreadId == stay.Thread!.Id && m.Origin == MessageOrigin.Automation);
        Assert.Contains($"https://{domain}/checkin/{stay.Token}", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://public.test/checkin/", message.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST (E50/F01/US03/T03, NF-152): the message of the check-in link in the chat of a portal also carries the stay link of the booking, made by the link service of the guest portal,
    /// only while <c>GuestPortal</c> is on; with it off the text is the one it has always been and no link is made; and the flag of the chat link still decides whether anything is sent: with
    /// it off no message goes and no stay link is made.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_TheChatCarriesTheStayLink_OnlyWhileThePortalIsOn_Test()
    {
        var withPortal = await AddStayAsync(reservation: "RES-PORTAL-ON");
        var withoutPortal = await AddStayAsync(reservation: "RES-PORTAL-OFF");
        var chatOff = await AddStayAsync(reservation: "RES-CHAT-OFF");
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink, FeatureFlags.GuestPortal);
        AcceptEveryMessage(rig.Messages);
        var platform = EmailTestHelpers.Links("https://public.test");
        var stayLinks = new GuestStayLinkProvider(
            rig.Messages.Flags,
            new GuestPortalLinkService(_db, platform, _clock, NullLogger<GuestPortalLinkService>.Instance),
            platform,
            NullLogger<GuestStayLinkProvider>.Instance);
        var service = new CheckInLinkDeliveryService(
            _db,
            rig.Emails.Object,
            rig.Messages.Service,
            rig.Contacts.Object,
            rig.Messages.Targets.Object,
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock),
            rig.Messages.Flags,
            platform,
            NullLogger<CheckInLinkDeliveryService>.Instance,
            _clock,
            rig.Push.Object,
            stayLinks: stayLinks);

        // The portal on: the check-in link and the stay link of the booking, in the one message.
        var on = await service.DeliverAsync(withPortal.Booking.Id, withPortal.Link);

        Assert.Equal(CheckInLinkDeliveryState.Sent, on.State);
        var link = await _db.GuestPortalLinks.AsNoTracking().SingleAsync(l => l.BookingId == withPortal.Booking.Id);
        var message = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.ThreadId == withPortal.Thread!.Id && m.Origin == MessageOrigin.Automation);
        Assert.Contains($"https://public.test/checkin/{withPortal.Token}", message.Text, StringComparison.Ordinal);
        Assert.EndsWith($"https://public.test/s/{link.Token}", message.Text, StringComparison.Ordinal);

        // The portal off: the text of today, and no link is made.
        rig.Messages.Flags.Set(FeatureFlags.GuestPortal, false);
        var off = await service.DeliverAsync(withoutPortal.Booking.Id, withoutPortal.Link);

        Assert.Equal(CheckInLinkDeliveryState.Sent, off.State);
        var plain = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.ThreadId == withoutPortal.Thread!.Id && m.Origin == MessageOrigin.Automation);
        Assert.Contains($"https://public.test/checkin/{withoutPortal.Token}", plain.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/s/", plain.Text, StringComparison.Ordinal);
        Assert.Empty(await _db.GuestPortalLinks.AsNoTracking().Where(l => l.BookingId == withoutPortal.Booking.Id).ToListAsync());

        // The chat flag of the link still decides: with it off, nothing is sent and no stay link is made, whatever the portal says.
        rig.Messages.Flags.Set(FeatureFlags.GuestPortal, true);
        rig.Messages.Flags.Set(FeatureFlags.PortalChatCheckInLink, false);
        var gated = await service.DeliverAsync(chatOff.Booking.Id, chatOff.Link);

        Assert.NotEqual(CheckInLinkDeliveryState.Sent, gated.State);
        Assert.Empty(await _db.OtaMessages.AsNoTracking().Where(m => m.ThreadId == chatOff.Thread!.Id && m.Origin == MessageOrigin.Automation).ToListAsync());
        Assert.Empty(await _db.GuestPortalLinks.AsNoTracking().Where(l => l.BookingId == chatOff.Booking.Id).ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF122-2). While the chat of the listing is not in its license (the copy of the conversations said so) nothing is sent and no call is made: the
    /// row says <c>WaitingChatPermission</c> with the code, the host has ONE «To do» («Copia link»), and when the listing reads its chat again the next pass sends the link, once,
    /// and closes the «To do».
    /// </summary>
    [Fact]
    public async Task LinkDelivery_ChatNotLicensed_Waits_Test()
    {
        var stay = await AddStayAsync();
        await SetSyncStateAsync(OtaMessageSyncErrorCodes.ChatNotLicensed, _clock.GetUtcNow().UtcDateTime);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.ChannelManagerAccepts("90001");

        var waiting = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.WaitingChatPermission), (waiting.Channel, waiting.State));
        Assert.Equal(OtaMessageSyncErrorCodes.ChatNotLicensed, waiting.LastErrorCode);
        rig.Messages.NothingWasSent();
        var todo = Assert.Single(await _db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(nameof(ActivityType.CheckInLinkToSend), todo.ActivityType);
        Assert.Equal(ActivityAction.CopyCheckInLink, todo.Action);
        Assert.Equal(stay.Booking.Id, todo.EntityId);
        Assert.Null(todo.CompletedAt);
        Assert.Equal(stay.Token, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).LinkToken);

        // Nothing changed: the listing still says the chat is not licensed, so no pass tries again.
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        rig.Messages.NothingWasSent();

        // The listing reads its chat again: the link goes, and the «To do» is closed by the system.
        await SetSyncStateAsync(null, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        var sent = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sent), (sent.Channel, sent.State));
        Assert.Null(sent.LinkToken);
        var closed = Assert.Single(await _db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.NotNull(closed.CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// PROOF TEST: permission and data rule. The link is returned only when the caller can already copy one, and only while the delivery waits or failed and the link can still be
    /// opened; it is in nothing else, and a booking of another org has no delivery to read.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_LinkOnlyForTheCallerWhoCanCopyIt_AndNeverAcrossOrgs_Test()
    {
        var stay = await AddStayAsync(withThread: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        var orgId = _world.Scope.OrgId;

        var hidden = await rig.Service.GetAsync(orgId, stay.Booking.Id, includeLink: false);
        var shown = await rig.Service.GetAsync(orgId, stay.Booking.Id, includeLink: true);
        var foreign = await rig.Service.GetAsync(_world.Scope.OtherOrgId, stay.Booking.Id, includeLink: true);

        Assert.Equal(CheckInLinkDeliveryState.WaitingThread, hidden!.State);
        Assert.Null(hidden.Link);
        Assert.Equal($"https://public.test/checkin/{stay.Token}", shown!.Link);
        Assert.Equal(stay.Link.ExpiresAt, shown.ExpiresAt);
        Assert.Null(foreign);

        // An expired link and an ended row give no link, whoever asks.
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.Null((await rig.Service.GetAsync(orgId, stay.Booking.Id, includeLink: true))!.Link);
        await rig.Service.EndAsync(orgId, stay.Booking.Id, CheckInLinkEndReasons.HostLink);
        var ended = await rig.Service.GetAsync(orgId, stay.Booking.Id, includeLink: true);
        Assert.Null(ended!.Link);
        Assert.NotNull(ended.EndedAt);
        Assert.Null((await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).LinkToken);
    }

    // ---- The rules that cost trust if they are wrong -----------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (decision O10). The message is an automatic one, never a person's: it answers nobody, so a question of the guest still waits in «Da rispondere», its
    /// messages stay unread, and the hub shows the message as <c>Automation</c>.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_AutomaticMessage_IsNeverAPersonsAndAnswersNobody_Test()
    {
        var stay = await AddStayAsync(unansweredQuestion: true);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.ChannelManagerAccepts("90001");
        var before = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == stay.Thread!.Id);
        Assert.Equal(OtaMessageDirection.Inbound, before.LastMessageDirection);
        Assert.Equal(1, before.UnreadCount);

        await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        var after = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == stay.Thread!.Id);
        Assert.Equal(OtaMessageDirection.Inbound, after.LastMessageDirection);
        Assert.Equal(1, after.UnreadCount);
        Assert.Null((await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.ThreadId == stay.Thread!.Id && m.Direction == OtaMessageDirection.Inbound)).ReadAt);
        var view = await MessageHubTestKit.Service(_db).GetThreadAsync(_world.Scope.OrgWide, MessageThreadKey.ForOta(stay.Thread!.Id), includeAmounts: false);
        Assert.True(view.NeedsReply);
        Assert.Equal(MessageOrigin.Automation, view.Messages[^1].Origin);
        Assert.Null(view.Messages[^1].RuleCode);
        Assert.Equal(OtaMessageDirection.Outbound, view.Messages[^1].Direction);
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-10, OCT-13). A send whose outcome nobody knows is never sent again: the row is <c>Failed</c> with <c>manual_verify</c>, the hosts are told once
    /// (the bell and the live update), and no pass sends the link a second time.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_UnknownOutcome_IsNeverSentAgain_AndTheHostIsTold_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(null));

        var failed = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        _clock.Advance(TimeSpan.FromDays(1));
        var tried = await rig.Service.RetryWaitingAsync(IssueNothing);

        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Failed), (failed.Channel, failed.State));
        Assert.Equal(CheckInLinkDeliveryRules.ManualVerifyCode, failed.LastErrorCode);
        Assert.Equal(0, tried);
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var push = Assert.Single(rig.Pushes);
        Assert.Equal(PushDeliveryKeys.CheckInLinkFailed(stay.Session.Id), push.Key);
        Assert.Equal(PushAudience.BookingHosts(stay.Booking.Id), push.Audience);
        Assert.Equal((PushTypes.CheckInLinkFailed, stay.Booking.Id, PushRoutes.Booking(stay.Booking.Id)), (push.Payload.Type, push.Payload.BookingId, push.Payload.Route));
        Assert.DoesNotContain(stay.Token, push.Payload.Body + push.Payload.Title, StringComparison.Ordinal);
        Assert.Single(await _db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend)).ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 2). A message the channel manager has only created, or holds for the authorization of the channel, is not delivered: the row is
    /// <c>Sending</c>, keeps its token and the «To do» it already has, and nothing is sent again; when the channel manager sends it, the next pass delivers the link (the token is cleared
    /// and the «To do» closed by the system).
    /// </summary>
    [Theory]
    [InlineData(OtaMessageDeliveryState.Created)]
    [InlineData(OtaMessageDeliveryState.WaitingAuthorization)]
    public async Task LinkDelivery_AMessageNotSentYet_IsNotDelivered_AndTheNextPassDeliversItWhenTheChannelSendsIt_Test(OtaMessageDeliveryState onItsWay)
    {
        var stay = await AddStayAsync(withThread: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatSendResult("90001", onItsWay));
        await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        Assert.Equal(CheckInLinkDeliveryState.WaitingThread, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).State);
        _db.OtaMessageThreads.Add(NewThread(stay.Booking, "T-ITS-WAY"));
        await _db.SaveChangesAsync();

        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        // Handed to the channel manager, not sent: the link is not delivered. The row is open with its token, the «To do» the host already had is still open, nobody is told.
        var sending = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sending), (sending.Channel, sending.State));
        Assert.Equal(stay.Token, sending.LinkToken);
        Assert.Null(sending.SentAt);
        Assert.Null(sending.EndedAt);
        Assert.NotNull(sending.OtaMessageId);
        Assert.Null(Assert.Single(await _db.InternalActivities.AsNoTracking().ToListAsync()).CompletedAt);
        Assert.Empty(rig.Pushes);

        // Still on its way an hour later: the message is read again, nothing changes and nothing is sent again.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        Assert.Equal(CheckInLinkDeliveryState.Sending, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).State);

        // The channel manager sends it (the copy of the conversation stores the state it reached): the link is delivered, the token is cleared, the «To do» is closed by the system.
        await SetMessageStateAsync(sending.OtaMessageId!.Value, OtaMessageDeliveryState.Sent);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        var sent = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sent), (sent.Channel, sent.State));
        Assert.Null(sent.LinkToken);
        Assert.NotNull(sent.SentAt);
        var closed = Assert.Single(await _db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.NotNull(closed.CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 2). A message the channel manager accepted and then reports as failed is a link that did not reach the guest: the row fails with the code
    /// of a message that ended in error, the host gets ONE «To do» and the notice, the link is still there to copy («Copia link»), and the message is never sent again.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_AMessageThatEndsInError_FailsTheRow_TheHostIsToldAndCanStillCopyTheLink_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatSendResult("90001", OtaMessageDeliveryState.Created));

        var sending = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        // On its way: nothing for the host to do yet.
        Assert.Equal(CheckInLinkDeliveryState.Sending, sending.State);
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
        var messageId = (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).OtaMessageId!.Value;

        await SetMessageStateAsync(messageId, OtaMessageDeliveryState.Error);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        var failed = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Failed, MessageReplyService.ChannelReportedErrorCode), (failed.Channel, failed.State, failed.LastErrorCode));
        Assert.Equal(stay.Token, failed.LinkToken);
        var todo = Assert.Single(await _db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend)).ToListAsync());
        Assert.Null(todo.CompletedAt);
        var push = Assert.Single(rig.Pushes);
        Assert.Equal(PushDeliveryKeys.CheckInLinkFailed(stay.Session.Id), push.Key);
        var view = await rig.Service.GetAsync(_world.Scope.OrgId, stay.Booking.Id, includeLink: true);
        Assert.Equal($"https://public.test/checkin/{stay.Token}", view!.Link);

        // Never sent again, however long it waits.
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        rig.Messages.Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A refusal of the channel manager that nothing was sent behind (the chat is not licensed, the send was refused) spends the key of that message and no more: the next attempt
    /// for the same link uses the next key and sends, once, when the listing reads its chat again.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_ARefusedSend_LetsTheNextAttemptUseTheNextKey_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        var calls = 0;
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CancellationToken>((_, _, _, _) =>
                ++calls == 1
                    ? throw OctorateAuthException.ChatNotLicensed(403)
                    : Task.FromResult(new OctorateChatSendResult("90002", OtaMessageDeliveryState.Sent)));

        var waiting = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal(CheckInLinkDeliveryState.WaitingChatPermission, waiting.State);
        Assert.Equal(OtaMessageSyncErrorCodes.ChatNotLicensed, waiting.LastErrorCode);
        var refused = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.Origin == MessageOrigin.Automation);
        Assert.Equal(OtaMessageDeliveryState.Error, refused.DeliveryState);

        // The listing reads fine and the copy moves its state at every pass, but the SEND was refused: that proves nothing, so the send is not tried again within the day (not one refused
        // message an hour in the thread of the guest).
        for (var hour = 1; hour <= 23; hour++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await SetSyncStateAsync(null, _clock.GetUtcNow().UtcDateTime);
            Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        }

        Assert.Equal(1, calls);

        // A day after the refusal it is tried again, and the second key is used.
        _clock.Advance(TimeSpan.FromHours(1));
        await SetSyncStateAsync(null, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        Assert.Equal(CheckInLinkDeliveryState.Sent, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).State);
        var keys = await _db.OtaMessages.AsNoTracking().Where(m => m.Origin == MessageOrigin.Automation).Select(m => m.IdempotencyKey).OrderBy(k => k).ToListAsync();
        var baseKey = CheckInLinkDeliveryRules.IdempotencyKey(stay.Booking.Id, stay.Token);
        Assert.Equal([baseKey, baseKey + "-1"], keys);
        Assert.Equal(2, calls);
    }

    /// <summary>A failure worth a retry (the calls of the org are used up) is tried again, an hour apart, up to the limit; only then are the hosts told, once.</summary>
    [Fact]
    public async Task LinkDelivery_ATransientFailure_IsTriedAgainUpToTheLimit_ThenTheHostIsTold_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));

        var first = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        Assert.Equal((CheckInLinkDeliveryState.Failed, "octorate_rate_limited", 1), (first.State, first.LastErrorCode, first.Attempts));
        Assert.Empty(rig.Pushes);

        // Within the hour: left alone. Then one attempt an hour.
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));
        Assert.Empty(rig.Pushes);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        var last = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(CheckInLinkDeliveryRules.MaxAttempts, last.Attempts);
        Assert.Single(rig.Pushes);

        // The attempts are used: nothing more is tried.
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        Assert.Single(rig.Pushes);
        Assert.Equal(CheckInLinkDeliveryRules.MaxAttempts, await _db.CheckInLinkDeliveries.AsNoTracking().Select(d => d.Attempts).SingleAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 4). Waiting for the conversation or for the permission of the chat is not an attempt that failed: a link that waited twice still gets its
    /// three attempts when the channel manager then fails for a reason worth a retry (a limit), and the host is told only after the third.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_WaitingIsNotAFailedAttempt_SoATransientFailureAfterTheWaitsStillGetsItsThreeAttempts_Test()
    {
        var stay = await AddStayAsync(withThread: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        rig.Messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));

        // It waits for the conversation, then for the permission of the chat: two waits, no failed attempt.
        var first = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        Assert.Equal((CheckInLinkDeliveryState.WaitingThread, 0), (first.State, first.Attempts));
        _db.OtaMessageThreads.Add(NewThread(stay.Booking, "T-WAITS"));
        await _db.SaveChangesAsync();
        await SetSyncStateAsync(OtaMessageSyncErrorCodes.ChatNotLicensed, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));
        var waiting = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkDeliveryState.WaitingChatPermission, 0), (waiting.State, waiting.Attempts));

        // The chat comes back and the channel manager answers with a limit: three attempts, an hour apart, and only after the third the host is told.
        _clock.Advance(TimeSpan.FromHours(1));
        await SetSyncStateAsync(null, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));
        var failed = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((CheckInLinkDeliveryState.Failed, "octorate_rate_limited", 1), (failed.State, failed.LastErrorCode, failed.Attempts));
        Assert.Empty(rig.Pushes);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));
        Assert.Equal(2, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).Attempts);
        Assert.Empty(rig.Pushes);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));
        Assert.Equal(CheckInLinkDeliveryRules.MaxAttempts, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).Attempts);
        Assert.Single(rig.Pushes);
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 6). A pass takes a bounded number of rows, and the rows that cannot change never change their place: with 201 rows that wait for a
    /// conversation that is not there, older than the one row whose conversation has arrived, the pass still takes the one that is due (the query chooses the due rows, it does not take the
    /// oldest of all of them).
    /// </summary>
    [Fact]
    public async Task RetryPass_WithMoreOpenRowsThanOnePassTakes_TakesTheDueOnesFirst_Test()
    {
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        AcceptEveryMessage(rig.Messages);
        for (var i = 0; i <= CheckInLinkDeliveryService.RetryBatchSize; i++)
        {
            var older = await AddStayAsync(withThread: false, reservation: $"RES-WAIT-{i}");
            await rig.Service.DeliverAsync(older.Booking.Id, older.Link);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var due = await AddStayAsync(withThread: false, reservation: "RES-DUE");
        await rig.Service.DeliverAsync(due.Booking.Id, due.Link);
        _db.OtaMessageThreads.Add(NewThread(due.Booking, "T-DUE"));
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        var waiting = await _db.CheckInLinkDeliveries.CountAsync(d => d.State == CheckInLinkDeliveryState.WaitingThread);
        Assert.True(waiting > CheckInLinkDeliveryService.RetryBatchSize);

        var tried = await rig.Service.RetryWaitingAsync(IssueNothing);

        Assert.Equal(1, tried);
        Assert.Equal(CheckInLinkDeliveryState.Sent, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == due.Booking.Id)).State);
        Assert.Equal(waiting - 1, await _db.CheckInLinkDeliveries.CountAsync(d => d.State == CheckInLinkDeliveryState.WaitingThread));
    }

    /// <summary>
    /// The query that chooses the rows of the hourly pass (due or over, in the statement itself, with the reservation of the channel manager, the session, the sync state and the messages
    /// it looks at) is turned into SQL by the PostgreSQL provider: the in-memory provider accepts what Npgsql cannot translate (see <c>NpgsqlTranslationProbe</c>).
    /// </summary>
    [Fact]
    public async Task RetryPass_TheQueryThatChoosesTheRows_TranslatesToSqlOnPostgres_Test()
    {
        var statements = new List<string>();
        await using var db = Casazen.Tests.Unit.Infrastructure.NpgsqlTranslationProbe.NewContext(statements);
        var flags = new TestFeatureFlags(FeatureFlags.OctorateConnector, FeatureFlags.PortalChatCheckInLink, FeatureFlags.GuestContactInfo);
        var service = new CheckInLinkDeliveryService(
            db,
            Mock.Of<IGuestCheckInLinkEmailQueue>(),
            Mock.Of<IMessageReplyService>(),
            Mock.Of<IGuestContactInfoService>(),
            Mock.Of<IOctorateBookingTargets>(),
            Mock.Of<IInternalActivityService>(),
            flags,
            EmailTestHelpers.Links("https://public.test"),
            NullLogger<CheckInLinkDeliveryService>.Instance,
            _clock);

        await Casazen.Tests.Unit.Infrastructure.NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.RetryWaitingAsync(IssueNothing));

        var sql = Assert.Single(statements);
        Assert.Contains("\"CheckInLinkDeliveries\"", sql, StringComparison.Ordinal);
        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("\"OctorateReservationId\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
    }

    /// <summary>With both flags off the service writes the row and nothing else: no call, no «To do», no notice, no token, and the caller does what it always did.</summary>
    [Fact]
    public async Task LinkDelivery_BothFlagsOff_OnlyRecordsWhyTheLinkGoesNowhere_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock);

        var view = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal((CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact), (view.Channel, view.State));
        Assert.False(view.IsAutomatic);
        rig.Messages.NothingWasSent();
        rig.Contacts.Verify(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Empty(rig.Pushes);
        Assert.Null((await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).LinkToken);
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
    }

    /// <summary>
    /// The chat comes first and reads no personal data: when it delivers, the contact of the guest is never asked for. When the thread is not there yet, the link waits for it, and
    /// goes as soon as the thread arrives.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_TheChatComesFirst_AndAThreadThatArrivesLaterGetsTheLink_Test()
    {
        var withThread = await AddStayAsync();
        var withoutThread = await AddStayAsync(withThread: false, reservation: "RES-LATE");
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink, FeatureFlags.GuestContactInfo);
        AcceptEveryMessage(rig.Messages);
        rig.Contacts
            .Setup(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), withoutThread.Booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestContactInfoOutcome(GuestContactInfoStatus.NoEmailAtChannel));

        var delivered = await rig.Service.DeliverAsync(withThread.Booking.Id, withThread.Link);
        var waiting = await rig.Service.DeliverAsync(withoutThread.Booking.Id, withoutThread.Link);

        Assert.Equal(CheckInLinkDeliveryState.Sent, delivered.State);
        rig.Contacts.Verify(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), withThread.Booking.Id, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.WaitingThread), (waiting.Channel, waiting.State));
        Assert.Equal("no_conversation", waiting.LastErrorCode);

        // The conversation of the booking arrives with the next read of the threads.
        _db.OtaMessageThreads.Add(NewThread(withoutThread.Booking, "T-LATE"));
        await _db.SaveChangesAsync();
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        Assert.Equal(CheckInLinkDeliveryState.Sent, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == withoutThread.Booking.Id)).State);
        rig.Contacts.Verify(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), withoutThread.Booking.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- The contact of the guest (T02) -------------------------------------------------------------------------------------------------

    /// <summary>With the chat off and the static IP addresses registered, the e-mail read once for this link goes by e-mail: the row is <c>Sent</c> by <c>Email</c>.</summary>
    [Fact]
    public async Task LinkDelivery_ContactReadGivesTheAddress_TheLinkGoesByEmail_Test()
    {
        var stay = await AddStayAsync(withThread: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.GuestContactInfo);
        rig.Contacts
            .Setup(c => c.ReadForCheckInLinkAsync(_world.Scope.OrgId, stay.Booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestContactInfoOutcome(GuestContactInfoStatus.EmailFilled));

        var view = await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);

        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.Sent), (view.Channel, view.State));
        rig.Emails.Verify(e => e.QueueAsync(stay.Session.Id, stay.Token, It.IsAny<CancellationToken>()), Times.Once);
        rig.Messages.NothingWasSent();
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Null((await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync()).LinkToken);
    }

    /// <summary>
    /// PROOF TEST: data rule (T02). A refused read (the static IP addresses are not registered) is a state, not a «no contact»: the row waits in <c>WaitingStaticIps</c> and the read
    /// is repeated at most once a day (no storm against a channel manager that says no); a read that was answered, even with no address, is never repeated for the booking.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_ContactRefused_WaitsForTheStaticIps_AndIsAskedAgainOnlyAfterADay_Test()
    {
        var refused = await AddStayAsync(withThread: false);
        var answered = await AddStayAsync(withThread: false, reservation: "RES-NOMAIL");
        var rig = new Rig(_db, _world, _clock, FeatureFlags.GuestContactInfo);
        rig.Contacts
            .Setup(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), refused.Booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestContactInfoOutcome(GuestContactInfoStatus.StaticIpsRequired));
        rig.Contacts
            .Setup(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), answered.Booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestContactInfoOutcome(GuestContactInfoStatus.NoEmailAtChannel));

        var waiting = await rig.Service.DeliverAsync(refused.Booking.Id, refused.Link);
        var noAddress = await rig.Service.DeliverAsync(answered.Booking.Id, answered.Link);

        Assert.Equal((CheckInLinkChannel.Email, CheckInLinkDeliveryState.WaitingStaticIps), (waiting.Channel, waiting.State));
        Assert.Equal("octorate_sensitive_data_not_allowed", waiting.LastErrorCode);
        Assert.Equal((CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact), (noAddress.Channel, noAddress.State));
        Assert.Equal(2, (await _db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend)).ToListAsync()).Count);

        // Within the day: nothing asked again, for either. After the day: only the refused one.
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await rig.Service.RetryWaitingAsync(IssueNothing));

        rig.Contacts.Verify(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), refused.Booking.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
        rig.Contacts.Verify(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), answered.Booking.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A row made while the flags were off holds no token and nobody has its link: when a flag is turned on, the pass issues a new link for it and delivers that one.</summary>
    [Fact]
    public async Task LinkDelivery_ARowMadeWithTheFlagsOff_IsServedWithANewLinkWhenAFlagIsTurnedOn_Test()
    {
        var stay = await AddStayAsync();
        var rig = new Rig(_db, _world, _clock);
        await rig.Service.DeliverAsync(stay.Booking.Id, stay.Link);
        rig.Messages.Flags.Set(FeatureFlags.PortalChatCheckInLink, true);
        AcceptEveryMessage(rig.Messages);
        _clock.Advance(TimeSpan.FromHours(2));

        // What IGuestCheckInService.IssueLinkAsync does: the open link of the booking expires, a new session is made.
        var issued = new IssuedCheckInLink(Guid.NewGuid(), "new-" + Guid.NewGuid().ToString("N"), _clock.GetUtcNow().UtcDateTime.AddDays(7));
        var issuedFor = new List<Guid>();
        var tried = await rig.Service.RetryWaitingAsync(async (bookingId, orgId) =>
        {
            issuedFor.Add(bookingId);
            (await _db.GuestCheckInSessions.SingleAsync(s => s.Id == stay.Session.Id)).Status = GuestCheckInSessionStatus.Scaduto;
            _db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                Id = issued.SessionId,
                BookingId = bookingId,
                OrgId = orgId,
                TokenHash = GuestCheckInService.HashToken(issued.Token),
                ExpiresAt = issued.ExpiresAt,
                Status = GuestCheckInSessionStatus.Inviato,
            });
            await _db.SaveChangesAsync();
            return issued;
        });

        Assert.Equal(1, tried);
        Assert.Equal([stay.Booking.Id], issuedFor);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((issued.SessionId, CheckInLinkDeliveryState.Sent), (row.SessionId, row.State));
        var message = await _db.OtaMessages.AsNoTracking().SingleAsync(m => m.Origin == MessageOrigin.Automation);
        Assert.Contains(issued.Token, message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(stay.Token, message.Text, StringComparison.Ordinal);
    }

    // ---- The end of a row ----------------------------------------------------------------------------------------------------------------

    /// <summary>The guest completes the check-in, or the booking is cancelled: the row ends, its token is cleared and the «To do» is closed (here and by the hourly pass).</summary>
    [Fact]
    public async Task LinkDelivery_ACompletedCheckInOrACancelledBooking_EndsTheRowAndClosesTheTodo_Test()
    {
        var completed = await AddStayAsync(withThread: false);
        var cancelled = await AddStayAsync(withThread: false, reservation: "RES-CANC");
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        await rig.Service.DeliverAsync(completed.Booking.Id, completed.Link);
        await rig.Service.DeliverAsync(cancelled.Booking.Id, cancelled.Link);
        Assert.Equal(2, await _db.InternalActivities.CountAsync(a => a.CompletedAt == null));

        // The guest completes the form: the check-in service tells the delivery.
        await rig.Service.EndAsync(_world.Scope.OrgId, completed.Booking.Id, CheckInLinkEndReasons.CheckInCompleted);
        await rig.Service.EndAsync(_world.Scope.OrgId, completed.Booking.Id, CheckInLinkEndReasons.CheckInCompleted);

        // The booking is cancelled: the pass ends the row and closes the «To do» (the event handler closes it too).
        var booking = await _db.Bookings.SingleAsync(b => b.Id == cancelled.Booking.Id);
        booking.Status = BookingStatus.Cancelled;
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await rig.Service.RetryWaitingAsync(IssueNothing));

        var rows = await _db.CheckInLinkDeliveries.AsNoTracking().ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.EndedAt);
            Assert.Null(r.LinkToken);
        });
        Assert.Equal(CheckInLinkEndReasons.CheckInCompleted, rows.Single(r => r.BookingId == completed.Booking.Id).EndedReason);
        Assert.Equal(CheckInLinkEndReasons.BookingNotActive, rows.Single(r => r.BookingId == cancelled.Booking.Id).EndedReason);
        Assert.Equal(0, await _db.InternalActivities.CountAsync(a => a.CompletedAt == null));
        rig.Messages.NothingWasSent();
    }

    // ---- The send job and the reminder ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A link the delivery service took over needs no e-mail from the daily job (it would only record a failed e-mail), a stay with no way to the guest goes on as it always did,
    /// and the reminder leaves a stay whose link is in the chat alone: a new link would replace the one the guest already has.
    /// </summary>
    [Fact]
    public async Task SendJob_HandsAStayWithNoAddressToTheService_AndTheReminderLeavesItsLinkAlone_Test()
    {
        var chat = await AddStayAsync(daysUntilCheckIn: 0, withSession: false);
        var manual = await AddStayAsync(daysUntilCheckIn: 0, reservation: "RES-MANUAL", source: BookingSource.Manual, withThread: false, withSession: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.PortalChatCheckInLink);
        AcceptEveryMessage(rig.Messages);

        // What IGuestCheckInService.IssueLinkAsync does: a new session for the booking, with its token.
        var issued = new Dictionary<Guid, List<IssuedCheckInLink>>();
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn
            .Setup(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync((Guid bookingId, Guid orgId) =>
            {
                var token = "job-" + Guid.NewGuid().ToString("N");
                var session = new GuestCheckInSession
                {
                    BookingId = bookingId,
                    OrgId = orgId,
                    TokenHash = GuestCheckInService.HashToken(token),
                    ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(7),
                    Status = GuestCheckInSessionStatus.Inviato,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime,
                };
                _db.GuestCheckInSessions.Add(session);
                _db.SaveChanges();
                var link = new IssuedCheckInLink(session.Id, token, session.ExpiresAt);
                if (!issued.TryGetValue(bookingId, out var links))
                    issued[bookingId] = links = [];

                links.Add(link);
                return link;
            });
        var stayGuests = new Mock<IStayGuestService>();
        stayGuests
            .Setup(s => s.GetForBookingsAsync(It.IsAny<IReadOnlyCollection<Booking>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Booking> bookings, CancellationToken _) =>
                (IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>)bookings.ToDictionary(b => b.Id, _ => (IReadOnlyList<StayGuest>)[]));
        var emails = new Mock<IGuestCheckInLinkEmailQueue>();
        emails
            .Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.NoRecipient));
        var job = new GuestCheckInSendJob(
            _db,
            checkIn.Object,
            emails.Object,
            stayGuests.Object,
            EmailTestHelpers.Links("https://public.test"),
            Options.Create(new GuestCheckInOptions { SendWindowDays = 3, ReminderDaysBefore = 3 }),
            NullLogger<GuestCheckInSendJob>.Instance,
            _clock,
            rig.Service);

        await job.ExecuteAsync();

        // The portal stay: its link is in the chat and no e-mail was asked for; the manual one goes on as before (the failed e-mail is what the host sees).
        var chatLink = Assert.Single(issued[chat.Booking.Id]);
        var manualLink = Assert.Single(issued[manual.Booking.Id]);
        Assert.Equal(CheckInLinkDeliveryState.Sent, (await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == chat.Booking.Id)).State);
        emails.Verify(e => e.QueueAsync(chatLink.SessionId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        emails.Verify(e => e.QueueAsync(manualLink.SessionId, manualLink.Token, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal((CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact), await RowOfAsync(manual.Booking.Id));

        // The reminder: the link of the chat is not replaced; the other stay, which the service did not take over, is reminded as it always was.
        foreach (var session in await _db.GuestCheckInSessions.ToListAsync())
            session.CreatedAt = session.CreatedAt.AddDays(-2);
        await _db.SaveChangesAsync();
        await job.ExecuteAsync();

        Assert.Single(issued[chat.Booking.Id]);
        Assert.Equal(2, issued[manual.Booking.Id].Count);
    }

    /// <summary>
    /// PROOF TEST: data rule (the review of S010, fix 7). The reminder before arrival does not replace the link a row holds for the host: with only the contact of the guest on and no address
    /// at the channel, the row waits for the host (<c>Manual</c>, <c>WaitingContact</c>, its token and a «To do» «Copia link»), and the reminder, which would issue a new link and leave the
    /// host with nothing to copy, leaves the stay alone.
    /// </summary>
    [Fact]
    public async Task SendJob_TheReminderLeavesAlone_ARowThatHoldsTheLinkForTheHost_Test()
    {
        var waiting = await AddStayAsync(daysUntilCheckIn: 0, withThread: false, withSession: false);
        var rig = new Rig(_db, _world, _clock, FeatureFlags.GuestContactInfo);
        rig.Contacts
            .Setup(c => c.ReadForCheckInLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestContactInfoOutcome(GuestContactInfoStatus.NoEmailAtChannel));
        var issued = new Dictionary<Guid, List<IssuedCheckInLink>>();
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn
            .Setup(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync((Guid bookingId, Guid orgId) =>
            {
                var token = "job-" + Guid.NewGuid().ToString("N");
                var session = new GuestCheckInSession
                {
                    BookingId = bookingId,
                    OrgId = orgId,
                    TokenHash = GuestCheckInService.HashToken(token),
                    ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(7),
                    Status = GuestCheckInSessionStatus.Inviato,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime,
                };
                _db.GuestCheckInSessions.Add(session);
                _db.SaveChanges();
                var link = new IssuedCheckInLink(session.Id, token, session.ExpiresAt);
                if (!issued.TryGetValue(bookingId, out var links))
                    issued[bookingId] = links = [];

                links.Add(link);
                return link;
            });
        var stayGuests = new Mock<IStayGuestService>();
        stayGuests
            .Setup(s => s.GetForBookingsAsync(It.IsAny<IReadOnlyCollection<Booking>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Booking> bookings, CancellationToken _) =>
                (IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>)bookings.ToDictionary(b => b.Id, _ => (IReadOnlyList<StayGuest>)[]));
        var emails = new Mock<IGuestCheckInLinkEmailQueue>();
        emails
            .Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.NoRecipient));
        var job = new GuestCheckInSendJob(
            _db,
            checkIn.Object,
            emails.Object,
            stayGuests.Object,
            EmailTestHelpers.Links("https://public.test"),
            Options.Create(new GuestCheckInOptions { SendWindowDays = 3, ReminderDaysBefore = 3 }),
            NullLogger<GuestCheckInSendJob>.Instance,
            _clock,
            rig.Service);

        await job.ExecuteAsync();

        // The first stage issued the link; nobody can be reached, so the row holds it for the host, with the «To do».
        var first = Assert.Single(issued[waiting.Booking.Id]);
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == waiting.Booking.Id);
        Assert.Equal((CheckInLinkChannel.Manual, CheckInLinkDeliveryState.WaitingContact, first.Token), (row.Channel, row.State, row.LinkToken));
        Assert.Single(await _db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend) && a.EntityId == waiting.Booking.Id).ToListAsync());

        // The day after: the reminder leaves the link the host copies from its «To do» where it is.
        foreach (var session in await _db.GuestCheckInSessions.ToListAsync())
            session.CreatedAt = session.CreatedAt.AddDays(-2);
        await _db.SaveChangesAsync();
        await job.ExecuteAsync();

        Assert.Single(issued[waiting.Booking.Id]);
        var view = await rig.Service.GetAsync(_world.Scope.OrgId, waiting.Booking.Id, includeLink: true);
        Assert.Equal($"https://public.test/checkin/{first.Token}", view!.Link);
    }

    // ---- Small facts -------------------------------------------------------------------------------------------------------------------------

    /// <summary>The codes the hourly pass calls transient are the ones the exceptions carry, and the key of a message is a short string of letters, digits and a dash.</summary>
    [Fact]
    public void TheCodesAndTheKeys_AreTheOnesTheCodeUses()
    {
        Assert.Contains(new OctorateRateLimitException(429).Code, CheckInLinkDeliveryService.TransientCodes);
        Assert.Contains(new OctorateChatNotReadException(500).Code, CheckInLinkDeliveryService.TransientCodes);
        Assert.Equal(2, CheckInLinkDeliveryService.TransientCodes.Length);
        Assert.Equal(CheckInLinkDeliveryService.OctorateSensitiveDataCode, new OctorateSensitiveDataNotAllowedException(403).Code);

        var booking = Guid.NewGuid();
        var key = CheckInLinkDeliveryRules.IdempotencyKey(booking, "token-1");
        Assert.NotNull(GuestMessageRules.NormalizeIdempotencyKey(key));
        Assert.Equal(44, key.Length);
        Assert.DoesNotContain("token-1", key, StringComparison.Ordinal);
        Assert.Equal(key, CheckInLinkDeliveryRules.IdempotencyKey(booking, "token-1"));
        Assert.NotEqual(key, CheckInLinkDeliveryRules.IdempotencyKey(booking, "token-2"));
        Assert.NotEqual(key, CheckInLinkDeliveryRules.IdempotencyKey(Guid.NewGuid(), "token-1"));
        Assert.NotNull(GuestMessageRules.NormalizeIdempotencyKey(key + "-12"));
    }

    // ---- The data every test runs on ----------------------------------------------------------------------------------------------------

    private static readonly Func<Guid, Guid, Task<IssuedCheckInLink>> IssueNothing =
        (_, _) => throw new InvalidOperationException("No new link is expected here.");

    private async Task<(CheckInLinkChannel Channel, CheckInLinkDeliveryState State)> RowOfAsync(Guid bookingId)
    {
        var row = await _db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        return (row.Channel, row.State);
    }

    /// <summary>The copy of the conversation stores the state the channel manager reached for a message CasaZen wrote.</summary>
    private async Task SetMessageStateAsync(Guid messageId, OtaMessageDeliveryState state)
    {
        var message = await _db.OtaMessages.SingleAsync(m => m.Id == messageId);
        message.DeliveryState = state;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task SetSyncStateAsync(string? lastError, DateTime updatedAt)
    {
        var state = await _db.OtaMessageSyncStates.SingleOrDefaultAsync(s => s.OrgId == _world.Scope.OrgId && s.OctorateAccommodationId == "ACC-1");
        if (state is null)
        {
            // The copy of the conversations names the property of the listing in its state at every pass (the query of the hourly pass reads it).
            state = new OtaMessageSyncState
            {
                OrgId = _world.Scope.OrgId,
                OctorateAccommodationId = "ACC-1",
                ConnectionId = Guid.NewGuid(),
                PropertyId = _world.Scope.Granted.Id,
            };
            _db.OtaMessageSyncStates.Add(state);
        }

        state.LastError = lastError;
        state.UpdatedAt = updatedAt;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private OtaMessageThread NewThread(Booking booking, string octorateThreadId) => new()
    {
        OrgId = booking.OrgId,
        PropertyId = booking.PropertyId,
        BookingId = booking.Id,
        OctorateThreadId = octorateThreadId,
        OctorateAccommodationId = "ACC-1",
        ChannelSource = OtaChannel.Airbnb,
        Kind = OtaThreadKind.Reservation,
        GuestDisplayName = "Jonas B. (TEST)",
        LastMessageAt = _clock.GetUtcNow().UtcDateTime.AddHours(-1),
        CreatedAt = _clock.GetUtcNow().UtcDateTime.AddHours(-1),
        UpdatedAt = _clock.GetUtcNow().UtcDateTime.AddHours(-1),
    };

    /// <summary>The channel manager takes every message and gives each its own id (two messages with one id would be one message of the copy).</summary>
    private static void AcceptEveryMessage(MessageReplyRig messages)
    {
        var next = 90000;
        messages.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OctorateChatSendResult((++next).ToString(), OtaMessageDeliveryState.Sent));
    }

    private async Task<Stay> AddStayAsync(
        bool withThread = true,
        bool unansweredQuestion = false,
        string reservation = "RES-PORTAL",
        BookingSource source = BookingSource.Airbnb,
        int daysUntilCheckIn = 3,
        bool withSession = true)
    {
        var orgId = _world.Scope.OrgId;
        var now = _clock.GetUtcNow().UtcDateTime;
        var guest = new Guest { OrgId = orgId, FirstName = "Jonas", LastName = "Berg", Email = string.Empty };
        _db.Guests.Add(guest);
        var checkIn = HostScopeScenario.Day(2026, 10, 9).AddDays(daysUntilCheckIn);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = _world.Scope.Granted.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = source,
            OctorateReservationId = reservation,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        _db.Bookings.Add(booking);
        var token = "tok-" + Guid.NewGuid().ToString("N");
        var session = new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = orgId,
            TokenHash = GuestCheckInService.HashToken(token),
            ExpiresAt = now.AddDays(7),
            Status = GuestCheckInSessionStatus.Inviato,
            CreatedAt = now,
        };
        if (withSession)
            _db.GuestCheckInSessions.Add(session);

        OtaMessageThread? thread = null;
        if (withThread)
        {
            thread = NewThread(booking, $"T-{Guid.NewGuid():N}"[..20]);
            _db.OtaMessageThreads.Add(thread);
            if (unansweredQuestion)
            {
                _db.OtaMessages.Add(new OtaMessage
                {
                    OrgId = orgId,
                    ThreadId = thread.Id,
                    OctorateMessageId = $"M-{Guid.NewGuid():N}"[..20],
                    Direction = OtaMessageDirection.Inbound,
                    SenderName = "Jonas B. (TEST)",
                    Text = "What time is check-in? (TEST)",
                    SentAt = now.AddMinutes(-30),
                    DeliveryState = OtaMessageDeliveryState.Sent,
                    Origin = MessageOrigin.Guest,
                    CreatedAt = now.AddMinutes(-30),
                });
                thread.LastMessageAt = now.AddMinutes(-30);
                thread.LastMessageDirection = OtaMessageDirection.Inbound;
                thread.UnreadCount = 1;
            }
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new Stay(booking, session, token, thread);
    }

    private sealed record Stay(Booking Booking, GuestCheckInSession Session, string Token, OtaMessageThread? Thread)
    {
        public IssuedCheckInLink Link => new(Session.Id, Token, Session.ExpiresAt);
    }

    /// <summary>The service over the data of <see cref="MessageHubTestKit"/>, with the real reply service and «To do» list and every other neighbour a test can steer or watch.</summary>
    private sealed class Rig
    {
        public Rig(AppDbContext db, MessageHubWorld world, TimeProvider clock, params string[] flags)
        {
            Messages = new MessageReplyRig(db, clock);
            foreach (var flag in flags)
                Messages.Flags.Set(flag, true);

            Messages.Targets
                .Setup(t => t.AccommodationOfAsync(world.Scope.Granted.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync("ACC-1");
            Emails
                .Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Queued));
            Push
                .Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
                .Callback<string, PushAudience, PushNotificationPayload>((key, audience, payload) => Pushes.Add((key, audience, payload)))
                .Returns(true);
            Service = new CheckInLinkDeliveryService(
                db,
                Emails.Object,
                Messages.Service,
                Contacts.Object,
                Messages.Targets.Object,
                new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, clock),
                Messages.Flags,
                EmailTestHelpers.Links("https://public.test"),
                NullLogger<CheckInLinkDeliveryService>.Instance,
                clock,
                Push.Object);
        }

        public MessageReplyRig Messages { get; }

        public Mock<IGuestCheckInLinkEmailQueue> Emails { get; } = new();

        public Mock<IGuestContactInfoService> Contacts { get; } = new();

        public Mock<IPushNotificationService> Push { get; } = new();

        public List<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Pushes { get; } = [];

        public CheckInLinkDeliveryService Service { get; }
    }
}
