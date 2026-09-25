using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-06 (decision D5, A3-06, R-11) on real PostgreSQL through the HTTP pipeline: a "pay at the property" checkout is a
/// request, never a confirmed booking. The guest confirms the email, the host accepts (Confirmed) or declines (Cancelled,
/// dates free); without an answer the <c>checkout-hold-expiry</c> job cancels it. Concurrent answers give one outcome; a
/// pending request is not exported to the OTAs; a host of another org cannot answer; stays above
/// <c>DirectBooking:OnSiteMaxNights</c> are refused with 422.
/// </summary>
public class OnSiteRequestApprovalPostgresTests : IClassFixture<OnSiteRequestApprovalPostgresTests.OnSiteFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const string HostRole = "PropertyOwner";
    private const int MaxNights = 10;
    private const int ApprovalHours = 48;

    private readonly OnSiteFactory _factory;

    public OnSiteRequestApprovalPostgresTests(OnSiteFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task CreateDirectBooking_OnSite_StaysPendingUntilApprovedAndIsNotExportedToOtas()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        var exportToken = await SeedExportFeedAsync(property);
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsJsonAsync("/api/public/bookings", OnSitePayload(property.Id, NextYear(3, 1), NextYear(3, 4)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var bookingId = body.GetProperty("bookingId").GetGuid();
        Assert.Equal("OnSite", body.GetProperty("paymentOption").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("emailConfirmationExpiresAt").ValueKind);

        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.NotEqual(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.GuestEmailVerifiedAt);
        Assert.Equal(PaymentMethod.CashOnArrival, Assert.Single(stored.Payments).Method);

        // The dates are held on the booking site (no second request for them)...
        Assert.Contains(NextYear(3, 2).ToString("yyyy-MM-dd"), await BookedDatesAsync(property.Id, NextYear(3, 1)));
        var second = await guest.PostAsJsonAsync("/api/public/bookings", OnSitePayload(property.Id, NextYear(3, 2), NextYear(3, 3)));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("booking_dates_unavailable", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // ...but not sent to Airbnb/Booking through iCal before the host accepts.
        Assert.DoesNotContain($"UID:booking-{bookingId}", await IcsAsync(exportToken));

        // The guest is emailed the confirmation link; the host is told nothing before the email is confirmed.
        var email = Assert.Single(Emails(bookingId, "onsite-request-received"));
        Assert.Equal(stored.Guest.Email, email.To);
        Assert.Empty(Emails(bookingId, "onsite-request-to-host"));
    }

    [PostgresFact]
    public async Task ApproveOnSiteRequest_AfterGuestConfirmsEmail_BookingConfirmedExportedAndGuestEmailed()
    {
        var (hostId, property) = await SeedCheckoutReadyPropertyAsync();
        var exportToken = await SeedExportFeedAsync(property);
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(4, 1), NextYear(4, 5));

        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var requests = await host.GetFromJsonAsync<JsonElement>("/api/bookings/approval-requests");
        var listed = Assert.Single(requests.EnumerateArray(), r => r.GetProperty("id").GetGuid() == bookingId);
        Assert.Equal(4, listed.GetProperty("nights").GetInt32());
        Assert.Equal(JsonValueKind.String, listed.GetProperty("respondBy").ValueKind);
        var bookings = await host.GetFromJsonAsync<JsonElement>($"/api/bookings?propertyId={property.Id}");
        Assert.Equal(
            "AwaitingHostApproval",
            Assert.Single(bookings.EnumerateArray(), b => b.GetProperty("id").GetGuid() == bookingId)
                .GetProperty("onSiteRequestState").GetString());

        var response = await host.PostAsync($"/api/bookings/{bookingId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("onSiteRequestState").ValueKind);
        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.RequestExpiresAt);
        Assert.Contains($"UID:booking-{bookingId}", await IcsAsync(exportToken));
        // BK-10: the standard confirmation, "pay at the property"; the host who accepted gets no "new booking" email.
        var confirmation = Assert.Single(Emails(bookingId, "guest-booking-confirmed"));
        Assert.Equal(stored.Guest.Email, confirmation.To);
        Assert.Contains("l'host ha accettato la tua richiesta", confirmation.Content.HtmlBody);
        Assert.Contains("direttamente in struttura", confirmation.Content.HtmlBody);
        // BK-11: the readable booking code of "Le mie prenotazioni", not the booking id.
        Assert.Contains(
            $"Codice prenotazione: <strong>{BookingCodes.Format(stored.BookingCode)}</strong>",
            confirmation.Content.HtmlBody);
        Assert.Empty(Emails(bookingId, "host-booking-confirmed"));
        var afterwards = await host.GetFromJsonAsync<JsonElement>("/api/bookings/approval-requests");
        Assert.DoesNotContain(afterwards.EnumerateArray(), r => r.GetProperty("id").GetGuid() == bookingId);
    }

    [PostgresFact]
    public async Task DeclineOnSiteRequest_Host_CancelsWithReasonAndReleasesDates()
    {
        var (hostId, property) = await SeedCheckoutReadyPropertyAsync();
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(5, 1), NextYear(5, 4));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/decline", new { message = "Casa in manutenzione" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.OnSiteRequestDeclined, stored.CancellationReason);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(stored.Payments).Status);
        Assert.DoesNotContain(NextYear(5, 2).ToString("yyyy-MM-dd"), await BookedDatesAsync(property.Id, NextYear(5, 1)));
        var email = Assert.Single(Emails(bookingId, "onsite-request-declined"));
        Assert.Contains("Casa in manutenzione", email.Content.HtmlBody);

        // The dates can be requested again at once.
        using var guest = _factory.CreateClient();
        var again = await guest.PostAsJsonAsync("/api/public/bookings", OnSitePayload(property.Id, NextYear(5, 1), NextYear(5, 4)));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [PostgresFact]
    public async Task ExpiryJob_HostDidNotAnswerInTime_CancelsRequestReleasesDatesAndEmailsGuest()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(6, 1), NextYear(6, 4));
        await UpdateBookingAsync(bookingId, b => b.RequestExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        // Reads free the dates as soon as the deadline has passed, before the job runs.
        Assert.DoesNotContain(NextYear(6, 2).ToString("yyyy-MM-dd"), await BookedDatesAsync(property.Id, NextYear(6, 1)));

        await RunExpiryJobAsync();

        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.OnSiteRequestExpired, stored.CancellationReason);
        Assert.Single(Emails(bookingId, "onsite-request-expired"));

        using var host = _factory.CreateAuthenticatedClient(await HostOfAsync(property), HostRole);
        var late = await host.PostAsync($"/api/bookings/{bookingId}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("onsite_request_not_pending", (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task ExpiryJob_GuestNeverConfirmedEmail_CancelsRequestWithoutReachingTheHost()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        var (bookingId, token) = await CreateRequestAsync(property, NextYear(6, 10), NextYear(6, 12));
        await UpdateBookingAsync(bookingId, b => b.RequestExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        await RunExpiryJobAsync();

        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
        Assert.Equal(BookingCancellationReason.OnSiteEmailNotConfirmed, stored.CancellationReason);
        Assert.Empty(Emails(bookingId, "onsite-request-expired"));

        using var guest = _factory.CreateClient();
        var late = await guest.PostAsJsonAsync($"/api/public/bookings/{bookingId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("onsite_request_expired", (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task ApproveAndDecline_SentConcurrently_OnlyOneOutcome()
    {
        var (hostId, property) = await SeedCheckoutReadyPropertyAsync();
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(7, 1), NextYear(7, 4));
        using var first = _factory.CreateAuthenticatedClient(hostId, HostRole);
        using var second = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var responses = await Task.WhenAll(
            first.PostAsync($"/api/bookings/{bookingId}/approve", null),
            second.PostAsJsonAsync($"/api/bookings/{bookingId}/decline", new { message = (string?)null }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("onsite_request_not_pending", (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var stored = await LoadBookingAsync(bookingId);
        var approveWon = responses[0].StatusCode == HttpStatusCode.OK;
        Assert.Equal(approveWon ? BookingStatus.Confirmed : BookingStatus.Cancelled, stored.Status);
        var outcomes = Emails(bookingId, "guest-booking-confirmed").Count + Emails(bookingId, "onsite-request-declined").Count;
        Assert.Equal(1, outcomes);
    }

    [PostgresFact]
    public async Task CreateDirectBooking_OnSiteAboveMaxNights_Returns422WithStableCodeAndStoresNothing()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        using var guest = _factory.CreateClient();
        guest.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT");

        var response = await guest.PostAsJsonAsync(
            "/api/public/bookings", OnSitePayload(property.Id, NextYear(8, 1), NextYear(8, 1).AddDays(MaxNights + 1)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("onsite_request_too_many_nights", problem.GetProperty("code").GetString());
        Assert.Contains($"{MaxNights} notti", problem.GetProperty("detail").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.AnyAsync(b => b.PropertyId == property.Id));

        // Exactly the limit is accepted.
        var atLimit = await guest.PostAsJsonAsync(
            "/api/public/bookings", OnSitePayload(property.Id, NextYear(8, 1), NextYear(8, 1).AddDays(MaxNights)));
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [PostgresFact]
    public async Task ApproveOnSiteRequest_HostOfAnotherOrg_Returns404AndRequestStaysPending()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(9, 1), NextYear(9, 3));
        var intruderId = $"auth0|bk06-intruder-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(intruderId);
        using var intruder = _factory.CreateAuthenticatedClient(intruderId, HostRole);

        var approve = await intruder.PostAsync($"/api/bookings/{bookingId}/approve", null);
        var decline = await intruder.PostAsJsonAsync($"/api/bookings/{bookingId}/decline", new { message = "no" });
        var list = await intruder.GetFromJsonAsync<JsonElement>("/api/bookings/approval-requests");

        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, decline.StatusCode);
        Assert.DoesNotContain(list.EnumerateArray(), r => r.GetProperty("id").GetGuid() == bookingId);
        var stored = await LoadBookingAsync(bookingId);
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Null(stored.CancellationReason);
    }

    [PostgresFact]
    public async Task ApproveOnSiteRequest_OtaBlockImportedMeanwhile_Returns409AndRequestStaysPending()
    {
        // The request was not exported to the OTAs: an OTA booking imported meanwhile wins, the host can only decline.
        var (hostId, property) = await SeedCheckoutReadyPropertyAsync();
        var bookingId = await CreateConfirmedRequestAsync(property, NextYear(10, 1), NextYear(10, 4));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                StartUtc = NextYear(10, 2),
                EndUtc = NextYear(10, 6),
                Summary = "Airbnb (Not available)",
                ExternalUid = $"airbnb-{Guid.NewGuid():N}",
                LastSyncedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var response = await host.PostAsync($"/api/bookings/{bookingId}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("onsite_request_dates_blocked", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(BookingStatus.Pending, (await LoadBookingAsync(bookingId)).Status);
    }

    [PostgresFact]
    public async Task ConfirmEmail_WrongTokenThenRightTokenTwice_404ThenIdempotentSuccessAndOneHostEmail()
    {
        var (_, property) = await SeedCheckoutReadyPropertyAsync();
        var (bookingId, token) = await CreateRequestAsync(property, NextYear(11, 1), NextYear(11, 3));
        using var guest = _factory.CreateClient();

        var wrong = await guest.PostAsJsonAsync($"/api/public/bookings/{bookingId}/confirm-email", new { token = "not-the-token" });
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal("onsite_request_link_invalid", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        for (var click = 0; click < 2; click++)
        {
            var response = await guest.PostAsJsonAsync($"/api/public/bookings/{bookingId}/confirm-email", new { token });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("AwaitingHostApproval", body.GetProperty("state").GetString());
            var respondBy = body.GetProperty("requestExpiresAt").GetDateTime();
            Assert.InRange(respondBy, DateTime.UtcNow.AddHours(ApprovalHours - 1), DateTime.UtcNow.AddHours(ApprovalHours + 1));
        }

        Assert.Single(Emails(bookingId, "onsite-request-to-host"));
    }

    [PostgresFact]
    public async Task CreateDirectBooking_ConnectNotReady_Returns409WithStableCodeAndItalianMessage()
    {
        // R-11: the guest reads why the site does not take bookings instead of a generic "checkout failed".
        var seeded = await _factory.SeedPropertyAsync($"auth0|bk06-noconnect-{Guid.NewGuid():N}");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
            property.CinCode = "IT058091C27G5FFZDZ";
            property.ComplianceStatus = PropertyComplianceStatus.Active;
            await db.SaveChangesAsync();
        }

        using var guest = _factory.CreateClient();
        guest.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT");
        var response = await guest.PostAsJsonAsync("/api/public/bookings", OnSitePayload(seeded.Id, NextYear(12, 1), NextYear(12, 3)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("direct_booking_payments_not_ready", problem.GetProperty("code").GetString());
        Assert.Contains("non accetta ancora prenotazioni online", problem.GetProperty("detail").GetString());
    }

    /// <summary>Checkout "pay at the property" + the guest's confirmation of the email: a request waiting for the host.</summary>
    private async Task<Guid> CreateConfirmedRequestAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        var (bookingId, token) = await CreateRequestAsync(property, checkIn, checkOut);
        using var guest = _factory.CreateClient();
        var response = await guest.PostAsJsonAsync($"/api/public/bookings/{bookingId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return bookingId;
    }

    /// <summary>Checkout "pay at the property"; returns the booking and the token of the confirmation link emailed to the guest.</summary>
    private async Task<(Guid BookingId, string Token)> CreateRequestAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        using var guest = _factory.CreateClient();
        var response = await guest.PostAsJsonAsync("/api/public/bookings", OnSitePayload(property.Id, checkIn, checkOut));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingId").GetGuid();

        var email = Assert.Single(Emails(bookingId, "onsite-request-received"));
        var match = Regex.Match(email.Content.HtmlBody, $@"/requests/{bookingId:D}/confirm\?token=([A-Za-z0-9_-]+)""");
        Assert.True(match.Success, "The confirmation email has no link to the request.");
        return (bookingId, match.Groups[1].Value);
    }

    /// <summary>
    /// Emails of <paramref name="template"/> about one booking: every test uses its own guest address and host address,
    /// so the recipient identifies the booking.
    /// </summary>
    private List<(string? To, EmailContent Content, string Template)> Emails(Guid bookingId, string template)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipients = db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { GuestEmail = b.Guest.Email, HostEmail = b.Org.ContactEmail })
            .Single();
        return _factory.Emails.Snapshot()
            .Where(e => e.Template == template && (e.To == recipients.GuestEmail || e.To == recipients.HostEmail))
            .ToList();
    }

    private async Task RunExpiryJobAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CheckoutHoldExpiryJob>().ExecuteAsync(CancellationToken.None);
    }

    private static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static object OnSitePayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Giulia",
            lastName = $"Bianchi{Guid.NewGuid():N}"[..20],
            email = $"giulia.{Guid.NewGuid():N}@example.com",
            phone = "+393339876543",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = nameof(PaymentOption.OnSite),
    };

    private async Task<List<string?>> BookedDatesAsync(Guid propertyId, DateTime from)
    {
        using var anonymous = _factory.CreateClient();
        var availability = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/public/bookings/property/{propertyId}/availability" +
            $"?startDate={from:yyyy-MM-dd}&endDate={from.AddDays(40):yyyy-MM-dd}");
        return availability.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()).ToList();
    }

    private async Task<string> IcsAsync(Guid exportToken)
    {
        using var anonymous = _factory.CreateClient();
        return await anonymous.GetStringAsync($"/api/public/ical/{exportToken}");
    }

    /// <summary>A host whose org has a connected account (Connect ready) and an active, bookable property.</summary>
    private async Task<(string HostId, Property Property)> SeedCheckoutReadyPropertyAsync()
    {
        var hostId = $"auth0|bk06-host-{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = $"acct_bk06_{Guid.NewGuid():N}";
        org.ConnectChargesEnabled = true;
        org.ContactEmail = $"host.{Guid.NewGuid():N}@example.com";
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.Name = $"Casa {Guid.NewGuid():N}";
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return (hostId, property);
    }

    private async Task<string> HostOfAsync(Property property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.Where(p => p.Id == property.Id).Select(p => p.OwnerId).SingleAsync();
    }

    private async Task<Guid> SeedExportFeedAsync(Property property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var export = new PropertyICalExport { PropertyId = property.Id, OrgId = property.OrgId, ExportToken = Guid.NewGuid() };
        db.PropertyICalExports.Add(export);
        await db.SaveChangesAsync();
        return export.ExportToken;
    }

    private async Task UpdateBookingAsync(Guid bookingId, Action<Booking> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        await db.SaveChangesAsync();
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking()
            .Include(b => b.Payments)
            .Include(b => b.Guest)
            .SingleAsync(b => b.Id == bookingId);
    }

    /// <summary>The integration host with a recording email queue and fixed "pay at the property" limits.</summary>
    public sealed class OnSiteFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:OnSiteMaxNights"] = MaxNights.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DirectBooking:OnSiteApprovalHours"] = ApprovalHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }
    }
}
