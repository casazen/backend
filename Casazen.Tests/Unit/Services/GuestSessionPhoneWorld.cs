using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The world of the proofs of the guest session opened with the last four digits of the phone (E40/F01/US03/T01, NF-143): an EF InMemory store, the
/// real lookup, session and token services, the controller of the guest area, the lock of the codes and its filter, the error middleware, and a log
/// sink for each of them. <see cref="OpenAsync"/> makes one call of <c>POST api/public/guest/session</c> from the lock on, as MVC and the pipeline run
/// it, and returns what the client reads. The flag <c>WhiteLabelHost</c> is on unless the proof says otherwise.
/// </summary>
internal sealed class GuestSessionPhoneWorld : IDisposable
{
    /// <summary>The phone of the guest as the checkout stores it: at most 20 characters, a valid <c>[Phone]</c>.</summary>
    public const string Phone = "+39 347 555 7364";

    public const string RightDigits = "7364";
    public const string WrongDigits = "4821";
    public const string ClientIp = "203.0.113.77";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceProvider _requestServices;

    public GuestSessionPhoneWorld(bool whiteLabelHost = true)
    {
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        Flags = whiteLabelHost ? new TestFeatureFlags(FeatureFlags.WhiteLabelHost) : new TestFeatureFlags();
        var configuration = new ConfigurationBuilder().Build();
        var lookup = new GuestBookingLookupService(
            Db,
            Mock.Of<IGuestCheckInService>(),
            Mock.Of<IGuestCheckInLinkEmailQueue>(),
            EmailTestHelpers.Links(),
            configuration,
            LookupLogs,
            Clock);
        Sessions = new GuestSessionService(lookup, new GuestSessionTokens(new EphemeralDataProtectionProvider(), Clock), configuration, Clock);
        Controller = new GuestAreaController(Sessions, Flags);
        Lockout = new GuestPhoneLookupLockout(Options.Create(new GuestPhoneLookupLockoutOptions()), Clock);
        Filter = new GuestPhoneLookupLockoutFilter(Lockout, Flags, FilterLogs);

        // The answers are localized from the resources of the product: the request needs the localizer.
        _requestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public TestFeatureFlags Flags { get; }

    public GuestSessionService Sessions { get; }

    public GuestAreaController Controller { get; }

    public GuestPhoneLookupLockout Lockout { get; }

    public GuestPhoneLookupLockoutFilter Filter { get; }

    public CapturingLogger<GuestBookingLookupService> LookupLogs { get; } = new();

    public CapturingLogger<GuestPhoneLookupLockoutFilter> FilterLogs { get; } = new();

    public CapturingLogger<ErrorHandlingMiddleware> ErrorLogs { get; } = new();

    /// <summary>Everything the three log sinks received: rendered messages, structured values and exceptions.</summary>
    public string AllLogs => string.Join(Environment.NewLine, LookupLogs.AllOutput, FilterLogs.AllOutput, ErrorLogs.AllOutput);

    /// <summary>The body of the phone proof, as the binder leaves it when the e-mail is left out.</summary>
    public static GuestSessionRequest PhoneBody(OrgEntity org, string bookingCode, string digits) =>
        new() { OrgSlug = org.Slug, BookingCode = bookingCode, PhoneLast4 = digits };

    /// <summary>The body of the e-mail proof.</summary>
    public static GuestSessionRequest EmailBody(OrgEntity org, string bookingCode, string email) =>
        new() { OrgSlug = org.Slug, BookingCode = bookingCode, Email = email };

    /// <summary>A code of the right form that is not <paramref name="code"/>: each character moved by one in the alphabet of the codes.</summary>
    public static string OtherCode(string code) =>
        string.Concat(code.Select(c => BookingCodes.Alphabet[(BookingCodes.Alphabet.IndexOf(c) + 1) % BookingCodes.Alphabet.Length]));

    /// <summary>A booking site: an active org and one of its houses.</summary>
    public async Task<(OrgEntity Org, Property House)> AddSiteAsync()
    {
        var org = new OrgEntity
        {
            Name = "Site Org",
            Slug = $"site-{Guid.NewGuid():N}",
            DisplayName = "Villa Prova",
            ContactEmail = "host@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var house = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "Casa Prova",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Guest session test",
            Address = "Via Prova 1",
            City = "Como",
            PostalCode = "22100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            IsActive = true,
        };
        Db.Orgs.Add(org);
        Db.Properties.Add(house);
        await Db.SaveChangesAsync();
        return (org, house);
    }

    /// <summary>A confirmed direct booking of the site whose guest has <paramref name="email"/> and <paramref name="phone"/> (empty: no phone).</summary>
    public async Task<Booking> AddBookingAsync(OrgEntity org, Property house, string email, string phone)
    {
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Guest",
            Email = email,
            PhoneNumber = phone,
        };
        var checkIn = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            PropertyId = house.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 350m,
            CleaningFee = 50m,
            TotalPrice = 350m,
            CreatedAt = checkIn.AddDays(-20),
            UpdatedAt = checkIn.AddDays(-20),
        };
        Db.AddRange(guest, booking);
        await Db.SaveChangesAsync();
        return booking;
    }

    /// <summary>
    /// One call of <c>POST api/public/guest/session</c> with <paramref name="body"/> (a body the model validation lets through), always from the same
    /// client IP: the lock of the codes, the action, and the error middleware that turns a refusal into the answer. Status, headers (sorted) and body
    /// as the client reads them, and whether the action (the lookup) ran at all.
    /// </summary>
    public async Task<SessionAnswer> OpenAsync(GuestSessionRequest body)
    {
        var http = new DefaultHttpContext { RequestServices = _requestServices, TraceIdentifier = "trace-guest-session" };
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/public/guest/session";
        http.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        http.Response.Body = new MemoryStream();
        var actionRan = false;

        var middleware = new ErrorHandlingMiddleware(
            async context =>
            {
                var action = new ActionContext(context, new RouteData(), new ActionDescriptor());
                var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?> { ["request"] = body }, Controller);
                ActionExecutedContext? executed = null;
                await Filter.OnActionExecutionAsync(
                    executing,
                    async () =>
                    {
                        actionRan = true;
                        executed = new ActionExecutedContext(action, [], Controller);
                        try
                        {
                            executed.Result = (await Controller.OpenSession(body, CancellationToken.None)).Result;
                        }
                        catch (Exception exception)
                        {
                            // As MVC does: the filters see the exception of the action, then it goes on to the error middleware.
                            executed.ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                        }

                        return executed;
                    });

                if (executing.Result is null)
                    executed?.ExceptionDispatchInfo?.Throw();

                await WriteAsync(context, executing.Result ?? executed!.Result!);
            },
            ErrorLogs);
        await middleware.InvokeAsync(http);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        var headers = http.Response.Headers
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => $"{header.Key}: {header.Value}")
            .ToList();
        return new SessionAnswer(http.Response.StatusCode, headers, text, actionRan);
    }

    public void Dispose()
    {
        Lockout.Dispose();
        _requestServices.Dispose();
        Db.Dispose();
    }

    /// <summary>What MVC writes for the object results of the action: the 200 of the session and the 429 of the lock.</summary>
    private static Task WriteAsync(HttpContext http, IActionResult result)
    {
        var answer = Assert.IsAssignableFrom<ObjectResult>(result);
        http.Response.StatusCode = answer.StatusCode ?? StatusCodes.Status200OK;
        var contentType = answer.ContentTypes.Count > 0 ? answer.ContentTypes[0] : "application/json";
        return http.Response.WriteAsJsonAsync(answer.Value, answer.Value?.GetType() ?? typeof(object), Json, contentType);
    }
}

/// <summary>What the client of the guest session reads: status, headers (<c>name: value</c>, sorted) and body; and whether the lookup ran at all.</summary>
internal sealed record SessionAnswer(int Status, IReadOnlyList<string> Headers, string Body, bool ActionRan)
{
    /// <summary>Status, headers and body together: two answers the client cannot tell apart have the same.</summary>
    public string Fingerprint => $"{Status}\n{string.Join("\n", Headers)}\n{Body}";
}
