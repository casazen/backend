using System.Collections.Concurrent;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// One stay for the unit tests of the guest portal: an org, a house, a confirmed stay of a portal (10-13 October 2026, the clock on 9 October) on the
/// in-memory database, the real link service and context factory, and a sink that keeps every log line any of them writes.
/// </summary>
internal sealed class GuestPortalTestWorld : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    public const string HouseAddress = "Via del Segreto 7, interno 3";

    public GuestPortalTestWorld()
    {
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"guest-portal-{Guid.NewGuid():N}").Options);
        Org = HostScopeScenario.NewOrg("Casa Vacanze Aurora", $"aurora-{Guid.NewGuid():N}"[..20]);
        Org.DefaultLanguage = "de";
        Org.ThemeColor = "#336699";
        Org.Tagline = "Il mare a due passi";
        Org.PublicPhone = "+390831000000";
        Property = HostScopeScenario.NewProperty(Org.Id, "auth0|titolare-aurora", "Trullo Aurora");
        Property.Address = HouseAddress;
        var guest = new Guest { OrgId = Org.Id, FirstName = "Anna", LastName = "Ospite", Email = $"{Guid.NewGuid():N}@example.com" };
        Booking = new Booking
        {
            OrgId = Org.Id,
            PropertyId = Property.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 10),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 3,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        Db.Orgs.Add(Org);
        Db.Properties.Add(Property);
        Db.Guests.Add(guest);
        Db.Bookings.Add(Booking);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    public AppDbContext Db { get; }

    public OrgEntity Org { get; }

    public Property Property { get; }

    public Booking Booking { get; }

    public GuestPortalLogSink Logs { get; } = new();

    public TimeProvider Clock { get; set; } = new FixedTimeProvider(Now);

    public PublicSiteLinks SiteLinks { get; } = new(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.casazen.example" }));

    public GuestPortalLinkService LinkService() => new(Db, SiteLinks, Clock, Logs.CreateLogger<GuestPortalLinkService>());

    public PortalContextFactory ContextFactory() => new(Db);

    public GuestPortalOverviewService OverviewService(
        IEnumerable<IGuestPortalSection>? sections = null,
        IEnumerable<IGuestPortalNextStep>? nextSteps = null,
        IEnumerable<IGuestPortalPolicy>? policies = null,
        IFeatureFlags? flags = null)
    {
        return new GuestPortalOverviewService(
            Db,
            sections ?? [new StayPortalSection()],
            nextSteps ?? [new StayOverviewNextStep()],
            policies ?? [new DefaultGuestPortalPolicy()],
            flags ?? new TestFeatureFlags(FeatureFlags.GuestPortal),
            Logs.CreateLogger<GuestPortalOverviewService>());
    }

    /// <summary>The portal context of the stay, as the token filter makes it from its link.</summary>
    public async Task<PortalContext> ContextAsync()
    {
        var link = (await LinkService().GetOrCreateAsync(Org.Id, Booking.Id, createdByUserId: null))!.Link;
        return (await ContextFactory().CreateAsync(link))!;
    }

    /// <summary>The token filter of the portal over this world, with its accessor.</summary>
    public (GuestPortalTokenFilter Filter, GuestPortalContextAccessor Accessor) TokenFilter()
    {
        var accessor = new GuestPortalContextAccessor();
        return (new GuestPortalTokenFilter(LinkService(), ContextFactory(), accessor, Logs.CreateLogger<GuestPortalTokenFilter>()), accessor);
    }

    /// <summary>
    /// Runs <paramref name="filter"/> for a request of the portal with <paramref name="token"/> in the route, as MVC does, with
    /// <paramref name="action"/> in place of the action: the result it sets (a short cut) or <c>null</c> when it let the action run, and the response
    /// the request ended with.
    /// </summary>
    public static async Task<(IActionResult? Result, HttpResponse Response, bool ActionRan)> RunAsync(
        GuestPortalTokenFilter filter,
        string token,
        Func<Task>? action = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            TraceIdentifier = "trace-guest-portal",
        };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = $"/api/public/guest-portal/{token}";
        var routeData = new RouteData();
        routeData.Values[GuestPortalTokenFilter.TokenRouteValue] = token;
        var context = new ResourceExecutingContext(
            new ActionContext(httpContext, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());

        var ran = false;
        await filter.OnResourceExecutionAsync(context, async () =>
        {
            ran = true;
            if (action is not null)
                await action();

            return new ResourceExecutedContext(context, new List<IFilterMetadata>());
        });

        return (context.Result, httpContext.Response, ran);
    }

    public void Dispose()
    {
        Db.Dispose();
        Logs.Dispose();
    }
}

/// <summary>Keeps every log line, with the values of its state, the scopes around it and its exception, whatever the category.</summary>
internal sealed class GuestPortalLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly LoggerExternalScopeProvider _scopes = new();
    private readonly LoggerFactory _factory;

    public GuestPortalLogSink()
    {
        _factory = new LoggerFactory([this]);
    }

    /// <summary>Every line: the category, the message, every value of the state and of the scopes, the exception as text.</summary>
    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger<T> CreateLogger<T>() => new Logger<T>(_factory);

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, this);

    public void Dispose()
    {
    }

    private sealed class Sink(string category, GuestPortalLogSink owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return owner._scopes.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string> { category, logLevel.ToString(), formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                parts.AddRange(values.Select(v => $"{v.Key}={v.Value}"));

            owner._scopes.ForEachScope(
                (scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        list.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
                    else if (scope is not null)
                        list.Add(scope.ToString() ?? string.Empty);
                },
                parts);

            if (exception is not null)
                parts.Add(exception.ToString());

            owner._lines.Enqueue(string.Join(" | ", parts));
        }
    }
}
