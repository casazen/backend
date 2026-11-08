using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// «Gestisci con AI» over HTTP, with the real cockpit, check-in link and check-out behind it: who may read the plan and run it, that the
/// assistant closes the departure of a stay that is over, asks the guest (the e-mail queued, the message the host sends given back, a second
/// run the same minute asking nobody), and never touches the stay of another org.
/// </summary>
public class ComplianceAutopilotIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";
    private const string Phone = "+39 333 123 4567";
    private readonly CasazenWebApplicationFactory _factory;

    public ComplianceAutopilotIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    /// <summary>Fixed clock (FD-06): 11:00 in Rome on 2 November 2026.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));

    private static readonly DateTime Today = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Plan_WithoutAToken_Is401()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/compliance/autopilot")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/compliance/autopilot/run", null)).StatusCode);
    }

    [Fact]
    public async Task Plan_ASupplierHasNoHostCockpit_Is403()
    {
        using var supplier = _factory.CreateAuthenticatedClient($"auth0|autopilot-{Guid.NewGuid():N}", "Supplier");

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/compliance/autopilot")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.PostAsync("/api/compliance/autopilot/run", null)).StatusCode);
    }

    [Fact]
    public async Task PlanThenRun_ClosesTheDepartureOfAStayThatIsOver_AndNothingElse()
    {
        var hostId = $"auth0|autopilot-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        var over = await SeedStayAsync(property, BookingStatus.CheckedIn, -9, -3, email: "not-an-email");
        var today = await SeedStayAsync(property, BookingStatus.CheckedIn, -2, 0, email: "not-an-email");
        await using var app = WithClock();
        using var host = CreateHostClient(app, hostId);

        var plan = await host.GetFromJsonAsync<JsonElement>("/api/compliance/autopilot");

        var closing = Item(plan, "Departure", over);
        Assert.Equal("Ready", closing.GetProperty("status").GetString());
        Assert.Equal("close_departure", closing.GetProperty("reason").GetString());
        Assert.Equal($"Departure:{over:N}", closing.GetProperty("key").GetString());
        var leftToTheHost = Item(plan, "Departure", today);
        Assert.Equal(("Host", "departure_today"), (leftToTheHost.GetProperty("status").GetString(), leftToTheHost.GetProperty("reason").GetString()));
        // The stay that ended still lacks guest fields: it is not a guest-data alert the home can open.
        Assert.DoesNotContain(
            plan.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("topic").GetString() == "GuestData" && i.GetProperty("bookingId").GetGuid() == over);

        var run = await host.PostAsJsonAsync("/api/compliance/autopilot/run", new { keys = new[] { $"Departure:{over:N}" } });

        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        var outcome = Assert.Single(result.GetProperty("outcomes").EnumerateArray());
        Assert.Equal(("Done", "departure_closed"), (outcome.GetProperty("status").GetString(), outcome.GetProperty("code").GetString()));
        Assert.Equal(over, outcome.GetProperty("bookingId").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(BookingStatus.CheckedOut, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == over)).Status);
        Assert.NotNull((await db.StayCheckouts.AsNoTracking().SingleAsync(c => c.BookingId == over)).CompletedAt);
        Assert.Equal(BookingStatus.CheckedIn, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == today)).Status);
    }

    [Fact]
    public async Task Run_AsksTheGuest_QueuesTheEmailAndGivesTheMessageBack_AndASecondRunAsksNobody()
    {
        var hostId = $"auth0|autopilot-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        var stay = await SeedStayAsync(property, BookingStatus.CheckedIn, -1, 2, email: "anna@example.com", phone: Phone);
        await using var app = WithClock(emailConfigured: true);
        using var host = CreateHostClient(app, hostId);
        _factory.BackgroundJobClientMock.Invocations.Clear();

        var plan = await host.GetFromJsonAsync<JsonElement>("/api/compliance/autopilot");
        var asking = Item(plan, "GuestData", stay);
        Assert.Equal(("Ready", "ask_guests"), (asking.GetProperty("status").GetString(), asking.GetProperty("reason").GetString()));
        Assert.Equal(new[] { "email", "chat" }, asking.GetProperty("channels").EnumerateArray().Select(c => c.GetString()!));

        var first = await (await host.PostAsync("/api/compliance/autopilot/run", null)).Content.ReadFromJsonAsync<JsonElement>();

        var outcome = Assert.Single(first.GetProperty("outcomes").EnumerateArray());
        Assert.Equal(("Done", "email_queued", "Queued"), (outcome.GetProperty("status").GetString(), outcome.GetProperty("code").GetString(), outcome.GetProperty("emailStatus").GetString()));
        var chat = outcome.GetProperty("chat");
        Assert.Equal("it", chat.GetProperty("language").GetString());
        Assert.StartsWith("https://wa.me/393331234567?text=", chat.GetProperty("whatsAppUrl").GetString(), StringComparison.Ordinal);
        Assert.Contains("https://casazen-app.vercel.app/checkin/", chat.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(first.GetProperty("aiUsed").GetBoolean());
        Assert.Equal("ai_not_configured", first.GetProperty("aiReason").GetString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.BookingId == stay);
            Assert.Equal(GuestCheckInLinkEmailStatus.Queued, session.LinkEmailStatus);
        }

        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(It.Is<Job>(job => job.Type == typeof(GuestCheckInLinkEmailJob)), It.IsAny<IState>()),
            Times.Once);

        // The same minute again: the guest was asked, nothing goes out twice.
        var second = await (await host.PostAsync("/api/compliance/autopilot/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(second.GetProperty("outcomes").EnumerateArray());
        using var again = _factory.Services.CreateScope();
        Assert.Equal(1, await again.ServiceProvider.GetRequiredService<AppDbContext>().GuestCheckInSessions.CountAsync(s => s.BookingId == stay));
    }

    [Fact]
    public async Task TheStayOfAnotherOrg_IsNotInThePlan_AndNeverTouched()
    {
        var ownerId = $"auth0|autopilot-{Guid.NewGuid():N}";
        var strangerId = $"auth0|autopilot-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        await _factory.SeedPropertyAsync(strangerId);
        var stay = await SeedStayAsync(property, BookingStatus.CheckedIn, -9, -3, email: "not-an-email");
        await using var app = WithClock();
        using var stranger = CreateHostClient(app, strangerId);

        var plan = await stranger.GetFromJsonAsync<JsonElement>("/api/compliance/autopilot");
        var run = await (await stranger.PostAsJsonAsync("/api/compliance/autopilot/run", new { keys = new[] { $"Departure:{stay:N}" } })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(plan.GetProperty("items").EnumerateArray(), i => i.GetProperty("bookingId").GetGuid() == stay);
        Assert.Empty(run.GetProperty("outcomes").EnumerateArray());
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(BookingStatus.CheckedIn, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.AsNoTracking().SingleAsync(b => b.Id == stay)).Status);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> WithClock(bool emailConfigured = false) => _factory.WithWebHostBuilder(builder =>
    {
        if (emailConfigured)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = EmailOptions.ResendProvider,
                ["Email:ApiKey"] = "re_test_autopilot",
                ["Email:FromAddress"] = "noreply@casazen.app",
            }));
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    });

    private static HttpClient CreateHostClient(WebApplicationFactory<Program> app, string hostId)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", hostId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", HostRole);
        return client;
    }

    private static JsonElement Item(JsonElement plan, string topic, Guid bookingId) =>
        plan.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("topic").GetString() == topic && i.GetProperty("bookingId").GetGuid() == bookingId);

    /// <summary>A stay with incomplete guest data (no stay guests), inserted directly like the other cockpit tests.</summary>
    private async Task<Guid> SeedStayAsync(
        Property property,
        BookingStatus status,
        int checkInOffset,
        int checkOutOffset,
        string email,
        string phone = "")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = email,
            PhoneNumber = phone,
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = Today.AddDays(checkInOffset),
            CheckOutDate = Today.AddDays(checkOutOffset),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }
}
