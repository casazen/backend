using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E40/F01/US03/T01 (NF-143, AC-NF143-3): the lock of a booking code after repeated failures of the last four digits of the phone. Five failed
/// attempts on one code within an hour lock it, even with the right digits and also when the code does not exist; the lock ends with the window;
/// a match clears the count; the key is a hash and the store is bounded. The chain is the real one of the endpoint (<see cref="GuestSessionPhoneWorld"/>).
/// </summary>
public class GuestPhoneLookupLockoutTests
{
    private const string Code = "7K2XM-9QD4T";

    /// <summary>
    /// Permission rule: the fifth failed attempt on a code within the window locks it. The next attempt answers 429 <c>rate_limited</c> with a
    /// <c>Retry-After</c> of the whole window (3600 seconds), even with the right digits, and the booking is not read. The e-mail proof is not locked.
    /// </summary>
    [Fact]
    public async Task FifthFailure_LocksCode_EvenWithRightDigits()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.WrongDigits));
            Assert.Equal(StatusCodes.Status404NotFound, failed.Status);
        }

        // Another spelling of the same code is the same code.
        var locked = await world.OpenAsync(
            GuestSessionPhoneWorld.PhoneBody(org, BookingCodes.Format(booking.BookingCode).ToLowerInvariant(), GuestSessionPhoneWorld.RightDigits));

        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.Status);
        Assert.False(locked.ActionRan);
        Assert.Contains("Retry-After: 3600", locked.Headers);
        var problem = JsonDocument.Parse(locked.Body).RootElement;
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal(3600, problem.GetProperty("retryAfterSeconds").GetInt32());

        // The e-mail proof keeps its own limits: the same code opens with the address of the booking.
        Assert.Equal(
            StatusCodes.Status200OK,
            (await world.OpenAsync(GuestSessionPhoneWorld.EmailBody(org, booking.BookingCode, "anna@example.com"))).Status);
    }

    /// <summary>Privacy rule: a code that does not exist counts and locks exactly as a real one, with the same 429 (status, headers, body).</summary>
    [Fact]
    public async Task UnknownCode_LocksLikeAKnownOne()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);
        var madeUp = GuestSessionPhoneWorld.OtherCode(booking.BookingCode);

        foreach (var code in new[] { booking.BookingCode, madeUp })
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var failed = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, code, GuestSessionPhoneWorld.WrongDigits));
                Assert.Equal(StatusCodes.Status404NotFound, failed.Status);
            }
        }

        var real = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.RightDigits));
        var fake = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, madeUp, GuestSessionPhoneWorld.RightDigits));

        Assert.Equal(StatusCodes.Status429TooManyRequests, real.Status);
        Assert.Equal(real.Fingerprint, fake.Fingerprint);
        Assert.False(real.ActionRan || fake.ActionRan);
    }

    /// <summary>
    /// Data rule: the key of a code is the SHA-256 of its stored form, whatever the spelling, never the code or the digits; and the store is bounded.
    /// A flood of made-up codes never makes it follow more codes than its bound, and a code it cannot follow is refused, never let through uncounted.
    /// </summary>
    [Fact]
    public void Key_IsHashOfNormalizedCode_StoreIsBounded()
    {
        var key = GuestPhoneLookupLockout.KeyOf(Code);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("7K2XM9QD4T"))), key);
        Assert.Equal(key, GuestPhoneLookupLockout.KeyOf(" 7k2xm 9qd4t "));
        Assert.Equal(key, GuestPhoneLookupLockout.KeyOf("7K2XM9QD4T"));
        Assert.Matches("^[0-9A-F]{64}$", key);

        // Something that is not a code is counted as typed (trimmed, upper case), still as a hash.
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("NOT-A-CODE"))), GuestPhoneLookupLockout.KeyOf(" not-a-code "));

        using var lockout = Lockout(new GuestPhoneLookupLockoutOptions { MaxTrackedCodes = 3 });
        for (var i = 0; i < 3; i++)
            Assert.True(lockout.TryAcquire(BookingCodes.New()));
        Assert.False(lockout.TryAcquire(BookingCodes.New()));

        for (var i = 0; i < 500; i++)
        {
            lockout.TryAcquire(BookingCodes.New());
            Assert.InRange(lockout.TrackedCodes, 0, 3);
        }
    }

    /// <summary>
    /// Permission rule: a match clears the count, through the filter as the endpoint runs it (not only the <c>Reset</c> of the store). Four failed
    /// attempts, then the right digits (200, and the code is no longer followed), then five more failed attempts are all answered 404 and only the
    /// sixth, even with the right digits, is locked. A failure never clears the count: the fifth failure locks (see the first test of this class).
    /// </summary>
    [Fact]
    public async Task Match_ClearsTheCount_ThroughTheFilter()
    {
        using var world = new GuestSessionPhoneWorld();
        var (org, house) = await world.AddSiteAsync();
        var booking = await world.AddBookingAsync(org, house, "anna@example.com", GuestSessionPhoneWorld.Phone);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var failed = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.WrongDigits));
            Assert.Equal(StatusCodes.Status404NotFound, failed.Status);
        }

        Assert.Equal(1, world.Lockout.TrackedCodes);
        var opened = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.RightDigits));
        Assert.Equal(StatusCodes.Status200OK, opened.Status);
        Assert.Equal(0, world.Lockout.TrackedCodes);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.WrongDigits));
            Assert.Equal(StatusCodes.Status404NotFound, failed.Status);
        }

        var locked = await world.OpenAsync(GuestSessionPhoneWorld.PhoneBody(org, booking.BookingCode, GuestSessionPhoneWorld.RightDigits));
        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.Status);
        Assert.False(locked.ActionRan);
    }

    /// <summary>The lock lasts until the window of the code ends (60 minutes from its first attempt), then the code starts again; a match clears the count.</summary>
    [Fact]
    public void LockEndsWithTheWindow_AndAMatchClearsTheCount()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        using var lockout = Lockout(new GuestPhoneLookupLockoutOptions(), clock);

        // Four failures, then the fifth attempt matches: the count goes back to zero.
        for (var i = 0; i < 5; i++)
            Assert.True(lockout.TryAcquire(Code));
        lockout.Reset(Code);

        for (var i = 0; i < 5; i++)
            Assert.True(lockout.TryAcquire(Code));
        Assert.False(lockout.TryAcquire(Code));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(lockout.TryAcquire(Code));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(lockout.TryAcquire(Code));
    }

    /// <summary>The two settings: 5 and 60 by default, read from <c>GuestArea:PhoneLockout:*</c>, and a value below 1 is refused.</summary>
    [Fact]
    public void Settings_FiveAndSixtyByDefault_ReadFromTheConfiguration_AtLeastOne()
    {
        var defaults = new GuestPhoneLookupLockoutOptions();
        GuestPhoneLookupLockoutOptions.Read(defaults, new ConfigurationBuilder().Build());
        using var byDefault = Lockout(defaults);
        Assert.Equal((5, TimeSpan.FromMinutes(60)), (byDefault.MaxFailures, byDefault.Window));

        var configured = new GuestPhoneLookupLockoutOptions();
        GuestPhoneLookupLockoutOptions.Read(
            configured,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GuestPhoneLookupLockoutOptions.MaxFailuresKey] = "3",
                [GuestPhoneLookupLockoutOptions.WindowMinutesKey] = "30",
            }).Build());
        using var custom = Lockout(configured);
        Assert.Equal((3, TimeSpan.FromMinutes(30)), (custom.MaxFailures, custom.Window));

        Assert.False(new GuestPhoneLookupLockoutOptions { MaxFailures = 0 }.IsValid());
        Assert.False(new GuestPhoneLookupLockoutOptions { WindowMinutes = 0 }.IsValid());
        Assert.Throws<InvalidOperationException>(() => Lockout(new GuestPhoneLookupLockoutOptions { MaxFailures = 0 }));
        Assert.Throws<InvalidOperationException>(() => Lockout(new GuestPhoneLookupLockoutOptions { WindowMinutes = -5 }));
    }

    /// <summary>
    /// The wiring of the endpoint (AC4): the session keeps its per-IP policy (10 per 5 minutes) for both proofs, takes the body of the session, and
    /// carries the limit per address, the limit per code and the lock, which runs after the other two.
    /// </summary>
    [Fact]
    public void Endpoint_KeepsThePerIpPolicy_AndRunsTheLockAfterTheOtherLimits()
    {
        var action = typeof(GuestAreaController).GetMethod(nameof(GuestAreaController.OpenSession))!;

        Assert.Equal(RateLimitPolicies.PublicGuestSession, action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        var policy = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.PublicGuestSession);
        Assert.Equal((10, TimeSpan.FromMinutes(5)), (policy.DefaultPermitLimit, policy.DefaultWindow));
        Assert.Equal(typeof(GuestSessionRequest), action.GetParameters()[0].ParameterType);
        Assert.NotNull(action.GetCustomAttribute<GuestBookingEmailRateLimitAttribute>());
        var perCode = action.GetCustomAttribute<GuestSessionCodeRateLimitAttribute>();
        var phoneLock = action.GetCustomAttribute<GuestPhoneLookupLockoutAttribute>();
        Assert.NotNull(perCode);
        Assert.NotNull(phoneLock);
        Assert.True(phoneLock.Order > perCode.Order);
    }

    /// <summary>
    /// The limit per code reads the code through <see cref="IGuestBookingCodeRequest"/>: the body of the session is counted whichever proof it
    /// carries, so the new body did not take the endpoint out of it.
    /// </summary>
    [Fact]
    public async Task CodeLimit_CountsTheSessionBody_WithEitherProof()
    {
        using var limiter = new GuestSessionCodeRateLimiter(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:GuestSessionPerCode:PermitLimit"] = "2",
            }).Build());
        var filter = new GuestSessionCodeRateLimitFilter(limiter, NullLogger<GuestSessionCodeRateLimitFilter>.Instance);

        Assert.True(await RanAsync(filter, new GuestSessionRequest { OrgSlug = "villa", BookingCode = Code, PhoneLast4 = "7364" }));
        Assert.True(await RanAsync(filter, new GuestSessionRequest { OrgSlug = "villa", BookingCode = "7k2xm9qd4t", Email = "anna@example.com" }));
        Assert.False(await RanAsync(filter, new GuestSessionRequest { OrgSlug = "villa", BookingCode = Code, PhoneLast4 = "7364" }));
    }

    private static GuestPhoneLookupLockout Lockout(GuestPhoneLookupLockoutOptions options, TimeProvider? clock = null) =>
        new(Options.Create(options), clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));

    /// <summary>Runs <paramref name="filter"/> on an action that takes <paramref name="body"/>: true when the action ran.</summary>
    private static async Task<bool> RanAsync(IAsyncActionFilter filter, object body)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider() };
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?> { ["request"] = body },
            controller: new object());
        var ran = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                ran = true;
                return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
            });

        return ran;
    }
}
