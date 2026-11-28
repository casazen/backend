using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// NF-140: the answer of the public check-in link (<c>GET api/public/checkin/{token}</c>) always says which org the stay belongs to (<c>orgSlug</c>), so the page
/// on an agency's own domain can refuse the link of another agency. Read through the real service and the real controller, as the JSON the page receives:
/// <c>privacyNoticeOrgSlug</c> keeps its own meaning, the slug the consent step links the notice through, and is absent while the org has no published notice.
/// </summary>
public class PublicGuestCheckInContextOrgSlugTests
{
    private const string OrgSlug = "agenzia-uno";

    private sealed record Seed(AppDbContext Db, Guid BookingId, Guid OrgId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    /// <summary>The case the page could not decide before: an org with no privacy notice. The answer names the org, and the consent step still has no notice slug.</summary>
    [Fact]
    public async Task GetContext_OpenLinkOfAnOrgWithNoPrivacyNotice_AnswersTheOrgSlugAndNoNoticeSlug()
    {
        await using var seed = await SeedAsync(publishedPrivacyNotice: false);
        var token = await new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance).CreateSessionAsync(seed.BookingId, seed.OrgId);

        var json = await ContextAsync(seed.Db, token);

        Assert.False(json.GetProperty("completed").GetBoolean());
        Assert.Equal(OrgSlug, json.GetProperty("orgSlug").GetString());
        Assert.False(json.TryGetProperty("privacyNoticeOrgSlug", out _));
    }

    /// <summary>With a published notice the answer is what it was (the notice slug), and carries the org slug beside it.</summary>
    [Fact]
    public async Task GetContext_OpenLinkWithAPublishedPrivacyNotice_AnswersBothSlugs()
    {
        await using var seed = await SeedAsync(publishedPrivacyNotice: true);
        var token = await new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance).CreateSessionAsync(seed.BookingId, seed.OrgId);

        var json = await ContextAsync(seed.Db, token);

        Assert.Equal(OrgSlug, json.GetProperty("orgSlug").GetString());
        Assert.Equal(OrgSlug, json.GetProperty("privacyNoticeOrgSlug").GetString());
    }

    /// <summary>
    /// A5-28 and NF-140: once the guest has submitted, the link answers that the check-in is done and which org it belongs to, public data: no other
    /// property, so nothing of the booking and of the guests (the notice slug is not one of them, it belongs to the consent step).
    /// </summary>
    [Fact]
    public async Task GetContext_CompletedLink_AnswersOnlyCompletedStatusAndTheOrgSlug()
    {
        await using var seed = await SeedAsync(publishedPrivacyNotice: true);
        var service = new GuestCheckInService(seed.Db, NullLogger<GuestCheckInService>.Instance);
        var token = await service.CreateSessionAsync(seed.BookingId, seed.OrgId);
        Assert.True((await service.SubmitAsync(token, ValidGuests())).Success);

        var json = await ContextAsync(seed.Db, token);

        Assert.True(json.GetProperty("completed").GetBoolean());
        Assert.Equal(nameof(GuestCheckInSessionStatus.Completo), json.GetProperty("status").GetString());
        Assert.Equal(OrgSlug, json.GetProperty("orgSlug").GetString());
        var properties = json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "completed", "orgSlug", "status" }, properties);
    }

    /// <summary>The answer of the controller for the token, as the JSON the page receives (the camel case of the web defaults).</summary>
    private static async Task<JsonElement> ContextAsync(AppDbContext db, string token)
    {
        var controller = Controller(new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance));

        var ok = Assert.IsType<OkObjectResult>((await controller.GetContext(token)).Result);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    private static async Task<Seed> SeedAsync(bool publishedPrivacyNotice)
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"checkin-orgslug-{Guid.NewGuid()}").Options);
        var org = new Casazen.Core.Entities.Org { Slug = OrgSlug, Name = "Agenzia Uno", DisplayName = "Agenzia Uno" };
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        db.Orgs.Add(org);
        if (publishedPrivacyNotice)
            db.OrgSiteDocuments.Add(new OrgSiteDocument { Org = org, Kind = OrgSiteDocumentKind.Privacy, Version = 1 });

        db.Guests.Add(new Guest { Id = guestId, FirstName = "Luigi", LastName = "Verdi", Email = "luigi@example.com" });
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = org.Id,
            OwnerId = "owner-test",
            Name = "Test Property",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            IsActive = true,
        });
        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            OrgId = org.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(3),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(6),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        });
        await db.SaveChangesAsync();
        return new Seed(db, bookingId, org.Id);
    }

    private static GuestCheckInSubmitRequest ValidGuests() => new()
    {
        Guests =
        [
            new StayGuestInput
            {
                Type = "SingleGuest",
                FirstName = "Luigi",
                LastName = "Verdi",
                DateOfBirth = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc),
                Gender = Gender.Male,
                BornInItaly = true,
                BirthComuneName = "Roma",
                BirthProvince = "rm",
                CitizenshipName = "Italia",
                DocumentType = "Passport",
                DocumentNumber = "YA1234567",
                DocumentIssuePlaceName = "Roma",
            },
        ],
    };

    private static PublicGuestCheckInController Controller(IGuestCheckInService checkIn)
    {
        return new PublicGuestCheckInController(
            checkIn,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<ICheckInDocumentReader>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IIdentityVerificationFlowService>(),
            Mock.Of<Casazen.Core.Features.IFeatureFlags>(),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<PublicGuestCheckInController>.Instance)
        {
            // The page reads the Accept-Language of the request to open in the guest's language (NF-40): a controller built by hand needs a request.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
