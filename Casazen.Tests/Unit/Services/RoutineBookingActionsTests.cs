using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class RoutineBookingActionsTests
{
    [Fact]
    public async Task TheGuestMessageUsesTheBookingConversationAndAutomationOrigin()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(RoutineRig.OrgA);
        await using var db = rig.NewDb();
        var template = new MessageTemplate { OrgId = RoutineRig.OrgA, Name = "Arrival", Body = "Hello", Language = "en", IsActive = true };
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();
        var thread = MessageThreadKey.ForDirect(booking.Id);
        var messages = new Mock<IMessageReplyService>(MockBehavior.Strict);
        var templates = new Mock<IMessageTemplateService>(MockBehavior.Strict);
        messages.Setup(service => service.ChooseChannelAsync(It.Is<HostScope>(scope => scope.OrgId == RoutineRig.OrgA), booking.Id, default))
            .ReturnsAsync(new MessageContactOptions(MessageContactChannel.Direct, thread, null, false, false));
        templates.Setup(service => service.RenderAsync(It.Is<HostScope>(scope => scope.OrgId == RoutineRig.OrgA), template.Id, thread, default))
            .ReturnsAsync("Hello");
        messages.Setup(service => service.ReplyAsync(It.Is<HostScope>(scope => scope.OrgId == RoutineRig.OrgA),
                thread, "Hello", It.IsAny<string>(), null, MessageOrigin.Automation, null, default))
            .ReturnsAsync(new MessageReplyResult(MessageContactChannel.Direct, thread, null, OtaMessageDeliveryState.Sent, false, null));
        var actions = new RoutineBookingActions(db, messages.Object, templates.Object, Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(), rig.Jobs.Object, TimeProvider.System);

        var result = await actions.ExecuteAsync(RoutineRig.OrgA, Guid.NewGuid(), 0,
            Action("MessageToGuest", $$"""{"templateId":"{{template.Id}}"}"""), RoutinePayload.Normalize(RoutineRig.PayloadOf(booking)), default);

        Assert.Equal(RoutineActionOutcomes.Done, result.Outcome);
        Assert.Equal(2, messages.Invocations.Count);
    }

    [Theory]
    [InlineData(null, RoutineActionOutcomes.Blocked)]
    [InlineData("2468", RoutineActionOutcomes.Done)]
    public async Task TheAccessCodeEmailRequiresTheCentralReleaseGate(string? code, string outcome)
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(RoutineRig.OrgA);
        var booking = await rig.SeedBookingAsync(RoutineRig.OrgA, guestId: guest.Id);
        await using var db = rig.NewDb();
        var access = new Mock<IAccessCodeService>(MockBehavior.Strict);
        access.Setup(service => service.ReleaseToGuestAsync(booking.Id, AccessCodeReleaseChannels.Email, default))
            .ReturnsAsync(code);
        var mail = new Mock<IEmailQueue>(MockBehavior.Strict);
        mail.Setup(queue => queue.Enqueue(guest.Email, It.IsAny<EmailContent>(), "guest-access-code")).Returns(true);
        var actions = new RoutineBookingActions(db, Mock.Of<IMessageReplyService>(), Mock.Of<IMessageTemplateService>(),
            access.Object, mail.Object, rig.Jobs.Object, TimeProvider.System);

        var result = await actions.ExecuteAsync(RoutineRig.OrgA, Guid.NewGuid(), 0,
            Action("SendAccessCode", "{}"), RoutinePayload.Normalize(RoutineRig.PayloadOf(booking)), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code is null ? 0 : 1, mail.Invocations.Count);
    }

    [Theory]
    [InlineData("NotifyOwner", PushTypes.RoutineOwner)]
    [InlineData("NotifyStaff", PushTypes.RoutineStaff)]
    [InlineData("NotifyTeam", PushTypes.RoutineTeam)]
    public async Task NotificationsGoToTheBellJob(string action, string type)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(RoutineRig.OrgA);
        await using var db = rig.NewDb();
        var actions = new RoutineBookingActions(db, Mock.Of<IMessageReplyService>(), Mock.Of<IMessageTemplateService>(),
            Mock.Of<IAccessCodeService>(), Mock.Of<IEmailQueue>(), rig.Jobs.Object, TimeProvider.System);

        var result = await actions.ExecuteAsync(RoutineRig.OrgA, Guid.NewGuid(), 0, Action(action, "{}"), RoutinePayload.Normalize(RoutineRig.PayloadOf(booking)), default);

        Assert.Equal(RoutineActionOutcomes.Done, result.Outcome);
        var job = Assert.Single(rig.Scheduled).Job;
        Assert.Equal(typeof(InAppNotificationJob), job.Type);
        var notification = Assert.IsType<QueuedInAppNotification>(job.Args[1]);
        Assert.Equal(type, notification.Type);
        Assert.Equal(booking.Id, notification.EntityId);
        Assert.Empty(await db.InternalActivities.ToListAsync());
    }

    [Fact]
    public async Task AddTagPreservesTheExistingTagsAndDoesNotDuplicateThem()
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(RoutineRig.OrgA);
        var booking = await rig.SeedBookingAsync(RoutineRig.OrgA, guestId: guest.Id);
        await using var db = rig.NewDb();
        var profile = new GuestCrmProfile { OrgId = RoutineRig.OrgA, TagsJson = """["Existing"]""" };
        db.GuestCrmProfiles.Add(profile);
        (await db.Guests.SingleAsync(g => g.Id == guest.Id)).GuestCrmProfileId = profile.Id;
        await db.SaveChangesAsync();
        var actions = new RoutineBookingActions(db, Mock.Of<IMessageReplyService>(), Mock.Of<IMessageTemplateService>(),
            Mock.Of<IAccessCodeService>(), Mock.Of<IEmailQueue>(), rig.Jobs.Object, TimeProvider.System);

        foreach (var tag in new[] { "VIP", "vip" })
        {
            var result = await actions.ExecuteAsync(RoutineRig.OrgA, Guid.NewGuid(), 0,
                Action("AddTag", $$"""{"tag":"{{tag}}"}"""), RoutinePayload.Normalize(RoutineRig.PayloadOf(booking)), default);
            Assert.Equal(RoutineActionOutcomes.Done, result.Outcome);
        }

        Assert.Equal(new[] { "Existing", "VIP" }, CrmProfileRules.ParseTags(profile.TagsJson));
    }

    private static RoutineAction Action(string type, string config)
    {
        Assert.True(RoutineAction.TryParseList(RoutineRig.Actions((type, config)), out var actions));
        return Assert.Single(actions);
    }
}
