using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

public sealed class StorefrontChatTests
{
    [Fact]
    public async Task VisitorAndHostUseTheSameConversationWithoutABooking()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        world.Granted.RentalMode = RentalMode.Short;
        world.Granted.IsActive = true;
        world.Granted.IsPaused = false;
        world.Granted.ComplianceStatus = PropertyComplianceStatus.Active;
        world.Granted.PublishedOnSite = true;
        db.Properties.Update(world.Granted);
        await db.SaveChangesAsync();

        var chat = new StorefrontChatService(db);
        var hub = MessageHubTestKit.Service(db);
        var access = new string('a', 64);
        var ct = CancellationToken.None;
        await chat.SendAsync(world.OrgId, world.Granted.Id, access, "visitor-first", "Is there a cot?", "Visitor", "visitor@example.com", true, ct);
        await chat.SendAsync(world.OrgId, world.Granted.Id, access, "visitor-first", "Is there a cot?", "Visitor", "visitor@example.com", true, ct);
        db.ChangeTracker.Clear();

        var listed = await hub.ListAsync(world.Restricted, new MessageHubFilter(Channel: MessageHubChannel.Storefront), 1, 20, ct);
        var conversation = Assert.Single(listed.Items);
        Assert.Equal(MessageHubThreadKind.Request, conversation.Kind);
        Assert.Equal(1, conversation.UnreadCount);
        Assert.Null(conversation.BookingId);
        Assert.Equal("Is there a cot?", conversation.Preview);

        var host = await hub.GetThreadAsync(world.Restricted, conversation.Key, false, ct);
        Assert.Equal("visitor@example.com", host.VisitorEmail);
        await hub.MarkReadAsync(world.Restricted, conversation.Key, null, ct);
        await chat.ReplyAsync(world.Restricted, conversation.Key, "host-first", "Yes, on request.", ct);
        var replay = await chat.ReplyAsync(world.Restricted, conversation.Key, "host-first", "Yes, on request.", ct);
        Assert.True(replay.Replayed);

        var visitor = await chat.GetAsync(world.OrgId, world.Granted.Id, access, ct);
        Assert.Collection(visitor,
            question => Assert.False(question.FromHost),
            reply => { Assert.True(reply.FromHost); Assert.Equal("Yes, on request.", reply.Text); });
        await Assert.ThrowsAsync<NotFoundException>(() => chat.GetAsync(world.OrgId, world.Granted.Id, new string('b', 64), ct));
        await Assert.ThrowsAsync<NotFoundException>(() => chat.GetAsync(world.OtherOrgId, world.Granted.Id, access, ct));
        await Assert.ThrowsAsync<NotFoundException>(() => hub.GetThreadAsync(new(world.OtherOrgId), conversation.Key, false, ct));
    }
}
