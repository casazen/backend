using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// E50/F01/US01/T01, T02 (NF-152, AC-NF152-5, decision A3): the token of a stay link is a secret that opens the stay, so it is in no log line, no
/// exception and no partition of the rate limiter; the logs of the portal carry the id of the link.
/// </summary>
public class GuestPortalLoggingTests
{
    /// <summary>
    /// Privacy rule: the whole life of a link (made, made again, extended by a later departure, opened with a section that fails, opened with a revoked and
    /// an unknown token, revoked) writes log lines, and none of them, with their values, scopes and exceptions, holds a token; they name the link by its
    /// id. The partition of the limiter holds a hash of the token, never the token.
    /// </summary>
    [Fact]
    public async Task Request_WithToken_NeverLogsTheToken()
    {
        using var world = new GuestPortalTestWorld();
        var links = world.LinkService();
        var first = (await links.GetOrCreateAsync(world.Org.Id, world.Booking.Id, "auth0|titolare-aurora"))!.Link.Token;
        var active = (await links.RegenerateAsync(world.Org.Id, world.Booking.Id, "auth0|titolare-aurora"))!;

        // The departure moves later: the next request extends the link, and says so in the log.
        var booking = await world.Db.Bookings.SingleAsync(b => b.Id == world.Booking.Id);
        booking.CheckOutDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        await world.Db.SaveChangesAsync();

        var (filter, accessor) = world.TokenFilter();
        var overview = world.OverviewService(sections: [new StayPortalSection(), new ThrowingGuideSection()]);
        var (_, _, ran) = await GuestPortalTestWorld.RunAsync(filter, active.Token, () => overview.GetAsync(accessor.Current!));
        Assert.True(ran);

        foreach (var token in new[] { first, "Unknown_Token_0000000000000000000" })
        {
            var (otherFilter, _) = world.TokenFilter();
            await GuestPortalTestWorld.RunAsync(otherFilter, token);
        }

        Assert.True(await links.RevokeAsync(world.Org.Id, world.Booking.Id));

        var lines = world.Logs.Lines.ToList();
        Assert.NotEmpty(lines);
        foreach (var token in new[] { first, active.Token })
            Assert.DoesNotContain(lines, line => line.Contains(token, StringComparison.Ordinal));

        // The id of the link is what the lines carry: on the creation, the extension and the failure of a section inside the request.
        Assert.Contains(lines, line => line.Contains("made for booking", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("extended", StringComparison.Ordinal) && line.Contains(active.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("SectionKey=guide", StringComparison.Ordinal)
                    && line.Contains($"GuestPortalLinkId={active.Id}", StringComparison.Ordinal));

        // The limiter partitions by the client and a hash of the token of the route.
        var http = new DefaultHttpContext();
        http.Request.RouteValues[GuestPortalTokenFilter.TokenRouteValue] = active.Token;
        var partition = RateLimitingServiceCollectionExtensions.GetPartitionKey(http, partitionByToken: true);
        Assert.DoesNotContain(active.Token, partition, StringComparison.Ordinal);
        Assert.Contains("|", partition, StringComparison.Ordinal);
    }
}
