using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// The guest who has a CasaZen account (the mobile app): <c>GET api/guest/bookings</c>, <c>GET api/guest/bookings/{id}</c>,
/// <c>POST api/guest/bookings/{id}/session</c> and <c>DELETE api/guest/account</c>, and the <c>emailVerified</c> of <c>GET api/users/me</c>.
/// Over the whole host: the policy, the verified email, the tenant filter of a request whose caller has no org (or another one), the one 404,
/// nothing private in the answers, and the token of the session against the endpoints of the guest area. The deletion runs here with the
/// repository's delete done by tracked removes (EF InMemory has no <c>ExecuteDelete</c>); the real repository is proved on PostgreSQL by
/// <see cref="GuestAccountDeletionPostgresTests"/>.
/// </summary>
public class GuestAccountIntegrationTests(GuestAccountIntegrationTests.GuestAccountFactory factory)
    : IClassFixture<GuestAccountIntegrationTests.GuestAccountFactory>
{
    private const string BookingsPath = "/api/guest/bookings";
    private const string AccountPath = "/api/guest/account";

    // ─── GET /api/users/me ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Me_EmailVerified_IsTheClaimOfTheAccessToken(bool verified)
    {
        var email = NewEmail();
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: verified);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me");
        var updated = await (await client.PutAsJsonAsync("/api/users/me", new { firstName = "Anna" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(verified, me.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(verified, updated.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Me_WithoutTheClaimAndWithoutAuth0_EmailVerifiedIsFalse_NeverGuessedTrue()
    {
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: NewEmail());

        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me");

        Assert.False(me.GetProperty("emailVerified").GetBoolean());
    }

    // ─── Who may call ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/guest/bookings")]
    [InlineData("GET", "/api/guest/bookings/6f1c1f0e-5b1a-4c62-9d58-0d3c8d5a7a10")]
    [InlineData("POST", "/api/guest/bookings/6f1c1f0e-5b1a-4c62-9d58-0d3c8d5a7a10/session")]
    [InlineData("DELETE", "/api/guest/account")]
    public async Task EveryAction_WithoutASignedInAccount_Is401(string method, string path)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Bookings_AnEmailAuth0DoesNotSayIsVerified_Is403_OnTheListTheDetailAndTheSession(bool? verified)
    {
        var site = await SeedSiteAsync();
        var email = NewEmail();
        var stay = await SeedStayAsync(site, email, checkInInDays: 10);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: verified);

        var answers = new[]
        {
            await client.GetAsync(BookingsPath),
            await client.GetAsync($"{BookingsPath}/{stay.BookingId}"),
            await client.PostAsync($"{BookingsPath}/{stay.BookingId}/session", content: null),
        };

        foreach (var answer in answers)
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            var body = await answer.Content.ReadAsStringAsync();
            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("guest_email_not_verified", problem.GetProperty("code").GetString());
            // Localized, and it tells nothing about the bookings behind it.
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
            Assert.DoesNotContain(site.PropertyName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(stay.FormattedCode, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Bookings_TheRefusalIsLocalized_ItalianByDefaultAndEnglishOnRequest()
    {
        using var italian = factory.CreateAuthenticatedClient(NewUserId(), email: NewEmail(), emailVerified: false);
        using var english = factory.CreateAuthenticatedClient(NewUserId(), email: NewEmail(), emailVerified: false);
        english.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));

        var it = JsonDocument.Parse(await (await italian.GetAsync(BookingsPath)).Content.ReadAsStringAsync()).RootElement;
        var en = JsonDocument.Parse(await (await english.GetAsync(BookingsPath)).Content.ReadAsStringAsync()).RootElement;

        Assert.StartsWith("Conferma il tuo indirizzo email", it.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Confirm your email address", en.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("guest_email_not_verified", en.GetProperty("code").GetString());
    }

    // ─── The list ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_VerifiedAccount_OwnBookingsOfTwoSites_ByCheckIn_WithTheFieldsOfTheContract()
    {
        var email = NewEmail();
        var siteA = await SeedSiteAsync("Villa Alfa", photo: "https://cdn.example.com/alfa.jpg");
        var siteB = await SeedSiteAsync("Casa Beta");
        var later = await SeedStayAsync(siteA, email, checkInInDays: 25);
        var sooner = await SeedStayAsync(siteB, email, checkInInDays: 4, nights: 2);
        var someoneElse = await SeedStayAsync(siteA, NewEmail(), checkInInDays: 5);
        // The caller is also the owner of a host org of their own: their guest bookings are listed all the same.
        var userId = NewUserId();
        await factory.SeedOrgForOwnerAsync(userId);
        using var client = factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner", email: email.ToUpperInvariant(), emailVerified: true);

        var response = await client.GetAsync($"{BookingsPath}?scope=upcoming");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var root = (await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(
            ["items", "page", "pageSize", "total"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((1, 20, 2), (root.GetProperty("page").GetInt32(), root.GetProperty("pageSize").GetInt32(), root.GetProperty("total").GetInt32()));
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([sooner.BookingId, later.BookingId], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(items, i => i.GetProperty("id").GetGuid() == someoneElse.BookingId);

        var item = items[1];
        Assert.Equal(
            [
                "adults", "bookingCode", "checkInDate", "checkOutDate", "children", "city", "currency", "hostName", "id", "nights",
                "orgSlug", "paidAmount", "paymentOption", "photoUrl", "propertyId", "propertyName", "propertySlug", "refundedAmount",
                "status", "totalPrice",
            ],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(siteA.Slug, item.GetProperty("orgSlug").GetString());
        Assert.Equal(later.FormattedCode, item.GetProperty("bookingCode").GetString());
        Assert.Equal("Confirmed", item.GetProperty("status").GetString());
        Assert.Equal(siteA.PropertyId, item.GetProperty("propertyId").GetGuid());
        Assert.Equal(siteA.PropertySlug, item.GetProperty("propertySlug").GetString());
        Assert.Equal(siteA.PropertyName, item.GetProperty("propertyName").GetString());
        Assert.Equal("Como", item.GetProperty("city").GetString());
        Assert.Equal("https://cdn.example.com/alfa.jpg", item.GetProperty("photoUrl").GetString());
        Assert.Equal(later.CheckIn.ToString("yyyy-MM-dd"), item.GetProperty("checkInDate").GetString());
        Assert.Equal(later.CheckIn.AddDays(3).ToString("yyyy-MM-dd"), item.GetProperty("checkOutDate").GetString());
        Assert.Equal(3, item.GetProperty("nights").GetInt32());
        Assert.Equal((2, 0), (item.GetProperty("adults").GetInt32(), item.GetProperty("children").GetInt32()));
        Assert.Equal("EUR", item.GetProperty("currency").GetString());
        Assert.Equal((362m, 362m, 0m), (item.GetProperty("totalPrice").GetDecimal(), item.GetProperty("paidAmount").GetDecimal(), item.GetProperty("refundedAmount").GetDecimal()));
        Assert.Equal("Immediate", item.GetProperty("paymentOption").GetString());
        Assert.Equal("Villa Alfa", item.GetProperty("hostName").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("photoUrl").ValueKind);
        Assert.Equal(2, items[0].GetProperty("nights").GetInt32());
    }

    [Fact]
    public async Task List_PastScopePagingAndTheBadScope()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var recent = await SeedStayAsync(site, email, checkInInDays: -10, status: BookingStatus.CheckedOut);
        var old = await SeedStayAsync(site, email, checkInInDays: -90, status: BookingStatus.CheckedOut);
        var ahead = await SeedStayAsync(site, email, checkInInDays: 30);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);

        var past = await client.GetFromJsonAsync<JsonElement>($"{BookingsPath}?scope=PAST");
        var firstOfTwo = await client.GetFromJsonAsync<JsonElement>($"{BookingsPath}?scope=past&page=1&pageSize=1");
        var secondOfTwo = await client.GetFromJsonAsync<JsonElement>($"{BookingsPath}?scope=past&page=2&pageSize=1");
        var capped = await client.GetFromJsonAsync<JsonElement>($"{BookingsPath}?pageSize=999");
        var defaults = await client.GetFromJsonAsync<JsonElement>(BookingsPath);
        var bad = await client.GetAsync($"{BookingsPath}?scope=everything");

        Assert.Equal([recent.BookingId, old.BookingId], past.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal("StayCompleted", past.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(2, past.GetProperty("total").GetInt32());
        Assert.Equal([recent.BookingId], firstOfTwo.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([old.BookingId], secondOfTwo.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal((2, 1, 2), (secondOfTwo.GetProperty("page").GetInt32(), secondOfTwo.GetProperty("pageSize").GetInt32(), secondOfTwo.GetProperty("total").GetInt32()));
        Assert.Equal(50, capped.GetProperty("pageSize").GetInt32());
        // No scope: the upcoming ones.
        Assert.Equal([ahead.BookingId], defaults.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("validation_error", JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_AccountWithNoBookings_IsAnEmptyPageNotAnError()
    {
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: NewEmail(), emailVerified: true);

        var page = await client.GetFromJsonAsync<JsonElement>(BookingsPath);

        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.GetProperty("total").GetInt32());
    }

    // ─── One booking, and the one 404 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_OwnBooking_IsTheBodyOfTheAnonymousLookupPlusTheIdAndTheSlug()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var stay = await SeedStayAsync(site, email, checkInInDays: 40);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);
        using var anonymous = factory.CreateClient();

        var detail = await client.GetAsync($"{BookingsPath}/{stay.BookingId}");
        var lookup = await anonymous.PostAsJsonAsync(
            "/api/public/bookings/lookup",
            new { orgSlug = site.Slug, bookingCode = stay.FormattedCode, email });

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>();
        var lookupBody = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        // Every field of the anonymous body, with the same value (the same projection), and two more.
        foreach (var field in lookupBody.EnumerateObject())
            Assert.True(JsonEquals(field.Value, detailBody.GetProperty(field.Name)), $"{field.Name} differs");
        Assert.Equal(
            lookupBody.EnumerateObject().Select(p => p.Name).Append("id").Append("orgSlug").Order(StringComparer.Ordinal),
            detailBody.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(stay.BookingId, detailBody.GetProperty("id").GetGuid());
        Assert.Equal(site.Slug, detailBody.GetProperty("orgSlug").GetString());
        Assert.Equal(site.PropertyId, detailBody.GetProperty("propertyId").GetGuid());
        Assert.Equal(site.PropertySlug, detailBody.GetProperty("propertySlug").GetString());
        Assert.Equal("NotYetOpen", detailBody.GetProperty("checkIn").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Detail_AndSession_ABookingOfAnotherPersonAnUnknownIdAndAStringThatIsNotAnId_TheSame404()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var otherOrg = await SeedSiteAsync();
        var own = await SeedStayAsync(site, email, checkInInDays: 10);
        var someoneElsesInTheSameSite = await SeedStayAsync(site, NewEmail(), checkInInDays: 10);
        var anotherSite = await SeedStayAsync(otherOrg, NewEmail(), checkInInDays: 10);
        var notDirect = await SeedStayAsync(site, email, checkInInDays: 10, source: BookingSource.Manual);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);

        var ids = new[]
        {
            someoneElsesInTheSameSite.BookingId.ToString(),
            anotherSite.BookingId.ToString(),
            notDirect.BookingId.ToString(),
            Guid.NewGuid().ToString(),
            "not-a-guid",
            own.FormattedCode,
        };
        var bodies = new List<string>();
        foreach (var id in ids)
        {
            foreach (var answer in new[]
                     {
                         await client.GetAsync($"{BookingsPath}/{id}"),
                         await client.PostAsync($"{BookingsPath}/{id}/session", content: null),
                     })
            {
                Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
                var text = await answer.Content.ReadAsStringAsync();
                var problem = JsonDocument.Parse(text).RootElement;
                Assert.Equal(GuestBookingLookupErrorCodes.NotFound, problem.GetProperty("code").GetString());
                Assert.DoesNotContain(site.PropertyName, text, StringComparison.Ordinal);
                Assert.DoesNotContain(otherOrg.PropertyName, text, StringComparison.Ordinal);
                bodies.Add(JsonSerializer.Serialize(new
                {
                    status = problem.GetProperty("status").GetInt32(),
                    code = problem.GetProperty("code").GetString(),
                    title = problem.GetProperty("title").GetString(),
                    detail = problem.GetProperty("detail").GetString(),
                }));
            }
        }

        // Identical answers (the trace id aside): nothing tells which bookings exist.
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        // The message speaks of the account's bookings, not of a code and an email.
        Assert.Contains("tra le tue", bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_AStayAtAHouseTheHostHasDeleted_IsStillReadable_AndTheListKeepsIt()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var stay = await SeedStayAsync(site, email, checkInInDays: -30, status: BookingStatus.CheckedOut);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var house = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == site.PropertyId);
            house.IsDeleted = true;
            house.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);
        var list = await client.GetFromJsonAsync<JsonElement>($"{BookingsPath}?scope=past");
        var detail = await client.GetAsync($"{BookingsPath}/{stay.BookingId}");

        Assert.Equal([stay.BookingId], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task AnInactiveOrg_NeverAppearsInTheListTheDetailOrTheSession()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync(active: false);
        var stay = await SeedStayAsync(site, email, checkInInDays: 10);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);

        var list = await client.GetFromJsonAsync<JsonElement>(BookingsPath);
        var detail = await client.GetAsync($"{BookingsPath}/{stay.BookingId}");
        var session = await client.PostAsync($"{BookingsPath}/{stay.BookingId}/session", content: null);

        Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);
    }

    // ─── Nothing private leaves ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingPrivateLeaves_ListDetailAndSession_EvenWithEveryPrivateFieldFilled()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var privateStay = await SeedStayAsync(
            site,
            email,
            checkInInDays: 12,
            shape: booking =>
            {
                booking.CancellationNote = "marker-cancellation-note";
                booking.PriceOverridden = true;
                booking.PriceOverrideNote = "marker-price-note";
                booking.SpecialRequests = "marker-special-requests";
                booking.ExternalId = "marker-external-id";
                booking.ChannelLabel = "marker-channel-label";
                booking.ICalFeedId = Guid.NewGuid();
                booking.StripeSetupIntentId = "seti_marker";
                booking.StripePaymentMethodId = "pm_marker";
                booking.StripeCustomerId = "cus_marker";
                booking.CheckoutTokenHash = "marker-checkout-token-hash";
                booking.GuestCancelTokenHash = "marker-cancel-token-hash";
                booking.GuestEmailVerificationTokenHash = "marker-verification-hash";
                booking.ExpectedArrivalTime = "marker-arrival";
            },
            guestShape: guest =>
            {
                guest.FirstName = "MarkerFirstName";
                guest.LastName = "MarkerLastName";
                guest.PhoneNumber = "+39marker-phone";
                guest.Address = "marker-address";
                guest.PostalCode = "marker-cap";
                guest.PlaceOfBirth = "marker-birthplace";
                guest.DocumentNumber = "marker-document-number";
                guest.DocumentScanUrl = "marker-document-scan";
                guest.Notes = "marker-guest-note";
            },
            stripePaymentIntentId: "pi_marker_secret");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StayGuests.Add(new StayGuest
            {
                BookingId = privateStay.BookingId,
                OrgId = site.OrgId,
                Position = 1,
                Type = StayGuestType.SingleGuest,
                FirstName = "MarkerCompanionFirst",
                LastName = "MarkerCompanionLast",
                DocumentNumber = "marker-companion-document",
            });
            await db.SaveChangesAsync();
        }

        // Another guest of the same site, with a booking and a house of their own, whom the caller must never see.
        var otherSite = await SeedSiteAsync("Casa Altrui MARKER");
        await SeedStayAsync(
            otherSite,
            NewEmail(),
            checkInInDays: 12,
            guestShape: guest => guest.FirstName = "MarkerOtherGuest");
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);

        var bodies = new List<string>
        {
            await client.GetStringAsync($"{BookingsPath}?scope=upcoming"),
            await client.GetStringAsync($"{BookingsPath}?scope=past"),
            await client.GetStringAsync($"{BookingsPath}/{privateStay.BookingId}"),
            await (await client.PostAsync($"{BookingsPath}/{privateStay.BookingId}/session", content: null)).Content.ReadAsStringAsync(),
        };

        Assert.Contains(privateStay.FormattedCode, bodies[0], StringComparison.Ordinal);
        foreach (var body in bodies)
        {
            // Every marker planted above, the payment intent included, and the other guest's.
            Assert.DoesNotContain("marker", body, StringComparison.OrdinalIgnoreCase);
            // Nor the identifiers that are not the account's to hold: the guest record, the org, and the account's own address.
            Assert.DoesNotContain(privateStay.GuestId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(site.OrgId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─── The session ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_TheTokenOpensTheGuestAreaOfThatBookingAndOfNoOther()
    {
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var own = await SeedStayAsync(site, email, checkInInDays: 20);
        var second = await SeedStayAsync(site, email, checkInInDays: 50);
        var someoneElses = await SeedStayAsync(site, NewEmail(), checkInInDays: 20);
        var otherSite = await SeedSiteAsync();
        var sameCodeOtherSite = await SeedStayAsync(otherSite, email, checkInInDays: 20, code: own.Code);
        using var client = factory.CreateAuthenticatedClient(NewUserId(), email: email, emailVerified: true);

        var opened = await client.PostAsync($"{BookingsPath}/{own.BookingId}/session", content: null);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var session = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["accessToken", "bookingCode", "expiresAt", "orgSlug"],
            session.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var token = session.GetProperty("accessToken").GetString()!;
        Assert.StartsWith("gs1.", token, StringComparison.Ordinal);
        Assert.Equal(own.FormattedCode, session.GetProperty("bookingCode").GetString());
        Assert.Equal(site.Slug, session.GetProperty("orgSlug").GetString());
        Assert.InRange(session.GetProperty("expiresAt").GetDateTime(), DateTime.UtcNow.AddMinutes(100), DateTime.UtcNow.AddHours(24));
        Assert.DoesNotContain(email, token, StringComparison.OrdinalIgnoreCase);

        // The cancellation preview of the guest area (DB-08a), with the token and no code or email typed.
        var preview = await GetWithGuestTokenAsync($"/api/public/guest/bookings/{session.GetProperty("bookingCode").GetString()}/cancellation", token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(previewBody.GetProperty("cancellable").GetBoolean());
        Assert.Equal(site.PropertyName, previewBody.GetProperty("stay").GetProperty("propertyName").GetString());

        // The booking itself, as the guest area shows it, and the other bookings of that site and that email (the token is the guest's).
        var area = await GetWithGuestTokenAsync($"/api/public/guest/bookings/{own.FormattedCode}", token);
        var areaOfSecond = await GetWithGuestTokenAsync($"/api/public/guest/bookings/{second.FormattedCode}", token);
        Assert.Equal(HttpStatusCode.OK, area.StatusCode);
        Assert.Equal(HttpStatusCode.OK, areaOfSecond.StatusCode);
        Assert.Equal(own.FormattedCode, (await area.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingCode").GetString());

        // The token is tied to the site: the same code on another site (same email) is that site's booking, which this token cannot read
        // (the guest area finds the booking by the org of the session).
        Assert.Equal(own.Code, sameCodeOtherSite.Code);
        var sameCode = await GetWithGuestTokenAsync($"/api/public/guest/bookings/{sameCodeOtherSite.FormattedCode}", token);
        Assert.Equal(HttpStatusCode.OK, sameCode.StatusCode);
        Assert.Equal(site.PropertyName, (await sameCode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("propertyName").GetString());
        Assert.NotEqual(otherSite.PropertyName, site.PropertyName);

        // Another guest of the site: the same 404 as everywhere in the guest area.
        var someoneElsesAnswer = await GetWithGuestTokenAsync($"/api/public/guest/bookings/{someoneElses.FormattedCode}", token);
        Assert.Equal(HttpStatusCode.NotFound, someoneElsesAnswer.StatusCode);
        Assert.Equal(
            GuestBookingLookupErrorCodes.NotFound,
            (await someoneElsesAnswer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ─── DELETE /api/guest/account ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_GuestAccount_204_Auth0FirstThenTheRow_BookingsAndGuestsUntouched_AndASecondCallIs401()
    {
        var userId = NewUserId();
        var email = NewEmail();
        var site = await SeedSiteAsync();
        var stay = await SeedStayAsync(site, email, checkInInDays: 15);
        await SeedUserAsync(userId, email);
        var rowWasThereWhenAuth0WasCalled = false;
        factory.Auth0
            .Setup(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()))
            .Callback(() => rowWasThereWhenAuth0WasCalled = UserExists(userId))
            .ReturnsAsync(Auth0SyncResult.Synced);
        using var client = factory.CreateAuthenticatedClient(userId, email: email, emailVerified: true);

        var response = await client.DeleteAsync(AccountPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(rowWasThereWhenAuth0WasCalled, "Auth0 is called while the CasaZen account is still there");
        factory.Auth0.Verify(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(UserExists(userId));
        // The hosts' books are not the account's: the booking and its guest record are exactly as they were.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.IgnoreQueryFilters().Include(b => b.Guest).Include(b => b.Payments).SingleAsync(b => b.Id == stay.BookingId);
            Assert.Equal(email, booking.Guest.Email);
            Assert.Equal("Anna", booking.Guest.FirstName);
            Assert.False(booking.Guest.IsDeleted);
            Assert.Null(booking.Guest.DataAnonymizedDate);
            Assert.Equal(362m, Assert.Single(booking.Payments).Amount);
        }

        // The access token already issued still verifies, but the account is gone: 401, and Auth0 is not asked again.
        var second = await client.DeleteAsync(AccountPath);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        factory.Auth0.Verify(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NeedsNoVerifiedEmail()
    {
        var userId = NewUserId();
        await SeedUserAsync(userId, NewEmail());
        factory.Auth0.Setup(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(Auth0SyncResult.Synced);
        using var client = factory.CreateAuthenticatedClient(userId, email: NewEmail(), emailVerified: false);

        var response = await client.DeleteAsync(AccountPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(UserExists(userId));
    }

    [Theory]
    [InlineData(Auth0SyncResult.RateLimitedCode)]
    [InlineData(Auth0SyncResult.ApiErrorCode)]
    [InlineData(Auth0SyncResult.NotConfiguredCode)]
    public async Task Delete_Auth0Refuses_502WithItsCode_AndNothingIsRemoved(string errorCode)
    {
        var userId = NewUserId();
        await SeedUserAsync(userId, NewEmail());
        factory.Auth0
            .Setup(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(errorCode));
        using var client = factory.CreateAuthenticatedClient(userId, email: NewEmail(), emailVerified: true);

        var response = await client.DeleteAsync(AccountPath);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(errorCode, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.True(UserExists(userId));
    }

    [Fact]
    public async Task Delete_AHostOrg_ASupplierLinkOrAnAdministrator_Is409_BeforeAnyCallToAuth0()
    {
        var host = NewUserId();
        await factory.SeedOrgForOwnerAsync(host);

        var supplierUser = NewUserId();
        var supplierOrgId = await SeedSupplierOrgAsync();
        await SeedUserAsync(supplierUser, NewEmail(), supplierOrgId: supplierOrgId);

        var admin = NewUserId();
        await SeedUserAsync(admin, NewEmail(), role: UserRole.Admin);

        foreach (var userId in new[] { host, supplierUser, admin })
        {
            using var client = factory.CreateAuthenticatedClient(userId, email: NewEmail(), emailVerified: true);

            var response = await client.DeleteAsync(AccountPath);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("guest_account_is_professional", problem.GetProperty("code").GetString());
            Assert.True(UserExists(userId));
            factory.Auth0.Verify(a => a.DeleteUserAsync(userId, It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────────────

    private static string NewUserId() => $"auth0|guest-{Guid.NewGuid():N}";

    private static string NewEmail() => $"guest.{Guid.NewGuid():N}@example.com";

    private static bool JsonEquals(JsonElement left, JsonElement right) => left.GetRawText() == right.GetRawText();

    private bool UserExists(string userId)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.IgnoreQueryFilters().Any(u => u.Id == userId);
    }

    private async Task SeedUserAsync(string userId, string email, UserRole role = UserRole.None, Guid? supplierOrgId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            FirstName = "Anna",
            LastName = "Guest",
            Role = role,
            SupplierOrgId = supplierOrgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSupplierOrgAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Pulizie Prova",
            Slug = $"supplier-{Guid.NewGuid():N}",
            DisplayName = "Pulizie Prova",
            ContactEmail = "supplier@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<HttpResponseMessage> GetWithGuestTokenAsync(string path, string token)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private sealed record SiteSeed(Guid OrgId, string Slug, string DisplayName, Guid PropertyId, string PropertySlug, string PropertyName);

    private sealed record StaySeed(Guid BookingId, Guid GuestId, string Code, string FormattedCode, DateTime CheckIn);

    private async Task<SiteSeed> SeedSiteAsync(string displayName = "Villa Prova", bool active = true, string? photo = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = displayName,
            Slug = $"guest-account-{Guid.NewGuid():N}",
            DisplayName = displayName,
            ContactEmail = $"host.{Guid.NewGuid():N}@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = active,
        };
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = $"Casa {Guid.NewGuid():N}"[..20],
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Guest account",
            Address = $"Via Prova {Guid.NewGuid():N}",
            City = "Como",
            PostalCode = "22100",
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 50m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            PhotoUrls = photo is null ? [] : [photo],
        };
        db.AddRange(org, property);
        await db.SaveChangesAsync();
        return new SiteSeed(org.Id, org.Slug, displayName, property.Id, property.Slug!, property.Name);
    }

    /// <summary>A paid booking of the public checkout: 3 nights, 300 lodging + 50 cleaning + 12 tourist tax = 362.</summary>
    private async Task<StaySeed> SeedStayAsync(
        SiteSeed site,
        string guestEmail,
        int checkInInDays,
        int nights = 3,
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct,
        string? code = null,
        Action<Booking>? shape = null,
        Action<Guest>? guestShape = null,
        string? stripePaymentIntentId = null)
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(checkInInDays);
        var guest = new Guest
        {
            OrgId = site.OrgId,
            FirstName = "Anna",
            LastName = "Guest",
            Email = guestEmail,
            PhoneNumber = "+393331112222",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        guestShape?.Invoke(guest);
        var booking = new Booking
        {
            PropertyId = site.PropertyId,
            OrgId = site.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 362m,
            FreeRefundDeadline = checkIn.AddDays(-7),
        };
        if (code is not null)
            booking.BookingCode = code;
        shape?.Invoke(booking);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(guest, booking);
        db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = site.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = $"pi_guest_account_{Guid.NewGuid():N}",
            StripePaymentIntentId = stripePaymentIntentId ?? $"pi_guest_account_{Guid.NewGuid():N}",
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return new StaySeed(booking.Id, guest.Id, booking.BookingCode, BookingCodes.Format(booking.BookingCode), checkIn);
    }

    /// <summary>
    /// The integration host with Auth0 mocked (the deletion is its only call) and the delete of a user done by tracked removes, because EF
    /// InMemory has no <c>ExecuteDelete</c>; on PostgreSQL both are as they are in production except that single statement.
    /// </summary>
    public class GuestAccountFactory : CasazenWebApplicationFactory
    {
        public Mock<IAuth0ManagementService> Auth0 { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IAuth0ManagementService>(services);
                services.AddSingleton(Auth0.Object);

                RemoveAllOf<IUserRepository>(services);
                services.AddScoped<IUserRepository>(sp =>
                    new TrackedDeleteUserRepository(sp.GetRequiredService<AppDbContext>()));
            });
        }
    }

    /// <summary>The real repository, but the delete is two tracked removes instead of the <c>ExecuteDelete</c> of the devices.</summary>
    private sealed class TrackedDeleteUserRepository(AppDbContext db) : IUserRepository
    {
        private readonly UserRepository _inner = new(db);

        public Task<User?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);

        public Task<User?> GetByEmailAsync(string email) => _inner.GetByEmailAsync(email);

        public Task<User?> GetBySubAsync(string sub) => _inner.GetBySubAsync(sub);

        public Task<IEnumerable<User>> GetAllAsync() => _inner.GetAllAsync();

        public Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(string? search, string? role, bool? isActive, int page, int pageSize) =>
            _inner.GetPagedAsync(search, role, isActive, page, pageSize);

        public Task<User> AddAsync(User user) => _inner.AddAsync(user);

        public Task<(User User, bool Created)> AddIfAbsentAsync(User user) => _inner.AddIfAbsentAsync(user);

        public Task UpdateAsync(User user) => _inner.UpdateAsync(user);

        public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            db.DeviceRegistrations.RemoveRange(await db.DeviceRegistrations.Where(d => d.UserId == id).ToListAsync(cancellationToken));
            var user = await db.Users.FindAsync([id], cancellationToken);
            if (user is null)
                return;

            db.Users.Remove(user);
            await db.SaveChangesAsync(cancellationToken);
        }

        public Task<(UserActivationOutcome Outcome, User? User)> SetActiveAsync(
            string id,
            bool isActive,
            string actorId,
            CancellationToken cancellationToken = default) =>
            _inner.SetActiveAsync(id, isActive, actorId, cancellationToken);

        public Task<bool> HasOtherActiveAdminAsync(string excludingUserId, CancellationToken cancellationToken = default) =>
            _inner.HasOtherActiveAdminAsync(excludingUserId, cancellationToken);
    }
}
