using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-11: the limit per e-mail address, generalized. It was the second limit of the guests' "Le mie prenotazioni" (BK-11); now one
/// mechanism serves them and the customers of the suppliers' showcases, and any body that carries an address
/// (<see cref="IPerEmailRateLimitedRequest"/>) can be counted. For the suppliers' customers the same address has one budget at each
/// supplier, and a 429 always names the whole window: it never tells when somebody last looked for a booking with that address.
/// </summary>
public class PerEmailRateLimiterTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();

    // ─── The limiters ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Guests_FivePerFifteenMinutes_ByDefault_TheSixthWaitsTheTimeThatIsLeft()
    {
        using var limiter = new GuestBookingEmailRateLimiter(Config());

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire(null, "anna@example.com", out _));
        var sixth = limiter.TryAcquire(null, "anna@example.com", out var retryAfter);

        Assert.False(sixth);
        Assert.InRange(retryAfter, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Equal("GuestBookingLookupPerEmail", limiter.PolicyName);
        Assert.Equal(TimeSpan.FromMinutes(15), limiter.Window);
        // The guests' limit never changed what its 429 names: the time that is left.
        Assert.False(limiter.NamesTheWholeWindow);
    }

    [Fact]
    public void Guests_TheAddressIsCountedOverTheWholeSite_NoMatterTheCaseOrTheSpaces()
    {
        using var limiter = new GuestBookingEmailRateLimiter(Config(("RateLimiting:GuestBookingLookupPerEmail:PermitLimit", "2")));

        Assert.True(limiter.TryAcquire(null, "anna@example.com", out _));
        Assert.True(limiter.TryAcquire(null, "  ANNA@Example.com ", out _));

        Assert.False(limiter.TryAcquire(null, "anna@example.com", out _));
        Assert.True(limiter.TryAcquire(null, "luca@example.com", out _));
    }

    [Fact]
    public void Suppliers_TenPerFifteenMinutes_ByDefault_AndTheWaitIsAlwaysTheWholeWindow()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config());

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));
        var eleventh = limiter.TryAcquire("vetrina-test", "mario@example.com", out _);

        Assert.False(eleventh);
        Assert.Equal("SupplierBookingManagePerEmail", limiter.PolicyName);
        Assert.Equal(TimeSpan.FromMinutes(15), limiter.Window);
        Assert.True(limiter.NamesTheWholeWindow);
    }

    [Fact]
    public void Suppliers_TheSameAddressHasABudgetAtEachSupplier_AndTheSpellingsOfTheSlugShareOne()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "2")));

        Assert.True(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));
        Assert.True(limiter.TryAcquire("  VETRINA-test ", "MARIO@example.com", out _));
        Assert.False(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));

        // Another supplier, another address, and the guests' limit of the same address: three other budgets.
        Assert.True(limiter.TryAcquire("altra-vetrina", "mario@example.com", out _));
        Assert.True(limiter.TryAcquire("vetrina-test", "anna@example.com", out _));
        using var guests = new GuestBookingEmailRateLimiter(Config());
        Assert.True(guests.TryAcquire(null, "mario@example.com", out _));
    }

    [Fact]
    public void Suppliers_AScopeOfTheSupplier_IsNotTheSameAsNoScope()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "1")));

        Assert.True(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));

        Assert.True(limiter.TryAcquire(null, "mario@example.com", out _));
        Assert.False(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));
        Assert.False(limiter.TryAcquire(null, "mario@example.com", out _));
    }

    [Fact]
    public void Suppliers_TheLimitAndTheWindowAreConfiguration_AndANonPositiveValueFailsTheStartup()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(
            ("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "3"),
            ("RateLimiting:SupplierBookingManagePerEmail:WindowSeconds", "120")));

        Assert.Equal(TimeSpan.FromMinutes(2), limiter.Window);
        for (var i = 0; i < 3; i++)
            Assert.True(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));
        Assert.False(limiter.TryAcquire("vetrina-test", "mario@example.com", out _));
        Assert.Throws<InvalidOperationException>(
            () => new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "0"))));
    }

    [Fact]
    public void TryAcquire_ANullAddress_IsRefused()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config());

        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire("vetrina-test", null!, out _));
    }

    // ─── The filter ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_OverTheLimit_AnswersThe429OfTheProduct_WithTheWholeWindow_AndTheActionDoesNotRun()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "1")));
        var filter = new PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>(limiter, NullLogger<PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>>.Instance);
        var request = new SupplierBookingAccessRequest { Slug = "vetrina-test", Code = "7K2XM-9QD4T", Email = "mario@example.com" };

        var first = await RunAsync(filter, request);
        var second = await RunAsync(filter, request);

        Assert.True(first.ActionRan);
        Assert.Null(first.Result);
        Assert.False(second.ActionRan);
        var result = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.Equal("rate_limited", problem.Extensions["code"]);
        Assert.Equal(15 * 60, problem.Extensions["retryAfterSeconds"]);
        Assert.Equal("900", second.Http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Filter_ASecondSupplierOfTheSameAddress_IsNotLimitedByTheFirst()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "1")));
        var filter = new PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>(limiter, NullLogger<PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>>.Instance);

        await RunAsync(filter, new SupplierBookingAccessRequest { Slug = "vetrina-test", Code = "7K2XM-9QD4T", Email = "mario@example.com" });
        var other = await RunAsync(filter, new SupplierBookingAccessRequest { Slug = "altra-vetrina", Code = "7K2XM-9QD4T", Email = "mario@example.com" });

        Assert.True(other.ActionRan);
    }

    [Fact]
    public async Task Filter_TheBodyOfTheGuests_IsCountedToo_WithoutAScope_AndKeepsTheTimeThatIsLeft()
    {
        using var limiter = new GuestBookingEmailRateLimiter(Config(("RateLimiting:GuestBookingLookupPerEmail:PermitLimit", "1")));
        var filter = new PerEmailRateLimitFilter<GuestBookingEmailRateLimiter>(limiter, NullLogger<PerEmailRateLimitFilter<GuestBookingEmailRateLimiter>>.Instance);
        var request = new GuestBookingLookupRequest { OrgSlug = "villa", BookingCode = "7K2XM-9QD4T", Email = "anna@example.com" };

        await RunAsync(filter, request);
        var second = await RunAsync(filter, request);

        Assert.False(second.ActionRan);
        var wait = int.Parse(second.Http.Response.Headers.RetryAfter.ToString());
        Assert.InRange(wait, 1, 900);
    }

    [Fact]
    public async Task Filter_ABodyWithoutAnAddress_OrAnActionWithoutSuchABody_IsNotCounted()
    {
        using var limiter = new SupplierBookingManageEmailRateLimiter(Config(("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "1")));
        var filter = new PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>(limiter, NullLogger<PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>>.Instance);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await RunAsync(filter, new SupplierBookingAccessRequest { Slug = "vetrina-test", Code = "x", Email = "  " })).ActionRan);
            Assert.True((await RunAsync(filter, new object())).ActionRan);
        }
    }

    [Fact]
    public void Dtos_TheBodiesOfTheCustomersArea_CountTheAddressWithinTheSupplier()
    {
        IPerEmailRateLimitedRequest access = new SupplierBookingAccessRequest { Slug = "vetrina-test", Email = "mario@example.com" };
        IPerEmailRateLimitedRequest cancel = new SupplierBookingCancelRequest { Slug = "vetrina-test", Email = "mario@example.com", Reason = "x" };
        IPerEmailRateLimitedRequest reschedule = new SupplierBookingRescheduleRequest { Slug = "altra-vetrina", Email = "mario@example.com" };
        IPerEmailRateLimitedRequest guests = new GuestBookingLookupRequest { OrgSlug = "villa", Email = "anna@example.com" };

        Assert.Equal(("vetrina-test", "mario@example.com"), (access.RateLimitScope, access.Email));
        Assert.Equal("vetrina-test", cancel.RateLimitScope);
        Assert.Equal("altra-vetrina", reschedule.RateLimitScope);
        Assert.Null(guests.RateLimitScope);
        Assert.Equal("anna@example.com", guests.Email);
    }

    private sealed record FilterRun(bool ActionRan, IActionResult? Result, DefaultHttpContext Http);

    private static async Task<FilterRun> RunAsync<TLimiter>(PerEmailRateLimitFilter<TLimiter> filter, object body)
        where TLimiter : PerEmailRateLimiter
    {
        // The answer is localized from the resources of the product, so the context needs the localizer.
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

        return new FilterRun(ran, context.Result, http);
    }
}
