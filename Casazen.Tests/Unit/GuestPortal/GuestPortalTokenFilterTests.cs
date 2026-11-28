using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalTokenFilterTests
{
    [Fact]
    public async Task UnknownRevokedExpiredMalformedToken_SameStatusBodyAndHeaders()
    {
        using var world = new GuestPortalTestWorld();
        var links = world.LinkService();
        var revoked = (await links.GetOrCreateAsync(world.Org.Id, world.Booking.Id, createdByUserId: null))!.Link.Token;
        var active = (await links.RegenerateAsync(world.Org.Id, world.Booking.Id, createdByUserId: null))!.Token;
        var expired = await AddExpiredLinkAsync(world);
        var unknown = "Unknown_Token_0000000000000000000";

        var answers = new List<string>();
        foreach (var token in new[] { unknown, revoked, expired, "not a token!", new string('x', GuestPortalLink.TokenMaxLength + 1) })
        {
            var (filter, accessor) = world.TokenFilter();

            var (result, response, actionRan) = await GuestPortalTestWorld.RunAsync(filter, token);

            Assert.False(actionRan, $"{token}: the action must not run");
            Assert.Null(accessor.Current);
            var answer = Describe(Assert.IsType<ObjectResult>(result), response);
            Assert.DoesNotContain(token, answer, StringComparison.Ordinal);
            answers.Add(answer);
        }

        Assert.Single(answers.Distinct());

        var (lastFilter, _) = world.TokenFilter();
        var (last, lastResponse, _) = await GuestPortalTestWorld.RunAsync(lastFilter, unknown);
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(last).Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal(GuestPortalRules.LinkInvalidCode, problem.Extensions["code"]);
        Assert.Equal("/api/public/guest-portal", problem.Instance);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.Equal("no-store", lastResponse.Headers.CacheControl.ToString());
        Assert.Equal("noindex", lastResponse.Headers["X-Robots-Tag"].ToString());

        var (okFilter, okAccessor) = world.TokenFilter();
        var (okResult, _, okRan) = await GuestPortalTestWorld.RunAsync(okFilter, active);
        Assert.Null(okResult);
        Assert.True(okRan);
        Assert.Equal((world.Org.Id, world.Booking.Id, world.Property.Id), (okAccessor.Current!.OrgId, okAccessor.Current.BookingId, okAccessor.Current.PropertyId));
    }

    private static string Describe(ObjectResult result, HttpResponse response)
    {
        var body = JsonSerializer.Serialize(result.Value, result.Value!.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var headers = string.Join(";", response.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}={h.Value}"));
        return $"{result.StatusCode}|{string.Join(",", result.ContentTypes)}|{body}|{headers}";
    }

    private static async Task<string> AddExpiredLinkAsync(GuestPortalTestWorld world)
    {
        var guest = new Guest { OrgId = world.Org.Id, FirstName = "Bruno", LastName = "Passato", Email = $"{Guid.NewGuid():N}@example.com" };
        var past = new Booking
        {
            OrgId = world.Org.Id,
            PropertyId = world.Property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        var link = new GuestPortalLink
        {
            OrgId = world.Org.Id,
            BookingId = past.Id,
            Token = "Expired_Token_00000000000000000000",
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiresAt = GuestPortalRules.ExpiryFor(past.CheckOutDate),
        };
        world.Db.Guests.Add(guest);
        world.Db.Bookings.Add(past);
        world.Db.GuestPortalLinks.Add(link);
        await world.Db.SaveChangesAsync();
        world.Db.ChangeTracker.Clear();
        return link.Token;
    }
}
