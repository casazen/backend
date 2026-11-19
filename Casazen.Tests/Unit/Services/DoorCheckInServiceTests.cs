using System.Net;
using System.Text.Json;
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
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
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
        world.CheckIn.Verify(c => c.ReissueLinkAsync(otherPropertyStay.Id, It.IsAny<Guid>()), Times.Never);
        world.CheckIn.Verify(c => c.ReissueLinkAsync(otherOrgStay.Id, It.IsAny<Guid>()), Times.Never);
        world.CheckIn.Verify(c => c.ReissueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);

        // The door of the other property finds its own stay with the same data.
        var other = await world.Service().OpenAsync(world.OtherDoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today));
        Assert.True(other.Found);
        world.CheckIn.Verify(c => c.ReissueLinkAsync(otherPropertyStay.Id, world.OrgId), Times.Once);
    }

    [Fact]
    public async Task DoorCheckIn_RightCodeAndArrivalDate_IssuesTheLinkOfTheStay()
    {
        await using var world = await World.CreateAsync();
        var stay = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);

        // The code as it is read on the e-mail: dash, small letters and the letters a person confuses.
        var result = await world.Service().OpenAsync(world.DoorToken, new DoorCheckInInput("abcde-12345", null, Today));

        Assert.Equal(new DoorCheckInResult(World.IssuedToken), result);
        world.CheckIn.Verify(c => c.ReissueLinkAsync(stay.Id, world.OrgId), Times.Once);
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
        world.CheckIn.Verify(c => c.ReissueLinkAsync(stay.Id, world.OrgId), Times.Once);
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
        world.CheckIn.Verify(c => c.ReissueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);

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

    /// <summary>
    /// Data and permission (E22/F01/US02 post-review): the guest at the door never gets the attempts of the face check again. A guest that spent two of its three attempts and opens the door
    /// gets the SAME session with a new token: every earlier link is dead, the counter stays at two (the third attempt is the last), and the consent, the document read and the language stay with it.
    /// </summary>
    [Fact]
    public async Task DoorCheckIn_SecondOpening_KeepsTheAttemptsAlreadySpent_Test()
    {
        await using var world = await World.CreateAsync();
        var stay = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        var deliveries = new Mock<ICheckInLinkDeliveryService>();
        deliveries.Setup(d => d.EndAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var checkIn = world.RealCheckIn(deliveries.Object);
        var emailedLink = await checkIn.CreateSessionAsync(stay.Id, world.OrgId);
        var session = await world.Db.GuestCheckInSessions.SingleAsync(s => s.BookingId == stay.Id);
        session.Status = GuestCheckInSessionStatus.InCompilazione;
        session.BiometricAttempts = 2;
        session.BiometricConsentAt = Now.UtcDateTime;
        session.BiometricConsentVersion = "checkin-biometric-2026-11";
        session.BiometricConsentTextHash = new string('a', 64);
        var documentCheck = new DocumentCheck(true, true, false, true, true, true, true, false);
        var documentJson = JsonSerializer.Serialize(
            new StoredDocumentCheck(documentCheck, "front-photo", DateTime.UnixEpoch, Front: new StoredDocumentPicture(true, true, documentCheck, "front-proof"), ProofKey: "first-link"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        session.DocumentCheckJson = documentJson;
        session.Language = "de";
        session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Sent;
        session.SentAt = Now.UtcDateTime;
        await world.Db.SaveChangesAsync();
        var service = world.Service(checkIn);

        var first = await service.OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, Today));
        var second = await service.OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, Today));

        Assert.True(first.Found);
        Assert.True(second.Found);
        Assert.NotEqual(first.Token, second.Token);
        var kept = await world.Db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == stay.Id);
        Assert.Equal(session.Id, kept.Id);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, kept.Status);
        Assert.Equal(2, kept.BiometricAttempts);
        Assert.NotNull(kept.BiometricConsentAt);
        Assert.Equal("checkin-biometric-2026-11", kept.BiometricConsentVersion);
        Assert.Equal(new string('a', 64), kept.BiometricConsentTextHash);
        Assert.Equal(documentJson, kept.DocumentCheckJson);
        Assert.Equal("de", kept.Language);
        Assert.Equal(GuestCheckInService.HashToken(second.Token!), kept.TokenHash);

        // The e-mail of the old link told of a link that is dead: the renewed one is a link to copy. The delivery of the dead link ended each time.
        Assert.Equal(GuestCheckInLinkEmailStatus.NotRequested, kept.LinkEmailStatus);
        Assert.Null(kept.SentAt);
        deliveries.Verify(d => d.EndAsync(world.OrgId, stay.Id, CheckInLinkEndReasons.LinkReplaced, It.IsAny<CancellationToken>()), Times.Exactly(2));

        // Every earlier link is dead; the last one opens the same session.
        Assert.Null(await checkIn.GetSessionByTokenAsync(emailedLink));
        Assert.Null(await checkIn.GetSessionByTokenAsync(first.Token!));
        var current = Assert.IsType<GuestCheckInSession>(await checkIn.GetSessionByTokenAsync(second.Token!));
        Assert.Equal(session.Id, current.Id);

        // The face check says it too: one attempt left, so the third attempt is the last.
        var state = await world.Flow().GetStateAsync(current, CancellationToken.None);
        Assert.Equal(1, state.AttemptsRemaining);
    }

    /// <summary>
    /// A stay with no open link gets a new session, as before; a link past its expiry is not renewed (its window is over): it is expired and a new session is made. Of two open links the one the
    /// guest works on is kept with its attempts, and the other one is expired.
    /// </summary>
    [Fact]
    public async Task DoorCheckIn_NoOpenLink_MakesANewSession_AndTwoOpenLinksKeepTheOneInProgress()
    {
        await using var world = await World.CreateAsync();
        var noLink = await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        var pastExpiry = await world.AddStayAsync(world.Property.Id, "Verdi", "FGH7K2M9PQ", Today);
        var twoLinks = await world.AddStayAsync(world.Property.Id, "Neri", "RSTV3WXYZ4", Today);
        var checkIn = world.RealCheckIn();
        var oldToken = await checkIn.CreateSessionAsync(pastExpiry.Id, world.OrgId);
        var past = await world.Db.GuestCheckInSessions.SingleAsync(s => s.BookingId == pastExpiry.Id);
        past.ExpiresAt = Now.UtcDateTime.AddHours(-1);
        past.BiometricAttempts = 2;
        var neverOpened = NewSession(twoLinks.Id, world.OrgId, GuestCheckInSessionStatus.Inviato, 0, Now.UtcDateTime.AddHours(-2));
        var inProgress = NewSession(twoLinks.Id, world.OrgId, GuestCheckInSessionStatus.InCompilazione, 1, Now.UtcDateTime.AddHours(-3));
        world.Db.GuestCheckInSessions.AddRange(neverOpened, inProgress);
        await world.Db.SaveChangesAsync();
        var service = world.Service(checkIn);

        var fresh = await service.OpenAsync(world.DoorToken, new DoorCheckInInput(Code, null, Today));
        var afterExpiry = await service.OpenAsync(world.DoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today));
        var kept = await service.OpenAsync(world.DoorToken, new DoorCheckInInput("RSTV3WXYZ4", null, Today));

        Assert.True(fresh.Found);
        Assert.Single(await world.Db.GuestCheckInSessions.AsNoTracking().Where(s => s.BookingId == noLink.Id).ToListAsync());
        Assert.NotNull(await checkIn.GetSessionByTokenAsync(fresh.Token!));

        Assert.True(afterExpiry.Found);
        var pastSessions = await world.Db.GuestCheckInSessions.AsNoTracking().Where(s => s.BookingId == pastExpiry.Id).ToListAsync();
        Assert.Equal(2, pastSessions.Count);
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, pastSessions.Single(s => s.Id == past.Id).Status);
        Assert.Equal(0, pastSessions.Single(s => s.Id != past.Id).BiometricAttempts);
        Assert.Null(await checkIn.GetSessionByTokenAsync(oldToken));

        Assert.True(kept.Found);
        var twoSessions = await world.Db.GuestCheckInSessions.AsNoTracking().Where(s => s.BookingId == twoLinks.Id).ToListAsync();
        Assert.Equal(GuestCheckInSessionStatus.Scaduto, twoSessions.Single(s => s.Id == neverOpened.Id).Status);
        var stillOpen = twoSessions.Single(s => s.Id == inProgress.Id);
        Assert.Equal(GuestCheckInSessionStatus.InCompilazione, stillOpen.Status);
        Assert.Equal(1, stillOpen.BiometricAttempts);
        Assert.Equal(GuestCheckInService.HashToken(kept.Token!), stillOpen.TokenHash);

        static GuestCheckInSession NewSession(Guid bookingId, Guid orgId, GuestCheckInSessionStatus status, int attempts, DateTime createdAt) => new()
        {
            BookingId = bookingId,
            OrgId = orgId,
            TokenHash = GuestCheckInService.HashToken(Guid.NewGuid().ToString("N")),
            ExpiresAt = Now.UtcDateTime.AddDays(3),
            Status = status,
            BiometricAttempts = attempts,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    /// <summary>
    /// Permission (E22/F01/US02 post-review): the failed attempts of a door count for every client together, because the limit per IP does not bound a surname and a date guessed from many
    /// addresses. A door with its 20 failed attempts of the hour is closed to everybody, the right data included, with the same 404 as for any wrong datum (no oracle); the attempts that open
    /// never count; the failures age out; another door, a made-up token and the door after «Rigenera» are not touched.
    /// </summary>
    [Fact]
    public async Task DoorCheckIn_TooManyFailedAttemptsAtOneDoor_CloseItToEverybodyWithTheSameAnswer_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AddStayAsync(world.Property.Id, "Rossi", Code, Today);
        await world.AddStayAsync(world.OtherProperty.Id, "Verdi", "FGH7K2M9PQ", Today);
        var tracker = new DoorCheckInFailureTracker(world.Clock);
        var service = world.Service(tracker);
        var controller = world.Controller(service);
        var right = new DoorCheckInInput(Code, null, Today);
        var wrong = new[]
        {
            new DoorCheckInInput("BCDEF23457", null, Today),    // a code that does not exist
            new DoorCheckInInput(Code, null, Today.AddDays(1)), // the right code, the wrong date
            new DoorCheckInInput(null, "Bianchi", Today),       // a surname nobody has
        };

        // The attempts that open never count, however many.
        for (var attempt = 0; attempt < DoorCheckInRules.FailureLimit + 5; attempt++)
            Assert.True((await service.OpenAsync(world.DoorToken, right)).Found);

        // One failure short of the limit the door still opens.
        for (var attempt = 0; attempt < DoorCheckInRules.FailureLimit - 1; attempt++)
            Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.DoorToken, wrong[attempt % wrong.Length]));

        Assert.False(tracker.IsLocked(world.DoorToken));
        Assert.True((await service.OpenAsync(world.DoorToken, right)).Found);

        // The failure that reaches the limit closes the door: nothing opens it now, the right data neither.
        Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.DoorToken, wrong[0]));
        Assert.True(tracker.IsLocked(world.DoorToken));
        Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.DoorToken, right));
        world.CheckIn.Invocations.Clear();
        Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.DoorToken, right));
        world.CheckIn.Verify(c => c.ReissueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);

        // At the endpoint it is the same 404, with the same code and text, as for a wrong datum: the answer never says that the door is closed.
        var closed = Assert.IsType<ObjectResult>(
            (await controller.Open(world.DoorToken, new DoorCheckInRequest { BookingCode = Code, ArrivalDate = Today }, CancellationToken.None)).Result);
        var guess = Assert.IsType<ObjectResult>(
            (await controller.Open(world.DoorToken, new DoorCheckInRequest { BookingCode = "BCDEF23457", ArrivalDate = Today }, CancellationToken.None)).Result);
        var closedDetails = Assert.IsType<ProblemDetails>(closed.Value);
        var guessDetails = Assert.IsType<ProblemDetails>(guess.Value);
        Assert.Equal(StatusCodes.Status404NotFound, closed.StatusCode);
        Assert.Equal(guess.StatusCode, closed.StatusCode);
        Assert.Equal(PublicDoorCheckInController.NotFoundCode, closedDetails.Extensions["code"]?.ToString());
        Assert.Equal(guessDetails.Extensions["code"]?.ToString(), closedDetails.Extensions["code"]?.ToString());
        Assert.Equal(guessDetails.Detail, closedDetails.Detail);

        // Another door is not touched, and a made-up token costs nothing (it is not a door, so it is not counted).
        Assert.True((await service.OpenAsync(world.OtherDoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today))).Found);
        for (var attempt = 0; attempt < DoorCheckInRules.FailureLimit + 5; attempt++)
            Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(new string('x', 32), wrong[0]));
        Assert.False(tracker.IsLocked(new string('x', 32)));

        // «Rigenera» gives a new door with no failures: the stay opens at the new token.
        var rotated = await service.RotateTokenAsync(world.OrgId, world.Property.Id);
        Assert.True((await service.OpenAsync(rotated!, right)).Found);

        // The failures age out: the second door, closed by its own, opens again when they are out of the window.
        for (var attempt = 0; attempt < DoorCheckInRules.FailureLimit; attempt++)
            Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.OtherDoorToken, wrong[0]));
        Assert.True(tracker.IsLocked(world.OtherDoorToken));
        Assert.Equal(DoorCheckInResult.NotFound, await service.OpenAsync(world.OtherDoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today)));

        world.Clock.SetUtcNow(Now + DoorCheckInRules.FailureWindow + TimeSpan.FromMinutes(1));
        Assert.False(tracker.IsLocked(world.OtherDoorToken));
        Assert.True((await service.OpenAsync(world.OtherDoorToken, new DoorCheckInInput("FGH7K2M9PQ", null, Today))).Found);
    }

    /// <summary>
    /// Shape of the door endpoint (E22/F01/US02 post-review): the body of an anonymous call is small (2 KB, as the other anonymous calls of the guests), and next to the policy per client IP and
    /// door token, which a client that changes the token at every request does not meet twice, a limit per client IP over every door applies: 30 a minute, the 31st is a 429 with Retry-After before
    /// the door is read, and another client has its own budget.
    /// </summary>
    [Fact]
    public async Task DoorCheckIn_TheEndpointHasASmallBody_AndAClientHasOneBudgetOverEveryDoor_Test()
    {
        var method = typeof(PublicDoorCheckInController).GetMethod(nameof(PublicDoorCheckInController.Open))!;
        var size = Assert.Single(method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: true).Cast<RequestSizeLimitAttribute>());
        Assert.Equal((long?)PublicDoorCheckInController.MaxRequestBytes, ((IRequestSizeLimitMetadata)size).MaxRequestBodySize);
        Assert.Equal(2048, PublicDoorCheckInController.MaxRequestBytes);
        Assert.Single(method.GetCustomAttributes(typeof(DoorCheckInIpRateLimitAttribute), inherit: true));
        Assert.Contains(
            new ServiceCollection().AddCasazenRateLimiting(),
            descriptor => descriptor.ServiceType == typeof(DoorCheckInIpRateLimiter) && descriptor.Lifetime == ServiceLifetime.Singleton);

        using var limiter = new DoorCheckInIpRateLimiter(new ConfigurationBuilder().Build());
        Assert.Equal("PublicDoorCheckInPerIp", limiter.PolicyName);
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.Window);
        var filter = new DoorCheckInIpRateLimitFilter(limiter, NullLogger<DoorCheckInIpRateLimitFilter>.Instance);

        for (var request = 1; request <= 30; request++)
            Assert.True((await RunAsync("203.0.113.7")).ActionRan);

        var over = await RunAsync("203.0.113.7");
        Assert.False(over.ActionRan);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(over.Result).StatusCode);
        Assert.InRange(int.Parse(over.Http.Response.Headers.RetryAfter.ToString()), 1, 60);
        Assert.True((await RunAsync("203.0.113.8")).ActionRan);

        async Task<(bool ActionRan, IActionResult? Result, DefaultHttpContext Http)> RunAsync(string address)
        {
            // The answer is localized from the resources of the product, so the context needs the localizer.
            var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider() };
            http.Connection.RemoteIpAddress = IPAddress.Parse(address);
            var context = new ActionExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                [],
                new Dictionary<string, object?>(),
                controller: new object());
            var ran = false;
            await filter.OnActionExecutionAsync(
                context,
                () =>
                {
                    ran = true;
                    return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
                });

            return (ran, context.Result, http);
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
            CheckIn.Setup(c => c.ReissueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
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

        /// <summary>The door with the real check-in service on the same database, to see what the sessions hold after it.</summary>
        public DoorCheckInService Service(IGuestCheckInService checkIn) => new(Db, checkIn, NullLogger<DoorCheckInService>.Instance, Clock);

        /// <summary>The door that counts the failed attempts of each door for every client together.</summary>
        public DoorCheckInService Service(IDoorCheckInFailureTracker failures) =>
            new(Db, CheckIn.Object, NullLogger<DoorCheckInService>.Instance, Clock, failures);

        public GuestCheckInService RealCheckIn(ICheckInLinkDeliveryService? deliveries = null) =>
            new(Db, NullLogger<GuestCheckInService>.Instance, timeProvider: Clock, linkDeliveries: deliveries);

        /// <summary>The flow of the identity verification, with fakes for everything but the state it reads from the session.</summary>
        public IdentityVerificationFlowService Flow() => new(
            Db,
            Mock.Of<IIdentityVerificationAgent>(),
            Mock.Of<IVerificationOperatorAlerts>(),
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            Clock,
            NullLogger<IdentityVerificationFlowService>.Instance);

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
