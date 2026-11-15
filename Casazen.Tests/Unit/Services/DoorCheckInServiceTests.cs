using System.Net;
using System.Threading.RateLimiting;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E22/F01/US02/T01 proofs of the QR code at the door: the guest who types the right data reaches the check-in link of its stay and of THAT property only (permission), every wrong
/// datum answers the same thing and never says which part was wrong (privacy), and the endpoint is limited to five attempts a minute for each client IP and door token (permission).
/// </summary>
public class DoorCheckInServiceTests
{
    private const string Code = "ABCDE12345";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 20);

    /// <summary>Permission: a stay of another property (or of another org), with the right code and date, is not found by the door of this one, and no link is issued.</summary>
    [Fact]
    public async Task DoorCheckIn_OtherPropertyStayNotFound_Test()
    {
        await using var world = await World.CreateAsync();
        var otherPropertyStay = await world.AddStayAsync(world.OtherProperty.Id, "Verdi", "FGH7K2M9PQ", Today);
        var otherOrgStay = await world.AddStayAsync(world.ForeignProperty.Id, "Neri", "RSTV3WXYZ4", Today, world.ForeignOrgId);

        var byCode = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today));
        var bySurname = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(null, "Verdi", Today));
        var otherOrg = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput("RSTV3WXYZ4", null, Today));

        Assert.Equal(DoorCheckInResult.NotFound, byCode);
        Assert.Equal(DoorCheckInResult.NotFound, bySurname);
        Assert.Equal(DoorCheckInResult.NotFound, otherOrg);
        world.CheckIn.Verify(c => c.IssueLinkAsync(otherPropertyStay.Id, It.IsAny<Guid>()), Times.Never);
        world.CheckIn.Verify(c => c.IssueLinkAsync(otherOrgStay.Id, It.IsAny<Guid>()), Times.Never);
        world.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);

        // The door of the other property finds its own stay with the same data.
        var other = await world.Service().OpenAsync(world.OtherDoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today));
        Assert.True(other.Found);
        world.CheckIn.Verify(c => c.IssueLinkAsync(otherPropertyStay.Id, world.OrgId), Times.Once);
    }

    [Fact]
    public async Task DoorCheckIn_RightCodeAndArrivalDate_IssuesTheLinkOfTheStay()
    {
        await using var world = await World.CreateAsync();
        var stay = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);

        // The code as it is read on the e-mail: dash, small letters and the letters a person confuses.
        var result = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput("abcde-12345", null, Today));

        Assert.Equal(new DoorCheckInResult(World.IssuedToken), result);
        world.CheckIn.Verify(c => c.IssueLinkAsync(stay.Id, world.OrgId), Times.Once);
    }

    [Theory]
    [InlineData("Rossi")]
    [InlineData("  rossi ")]
    [InlineData("ROSSI")]
    public async Task DoorCheckIn_HeadGuestSurname_OpensTheStayWhenOnlyOneMatches(string typed)
    {
        await using var world = await World.CreateAsync();
        var stay = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);

        var result = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(null, typed, Today));

        Assert.True(result.Found);
        world.CheckIn.Verify(c => c.IssueLinkAsync(stay.Id, world.OrgId), Times.Once);
    }

    [Fact]
    public async Task DoorCheckIn_SurnameOfTheHeadOfGroupAndAccents_AreComparedWithoutThem()
    {
        await using var world = await World.CreateAsync();
        var stay = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        world.Db.StayGuests.AddRange(
            new StayGuest { BookingId = stay.Id, OrgId = world.OrgId, Position = 1, Type = StayGuestType.HeadOfGroup, FirstName = "Zoë", LastName = "D'Àngelo" },
            new StayGuest { BookingId = stay.Id, OrgId = world.OrgId, Position = 2, Type = StayGuestType.GroupMember, FirstName = "Ugo", LastName = "Gialli" });
        await world.Db.SaveChangesAsync();

        Assert.True((await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(null, "dangelo", Today))).Found);

        // A member of the group does not leave a document: his surname does not open the stay.
        Assert.Equal(DoorCheckInResult.NotFound, await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(null, "Gialli", Today)));
    }

    /// <summary>A surname that two arriving stays share opens none (it would be a guess among strangers), and the code is not asked from a code that does not exist.</summary>
    [Fact]
    public async Task DoorCheckIn_TwoStaysWithTheSameSurname_OpenNone()
    {
        await using var world = await World.CreateAsync();
        await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        await world.AddStayAsync(world.Property.Id, "Rossi", "FGH7K2M9PQ", Today);

        Assert.Equal(DoorCheckInResult.NotFound, await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(null, "Rossi", Today)));
        Assert.True((await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, Today))).Found);
    }

    /// <summary>
    /// Privacy: every way of not finding a stay is the same answer, at the service and at the endpoint (same status, same code, same text), so the door never says whether the code,
    /// the surname, the date or the door itself was wrong, nor that the stay exists.
    /// </summary>
    [Fact]
    public async Task DoorCheckIn_NoEnumeration_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        await world.AddStayAsync(world.Property.Id, "Cancellato", "BCDEF23456", Today, status: BookingStatus.Cancelled);
        await world.AddStayAsync(world.Property.Id, "Partito", "CDEFG34567", Today, status: BookingStatus.CheckedOut);
        await world.AddStayAsync(world.Property.Id, "Assente", "DEFGH45678", Today, noShow: true);
        await world.AddStayAsync(world.Property.Id, "Lontano", "EFGHJ56789", Today.AddDays(10));
        await world.AddStayAsync(world.Property.Id, "Passato", "FGHJK67891", Today.AddDays(-20));
        var done = await world.AddStayAsync(world.Property.Id, "Completo", "GHJKM78912", Today);
        world.CheckIn.Setup(c => c.GetSessionForBookingAsync(done.Id))
            .ReturnsAsync(new GuestCheckInSession { BookingId = done.Id, Status = GuestCheckInSessionStatus.Completo });

        var wrong = new (string DoorToken, DoorCheckInInput Input)[]
        {
            (world.DoorToken, new DoorCheckInInput("BCDEF23457", null, Today)),                // a code that does not exist
            (world.DoorToken, new DoorCheckInInput(Code, null, Today.AddDays(1))),             // the right code, the wrong date
            (world.DoorToken, new DoorCheckInInput(Code, null, null)),                         // no date
            (world.DoorToken, new DoorCheckInInput(null, "Bianchi", Today)),                   // a surname that does not exist
            (world.DoorToken, new DoorCheckInInput(null, "R", Today)),                         // not a surname
            (world.DoorToken, new DoorCheckInInput(null, null, Today)),                        // nothing typed
            (world.DoorToken, new DoorCheckInInput("not a code", null, Today)),                // not a code
            (world.DoorToken, new DoorCheckInInput("BCDEF23456", null, Today)),                // cancelled
            (world.DoorToken, new DoorCheckInInput("CDEFG34567", null, Today)),                // checked out
            (world.DoorToken, new DoorCheckInInput("DEFGH45678", null, Today)),                // no-show
            (world.DoorToken, new DoorCheckInInput("EFGHJ56789", null, Today.AddDays(10))),    // arrives in ten days
            (world.DoorToken, new DoorCheckInInput("FGHJK67891", null, Today.AddDays(-20))),   // over for weeks
            (world.DoorToken, new DoorCheckInInput("GHJKM78912", null, Today)),                // the check-in is complete
            (new string('x', 32), new DoorCheckInInput(Code, null, Today)),                    // a door token that does not exist
            ("short", new DoorCheckInInput(Code, null, Today)),                                // not even shaped like one
        };

        var service = world.Service();
        var controller = world.Controller(service);
        var answers = new List<(int? Status, string? Code, string? Detail)>();
        foreach (var (doorToken, input) in wrong)
        {
            Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(doorToken, input));

            var result = await controller.Open(
                doorToken,
                new DoorCheckInRequest { BookingCode = input.BookingCode, Surname = input.Surname, ArrivalDate = input.ArrivalDate },
                CancellationToken.None);
            var problem = Assert.IsType<ObjectResult>(result.Result);
            var details = Assert.IsType<ProblemDetails>(problem.Value);
            answers.Add((problem.StatusCode, details.Extensions["code"]?.ToString(), details.Detail));
        }

        var first = answers[0];
        Assert.Equal(StatusCodes.Status404NotFound, first.Status);
        Assert.Equal(PublicDoorCheckInController.NotFoundCode, first.Code);
        Assert.All(answers, answer => Assert.Equal(first, answer));
        world.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);

        // The one that opens answers the token and nothing else.
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.Open(world.DoorToken, new DoorCheckInRequest { BookingCode = Code, ArrivalDate = Today }, CancellationToken.None)).Result);
        Assert.Equal(World.IssuedToken, Assert.IsType<DoorCheckInResponse>(ok.Value).Token);
    }

    /// <summary>
    /// A stay already in progress opens the door whatever the day it arrived, and a stay that arrived yesterday or arrives tomorrow too; the date typed must be the arrival
    /// date of the stay (the proof is the date, not only the code).
    /// </summary>
    [Theory]
    [InlineData(-5, 2, true)]
    [InlineData(-1, 3, true)]
    [InlineData(1, 4, true)]
    [InlineData(2, 5, false)]
    [InlineData(-8, -1, false)]
    public async Task DoorCheckIn_TheDaysOfTheStay_DecideWhetherTheDoorServesIt(int arrivalOffset, int departureOffset, bool served)
    {
        await using var world = await World.CreateAsync();
        var arrival = Today.AddDays(arrivalOffset);
        await world.AddStayAsync(world.Property.Id, "Rossi", Code, arrival, departure: Today.AddDays(departureOffset));

        var result = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, arrival));

        Assert.Equal(served, result.Found);
    }

    [Fact]
    public async Task DoorCheckIn_TheDayIsTheDayInRome_NotTheDayInUtc()
    {
        await using var world = await World.CreateAsync();
        await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today.AddDays(1));

        // 23:30 UTC of the 20th is 00:30 of the 21st in Rome: the stay of the 21st arrives today.
        world.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero));
        Assert.True((await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, Today.AddDays(1)))).Found);
    }

    [Fact]
    public async Task DoorToken_IsMadeOnceAndReplacedByRotate_AndTheOldOneStopsWorking()
    {
        await using var world = await World.CreateAsync();
        var propertyId = world.Property.Id;
        var oldToken = world.DoorToken;
        await world.AddStayAsync(propertyId, "Rossi", Code, Today);

        world.OtherProperty.DoorToken = null;
        await world.Db.SaveChangesAsync();
        var first = await world.Service().GetOrCreateTokenAsync(world.OrgId, world.OtherProperty.Id);
        var again = await world.Service().GetOrCreateTokenAsync(world.OrgId, world.OtherProperty.Id);
        var rotated = await world.Service().RotateTokenAsync(world.OrgId, propertyId);

        Assert.True(PropertyDoorTokens.IsWellFormed(first));
        Assert.Equal(first, again);
        Assert.True(PropertyDoorTokens.IsWellFormed(rotated));
        Assert.NotEqual(oldToken, rotated);

        // The printed code of the old token answers like a code that never existed; the new one opens the stay.
        Assert.Equal(DoorCheckInResult.NotFound, await world.Service().OpenAsync(oldToken, new DoorCheckInInput(Code, null, Today)));
        Assert.True((await world.Service().OpenAsync(rotated!, new DoorCheckInInput(Code, null, Today))).Found);

        // A property of another org, or one that does not exist, has no token to make.
        Assert.Null(await world.Service().GetOrCreateTokenAsync(world.ForeignOrgId, propertyId));
        Assert.Null(await world.Service().RotateTokenAsync(world.OrgId, Guid.NewGuid()));
    }

    [Fact]
    public void DoorToken_IsRandomAndShaped_AndTheAddressHoldsOnlyIt()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => PropertyDoorTokens.New()).ToList();

        Assert.Equal(50, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token => Assert.True(PropertyDoorTokens.IsWellFormed(token)));
        Assert.False(PropertyDoorTokens.IsWellFormed(null));
        Assert.False(PropertyDoorTokens.IsWellFormed(new string('a', 31)));
        Assert.False(PropertyDoorTokens.IsWellFormed(new string('a', 31) + "+"));

        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" }));
        Assert.Equal($"https://site.example.test/checkin/porta/{tokens[0]}", links.DoorCheckIn(tokens[0]));
    }

    /// <summary>
    /// Permission: five attempts a minute for each client IP and door token, the sixth is refused; a made-up token cannot share the budget of the real one, and the partition holds a hash of
    /// the token, never the token. The endpoint is behind the flag of the identity verification and this policy.
    /// </summary>
    [Fact]
    public void DoorCheckIn_RateLimited_Test()
    {
        var policy = RateLimitingServiceCollectionExtensions.Policies.Single(p => p.Name == RateLimitPolicies.PublicDoorCheckIn);
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(policy, new ConfigurationBuilder().Build());
        Assert.Equal(5, options.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(0, options.QueueLimit);
        Assert.True(policy.PartitionByToken);
        Assert.Equal(RateLimitingServiceCollectionExtensions.DoorTokenRouteValue, policy.TokenRouteValue);

        // The sixth attempt of the same client at the same door is refused.
        using var limiter = new FixedWindowRateLimiter(options);
        for (var attempt = 1; attempt <= 5; attempt++)
            Assert.True(limiter.AttemptAcquire().IsAcquired);

        Assert.False(limiter.AttemptAcquire().IsAcquired);

        // The partition is the client and the door token, and never the raw token.
        var one = Context("203.0.113.7", "door-token-one");
        var sameAgain = Context("203.0.113.7", "door-token-one");
        var otherDoor = Context("203.0.113.7", "door-token-two");
        var otherClient = Context("203.0.113.8", "door-token-one");
        string Key(HttpContext context) =>
            RateLimitingServiceCollectionExtensions.GetPartitionKey(context, policy.PartitionByToken, policy.PartitionByUser, policy.TokenRouteValue);

        Assert.Equal(Key(one), Key(sameAgain));
        Assert.NotEqual(Key(one), Key(otherDoor));
        Assert.NotEqual(Key(one), Key(otherClient));
        Assert.DoesNotContain("door-token-one", Key(one));

        var method = typeof(PublicDoorCheckInController).GetMethod(nameof(PublicDoorCheckInController.Open))!;
        Assert.Equal(RateLimitPolicies.PublicDoorCheckIn, Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true).Cast<EnableRateLimitingAttribute>()).PolicyName);
        Assert.Equal(FeatureFlags.CheckInIdentityVerification, Assert.Single(method.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: true).Cast<FeatureGateAttribute>()).Flag);
        Assert.NotEmpty(typeof(PublicDoorCheckInController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));

        static DefaultHttpContext Context(string address, string doorToken)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Request.RouteValues[RateLimitingServiceCollectionExtensions.DoorTokenRouteValue] = doorToken;
            return context;
        }
    }

    /// <summary>The stays, the properties and the check-in service that issues a link, in an in-memory database; the clock says 10:00 of 2026-11-20 in Rome.</summary>
    private sealed class World : IAsyncDisposable
    {
        public const string IssuedToken = "issued-check-in-token";

        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"door-checkin-{Guid.NewGuid()}").Options;

        private World()
        {
            Db = new AppDbContext(_options, null, new EphemeralDataProtectionProvider());
            CheckIn.Setup(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
                .ReturnsAsync((Guid bookingId, Guid _) => new IssuedCheckInLink(Guid.NewGuid(), IssuedToken, Now.UtcDateTime.AddDays(7)));
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid ForeignOrgId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public Mock<IGuestCheckInService> CheckIn { get; } = new();

        public FakeTimeProvider Clock { get; } = new(Now);

        public Property Property { get; private set; } = null!;

        public Property OtherProperty { get; private set; } = null!;

        public Property ForeignProperty { get; private set; } = null!;

        public string DoorToken => Property.DoorToken!;

        public string OtherDoorToken => OtherProperty.DoorToken!;

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world.Property = world.NewProperty(world.OrgId, "Casa del cancello");
            world.OtherProperty = world.NewProperty(world.OrgId, "Casa accanto");
            world.ForeignProperty = world.NewProperty(world.ForeignOrgId, "Casa di altri");
            world.Db.Orgs.AddRange(NewOrg(world.OrgId), NewOrg(world.ForeignOrgId));
            world.Db.Properties.AddRange(world.Property, world.OtherProperty, world.ForeignProperty);
            await world.Db.SaveChangesAsync();
            return world;
        }

        public DoorCheckInService Service() => new(Db, CheckIn.Object, NullLogger<DoorCheckInService>.Instance, Clock);

        public PublicDoorCheckInController Controller(DoorCheckInService service) => new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

        public async Task<Booking> AddStayAsync(
            Guid propertyId,
            string lastName,
            string bookingCode,
            DateOnly arrival,
            Guid? orgId = null,
            BookingStatus status = BookingStatus.Confirmed,
            bool noShow = false,
            DateOnly? departure = null)
        {
            var org = orgId ?? OrgId;
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = org,
                FirstName = "Mario",
                LastName = lastName,
                Email = $"{lastName.ToLowerInvariant()}@example.com",
                DateOfBirth = new DateTime(1985, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = org,
                PropertyId = propertyId,
                GuestId = guest.Id,
                BookingCode = bookingCode,
                CheckInDate = arrival.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CheckOutDate = (departure ?? arrival.AddDays(3)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Status = status,
                Source = BookingSource.Direct,
                NumberOfGuests = 2,
                NoShowAt = noShow ? Now.UtcDateTime : null,
                TotalPrice = 300m,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            await Db.SaveChangesAsync();
            return booking;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();

        private static OrgEntity NewOrg(Guid id) => new()
        {
            Id = id,
            Name = "Agenzia porta",
            Slug = $"porta-{id:N}"[..20],
            DisplayName = "Agenzia porta",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Agency,
        };

        private Property NewProperty(Guid orgId, string name) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = name,
            Address = $"Via {name} 1",
            City = "Roma",
            PostalCode = "00100",
            Country = "IT",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            DoorToken = PropertyDoorTokens.New(),
        };
    }
}
