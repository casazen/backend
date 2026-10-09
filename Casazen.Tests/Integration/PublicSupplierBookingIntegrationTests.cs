using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10 on the real pipeline, with the flag <c>SupplierShowcaseBooking</c> on and the clock on Monday 12 October 2026, 08:45 in
/// Rome: an anonymous customer picks a slot of a supplier's showcase, leaves its data, follows the link of the e-mail it receives,
/// and only then the supplier has a request — which it takes, and the customer is told. The hold is idempotent, the same slot is
/// never booked twice, every refusal has its code and its fields, the answers carry nothing personal and are never cached or
/// indexed, and neither do the logs. Runs on PostgreSQL in CI and on the in-memory fallback locally; the race of many customers
/// on one slot, the lock and the encryption at rest need PostgreSQL: <see cref="ShowcaseBookingPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingIntegrationTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    // ─── The whole journey ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Booking_FromTheSlotsToTheTakenRequest_EndToEnd()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();

        // 1. The customer picks one of the slots the showcase offers.
        var slots = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/slots?service={supplier.Service}");
        var start = slots.GetProperty("days").EnumerateArray()
            .SelectMany(day => day.GetProperty("slots").EnumerateArray())
            .Select(slot => slot.GetProperty("startUtc").GetDateTime())
            .First();

        // 2. It leaves its data: a hold, an e-mail to check the address, and the supplier hears nothing.
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertPrivateAnswer(created);
        var hold = await created.Content.ReadFromJsonAsync<JsonElement>();
        var holdId = hold.GetProperty("id").GetGuid();
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), hold.GetProperty("expiresAt").GetDateTime().ToUniversalTime());
        Assert.Equal(new[] { "expiresAt", "id" }, hold.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var verification = Assert.Single(factory.EmailsTo(email));
        Assert.Equal(EmailTemplates.Names.SupplierBookingVerification, verification.Template);
        var (linkHold, token) = PublicBookingTestData.LinkOf(verification);
        Assert.Equal(holdId, linkHold);
        Assert.Empty(factory.EmailsTo(supplier.ContactEmail));
        Assert.Equal(0, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());

        // 3. The slot is taken from now on, for anybody else.
        var second = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, start, PublicBookingTestData.NewEmail()));
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "supplier_slot_unavailable");

        // 4. It follows the link: now the supplier has a request, and both sides are told.
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        AssertPrivateAnswer(confirmed);
        var booking = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[0-9A-Z]{5}-[0-9A-Z]{5}$", booking.GetProperty("publicCode").GetString());
        Assert.Equal("Richiesto", booking.GetProperty("status").GetString());
        Assert.Equal(PublicBookingTestData.ServiceName, booking.GetProperty("serviceName").GetString());
        Assert.Equal(start, booking.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.False(booking.GetProperty("alreadyConfirmed").GetBoolean());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), booking.GetProperty("respondBy").GetDateTime().ToUniversalTime());
        Assert.Equal(Offset(start), booking.GetProperty("startLocal").GetDateTimeOffset().Offset);

        Assert.Contains(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);
        var toSupplier = Assert.Single(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);
        Assert.DoesNotContain("Rossi", toSupplier.Content.Subject + toSupplier.Content.HtmlBody);
        Assert.Contains(factory.Pushes.ToArray(), p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId));

        // 5. The supplier sees "Nome C." and the comune; the street waits for the take.
        var inbox = await supplier.InboxAsync(factory);
        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        var requestId = item.GetProperty("id").GetGuid();
        Assert.Equal("showcase", item.GetProperty("source").GetString());
        Assert.Equal("Mario R.", item.GetProperty("clientName").GetString());
        Assert.Equal(PublicBookingTestData.City, item.GetProperty("city").GetString());
        Assert.False(item.GetProperty("contactDisclosed").GetBoolean());
        var beforeTake = item.ToString();
        foreach (var secret in PublicBookingTestData.Secrets(email))
            Assert.DoesNotContain(secret, beforeTake);

        // 6. The supplier takes it: the customer is told, and the supplier now sees who and where.
        using (var asSupplier = supplier.Client(factory))
        {
            var taken = await asSupplier.PostAsync($"/api/service-requests/{requestId}/take", content: null);
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
            var detail = await asSupplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{requestId}");
            Assert.Equal("PresoInCarico", detail.GetProperty("status").GetString());
            Assert.Equal("Mario Rossi", detail.GetProperty("clientName").GetString());
            Assert.Equal(PublicBookingTestData.Address, detail.GetProperty("address").GetString());
            Assert.Equal(PublicBookingTestData.Floor, detail.GetProperty("floor").GetString());
            Assert.Equal(PublicBookingTestData.AccessNotes, detail.GetProperty("accessNotes").GetString());
            Assert.Equal(email, detail.GetProperty("hostContact").GetProperty("email").GetString());
            Assert.Equal("+393331234567", detail.GetProperty("hostContact").GetProperty("phone").GetString());
        }

        Assert.Contains(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingAccepted);

        // 7. A second click on the link answers the same, with the status as it is by then, and does nothing again.
        var emailsBefore = factory.EmailsTo(email).Count;
        var again = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replay.GetProperty("alreadyConfirmed").GetBoolean());
        Assert.Equal("PresoInCarico", replay.GetProperty("status").GetString());
        Assert.Equal(booking.GetProperty("publicCode").GetString(), replay.GetProperty("publicCode").GetString());
        Assert.Equal(emailsBefore, factory.EmailsTo(email).Count);
        Assert.Equal(1, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Booking_TheSameClientRequestId_IsTheSameHold_NoSecondEmail()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        var clientRequestId = Guid.NewGuid();
        using var client = factory.CreateClient();
        var body = PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email, clientRequestId);

        var first = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", body);
        var second = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Single(factory.EmailsTo(email));
    }

    [Fact]
    public async Task Booking_TwoCustomersForTheSameSlot_OneHoldsItTheOtherIs409()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var start = PublicBookingTestData.TuesdayAt9;

        var first = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, PublicBookingTestData.NewEmail()));
        var other = PublicBookingTestData.NewEmail();
        var second = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, other));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "supplier_slot_unavailable");
        Assert.Empty(factory.EmailsTo(other));
    }

    [Fact]
    public async Task Booking_AnotherSupplierHasItsOwnAgenda_TheSameSlotIsFree()
    {
        var mine = await PublicBookingTestData.SeedAsync(factory);
        var theirs = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var start = PublicBookingTestData.TuesdayAt9;

        var first = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{mine.Slug}/bookings", PublicBookingTestData.Body(mine.Service, start, PublicBookingTestData.NewEmail()));
        var second = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{theirs.Slug}/bookings", PublicBookingTestData.Body(theirs.Service, start, PublicBookingTestData.NewEmail()));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task Booking_AHoldLapses_TheSlotIsFreeAgain_AndTheLinkNoLongerWorks()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var start = PublicBookingTestData.TuesdayAt9;
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));

        using (factory.MoveClock(TimeSpan.FromMinutes(31)))
        {
            var expired = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
            await AssertProblemAsync(expired, HttpStatusCode.Conflict, "supplier_booking_link_expired");
            // The slot is free again for the next customer (the booking starts again).
            var next = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, start.AddDays(7), PublicBookingTestData.NewEmail()));
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        }

        Assert.Equal(0, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
    }

    // ─── What is refused ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValuesThatAreNotValid_Are422WithTheirFields_AndNothingIsHeldOrSent()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var body = PublicBookingTestData.Body(
            supplier.Service,
            PublicBookingTestData.TuesdayAt9,
            "non-un-indirizzo",
            postalCode: "12",
            fullName: " ");

        var response = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", body);

        var problem = await AssertProblemAsync(response, (HttpStatusCode)422, "supplier_booking_invalid");
        Assert.Equal(new[] { "postalCode", "fullName", "email" }, problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("non-un-indirizzo", await response.Content.ReadAsStringAsync());
        AssertPrivateAnswer(response);
    }

    [Fact]
    public async Task Create_NoConsent_OrAnOldNotice_Is422_WithItsOwnCode()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();

        var none = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail(), privacyAccepted: false));
        var old = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail(), noticeVersion: "2026-01-vecchia"));

        await AssertProblemAsync(none, (HttpStatusCode)422, "supplier_booking_consent_required");
        await AssertProblemAsync(old, (HttpStatusCode)422, "supplier_booking_consent_outdated");
    }

    [Fact]
    public async Task Create_AChoiceThatDoesNotFitTheService_IsTheSame422AsTheEstimate()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var body = PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail(), options: new[] { new { code = "non-esiste", quantity = 1 } });

        var response = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", body);

        var problem = await AssertProblemAsync(response, (HttpStatusCode)422, "supplier_quote_invalid");
        Assert.Equal(new[] { "options[0].code" }, problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Create_ASupplierThatTakesNoOnlineBookings_OrAComuneItDoesNotCover_Is422()
    {
        var offline = await PublicBookingTestData.SeedAsync(factory, onlineBooking: false);
        var online = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();

        var notTaken = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{offline.Slug}/bookings",
            PublicBookingTestData.Body(offline.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        var outside = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{online.Slug}/bookings",
            PublicBookingTestData.Body(online.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail(), city: "Napoli"));

        await AssertProblemAsync(notTaken, (HttpStatusCode)422, "supplier_booking_offline");
        await AssertProblemAsync(outside, (HttpStatusCode)422, "supplier_booking_outside_zone");
    }

    [Fact]
    public async Task Create_ASlotThePlannerDoesNotOffer_Is409()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();

        // Sunday: the supplier does not work. A time that is not on the grid of the slots. A time in the past.
        foreach (var start in new[]
                 {
                     PublicBookingTestData.TuesdayAt9.AddDays(5),
                     PublicBookingTestData.TuesdayAt9.AddMinutes(20),
                     PublicBookingTestData.TuesdayAt9.AddDays(-7),
                 })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, start, PublicBookingTestData.NewEmail()));
            await AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_slot_unavailable");
        }
    }

    [Fact]
    public async Task Create_ATimeNoSlotCanHave_IsTheSame409_TheEndsOfTheCalendarIncluded_NeverA500()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();

        // Whole minutes at the very ends of the calendar and far from today: anyone can send these dates, and the date arithmetic of
        // the planner would overflow on them. They are refused like any slot that is not free.
        foreach (var start in new[]
                 {
                     new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(9999, 12, 29, 7, 0, 0, DateTimeKind.Utc),
                     new DateTime(9999, 12, 31, 22, 30, 0, DateTimeKind.Utc),
                 })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, start, PublicBookingTestData.NewEmail()));
            await AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_slot_unavailable");
        }
    }

    [Fact]
    public async Task Create_AServiceThatIsNotPublished_OrIsAnotherSuppliers_IsTheSame404()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var other = await PublicBookingTestData.SeedAsync(factory);
        var draft = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Bozza", SupplierServiceListingStatus.Draft);
        using var client = factory.CreateClient();

        foreach (var service in new[] { "non-esiste", draft, other.Service })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "supplier_service_not_found");
        }
    }

    [Fact]
    public async Task UnknownPendingAndSuspendedSuppliers_AreTheSame404_ForBothEndpoints()
    {
        var pending = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Pending);
        var suspended = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Suspended);
        using var client = factory.CreateClient();
        var details = new List<string?>();

        foreach (var slug in new[] { "non-esiste", pending.Slug, suspended.Slug })
        {
            var create = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{slug}/bookings",
                PublicBookingTestData.Body("pulizia", PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
            var confirm = await client.PostAsJsonAsync($"/api/public/suppliers/{slug}/bookings/{Guid.NewGuid()}/confirm-email", new { token = "x" });

            foreach (var response in new[] { create, confirm })
            {
                var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
                details.Add(problem.GetProperty("detail").GetString());
            }
        }

        Assert.Single(details.Distinct());
    }

    [Fact]
    public async Task Create_TheRobotsTrap_IsAnsweredLikeABooking_AndNothingIsDone()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var body = PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email, website: "https://spam.example");

        var response = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, answer.GetProperty("id").GetGuid());
        Assert.Empty(factory.EmailsTo(email));
        Assert.Equal(0, await CountHoldsAsync(supplier.OrgId));
        // The slot is still free for a person.
        var person = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, person.StatusCode);
    }

    [Fact]
    public async Task Confirm_AWrongToken_AnUnknownBooking_AndAnotherSuppliersBooking_AreTheSame404()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var other = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email));
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        var details = new List<string?>();

        var attempts = new[]
        {
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token = "token-sbagliato-di-prova-0123456789" }),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { token }),
            await client.PostAsJsonAsync($"/api/public/suppliers/{other.Slug}/bookings/{holdId}/confirm-email", new { token }),
        };
        foreach (var response in attempts)
        {
            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, "supplier_booking_link_invalid");
            details.Add(problem.GetProperty("detail").GetString());
        }

        Assert.Single(details.Distinct());
        Assert.Equal(0, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Confirm_WithoutAToken_Is400()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();

        var empty = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { token = "" });
        var missing = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { });
        var notGuid = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/non-un-guid/confirm-email", new { token = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notGuid.StatusCode);
    }

    [Fact]
    public async Task Create_ABodyThatIsTooBig_IsRefusedBeforeItIsRead()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var huge = new StringContent($"{{\"fullName\":\"{new string('a', 200_000)}\"}}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"/api/public/suppliers/{supplier.Slug}/bookings", huge);

        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"{(int)response.StatusCode}");
    }

    // ─── What nobody may learn ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryAnswer_IsNotIndexableNotCacheableAndSetsNoCookie_AndEchoesNothingPersonal()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var responses = new List<HttpResponseMessage>
        {
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email)),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email, fullName: "A")),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body("non-esiste", PublicBookingTestData.TuesdayAt9, email)),
            await client.PostAsJsonAsync($"/api/public/suppliers/non-esiste/bookings", PublicBookingTestData.Body("x", PublicBookingTestData.TuesdayAt9, email)),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { token = "xyz" }),
        };

        foreach (var response in responses)
        {
            AssertPrivateAnswer(response);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var secret in PublicBookingTestData.Secrets(email))
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheLogs_CarryNoNameNoAddressNoEmailNoPhoneAndNoToken()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var start = PublicBookingTestData.TuesdayAt9.AddDays(1);
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email, fullName: "A"));
        await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token = "token-sbagliato-di-prova-0123456789" });
        await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });

        var logs = factory.LogText();

        Assert.NotEmpty(logs);
        foreach (var secret in PublicBookingTestData.Secrets(email).Append(token))
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
    }

    // ─── helpers ───

    private static void AssertPrivateAnswer(HttpResponseMessage response)
    {
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain("SupplierBooking", problem.GetProperty("detail").GetString()!);
        return problem;
    }

    private static TimeSpan Offset(DateTime utc) =>
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome").GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private async Task<int> CountHoldsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == orgId);
    }
}

/// <summary>
/// SP-10 with the flag <c>SupplierShowcaseBooking</c> <b>off</b> (the default of every environment): the two endpoints answer 404
/// like a route that does not exist, whatever the supplier published and whatever the request, and nothing is held, stored or sent.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingFlagOffIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task BothEndpoints_AnswerTheSame404AsARouteThatDoesNotExist_EvenForARealSupplier()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var service = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/public/suppliers/non-esiste/non-esiste/non-esiste/non-esiste");
        var responses = new[]
        {
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail())),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { token = "x" }),
            // A request that would be refused is a 404 too, never a 400 or a 422: nothing is read.
            await client.PostAsync($"/api/public/suppliers/{supplier.Slug}/bookings", new StringContent("{ not json", Encoding.UTF8, "application/json")),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email", new { }),
        };

        var missingDetail = (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_found", body.GetProperty("code").GetString());
            Assert.Equal(missingDetail, body.GetProperty("detail").GetString());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == supplier.OrgId));
    }
}

/// <summary>The showcase host with the per-IP limit of the booking at two requests per ten minutes.</summary>
public sealed class PublicBookingPerIpThrottledFactory : PublicBookingFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("RateLimiting:PublicSupplierBookingCreate:PermitLimit", "2"),
    ];
}

/// <summary>The showcase host with the per-address limit of the booking at two requests an hour.</summary>
public sealed class PublicBookingPerEmailThrottledFactory : PublicBookingFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("RateLimiting:SupplierBookingCreatePerEmail:PermitLimit", "2"),
    ];
}

/// <summary>SP-10: the three limits of the booking — per IP, per address and supplier, and the cap of unchecked bookings of an address — all answer the same 429.</summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingRateLimitIntegrationTests(
    PublicBookingPerIpThrottledFactory perIp,
    PublicBookingPerEmailThrottledFactory perEmail,
    PublicBookingFactory capped)
    : IClassFixture<PublicBookingPerIpThrottledFactory>, IClassFixture<PublicBookingPerEmailThrottledFactory>, IClassFixture<PublicBookingFactory>
{
    [Fact]
    public async Task PerIp_AThirdBookingInTheWindow_Is429WithRetryAfter_AnotherIpIsNot()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perIp);
        using var client = perIp.CreateClient();

        var first = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9, "203.0.113.61");
        var second = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1), "203.0.113.61");
        var limited = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(2), "203.0.113.61");
        var other = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(2), "203.0.113.62");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task PerIp_TheCheckOfTheEmail_IsNotEatenByTheLimitOfTheBooking()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perIp);
        using var client = perIp.CreateClient();
        for (var i = 0; i < 3; i++)
            await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(i), "203.0.113.63");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplier.Slug}/bookings/{Guid.NewGuid()}/confirm-email")
        {
            Content = JsonContent.Create(new { token = "x" }),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, "203.0.113.63");
        var check = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
    }

    [Fact]
    public async Task PerEmail_AThirdBookingOfOneAddressAtOneSupplier_Is429_FromAnyIp_AnotherAddressIsNot()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perEmail);
        using var client = perEmail.CreateClient();
        var email = PublicBookingTestData.NewEmail();

        var first = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9, "203.0.113.71", email);
        var second = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1), "203.0.113.72", email.ToUpperInvariant());
        var limited = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(2), "203.0.113.73", email);
        var otherAddress = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(2), "203.0.113.73", PublicBookingTestData.NewEmail());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        // The wait it names is the window of the limit, an hour: the cap of unchecked bookings answers the same (next test).
        Assert.Equal(TimeSpan.FromHours(1), limited.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.Created, otherAddress.StatusCode);
        // Nothing of the third attempt was held or sent.
        Assert.Equal(2, perEmail.EmailsTo(email, StringComparison.OrdinalIgnoreCase).Count);
    }

    [Fact]
    public async Task TheCapOfUncheckedBookings_AFourthBookingOfOneAddressWhileThreeWaitForTheCheck_IsTheSame429()
    {
        var supplier = await PublicBookingTestData.SeedAsync(capped);
        using var client = capped.CreateClient();
        var email = PublicBookingTestData.NewEmail();
        for (var day = 0; day < 3; day++)
        {
            var created = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(day), "203.0.113.81", email);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var fourth = await PostAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(3), "203.0.113.81", email);

        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(fourth);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal(3, capped.EmailsTo(email).Count);
        // The wait the answer names is the window of the limit per address, to the second — the same as that limit's own answer —
        // and not the time until the oldest of the three lapses, which would tell when someone booked with this address.
        Assert.Equal(TimeSpan.FromHours(1), fourth.Headers.RetryAfter?.Delta);
        Assert.Equal(60 * 60, problem.GetProperty("retryAfterSeconds").GetInt32());
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        BookableSupplier supplier,
        DateTime start,
        string ip,
        string? email = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplier.Slug}/bookings")
        {
            Content = JsonContent.Create(PublicBookingTestData.Body(supplier.Service, start, email ?? PublicBookingTestData.NewEmail())),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, ip);
        return client.SendAsync(request);
    }
}

/// <summary>
/// The showcase host that records what the booking e-mails and pushes (instead of queueing them on Hangfire) and the log lines
/// every logger writes, so a test can follow the link of an e-mail and prove that nothing personal is in a log.
/// </summary>
public class PublicBookingFactory : PublicShowcaseFactory
{
    private readonly ConcurrentQueue<(string? To, EmailContent Content, string Template)> _emails = new();
    private readonly ConcurrentQueue<string> _logs = new();

    public ConcurrentQueue<QueuedPush> Pushes { get; } = new();

    public IReadOnlyList<(string? To, EmailContent Content, string Template)> EmailsTo(string? address, StringComparison comparison = StringComparison.Ordinal) =>
        _emails.Where(e => string.Equals(e.To, address, comparison)).ToList();

    /// <summary>Everything written to a log since the host started, one line per entry.</summary>
    public string LogText() => string.Join('\n', _logs);

    /// <summary>Moves the clock of the host for the lifetime of the returned handle (the shared host is put back afterwards).</summary>
    public IDisposable MoveClock(TimeSpan by)
    {
        var original = Clock.GetUtcNow();
        Clock.Advance(by);
        return new ClockRestore(Clock, original);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingProvider(_logs)));
        builder.ConfigureTestServices(services =>
        {
            RemoveService<IEmailQueue>(services);
            services.AddSingleton<IEmailQueue>(new RecordingQueue(_emails));
            RemoveService<IPushNotificationService>(services);
            services.AddSingleton<IPushNotificationService>(new RecordingPush(Pushes));
        });
    }

    public sealed record QueuedPush(string DeliveryKey, PushAudience Audience, PushNotificationPayload Payload);

    private sealed class RecordingQueue(ConcurrentQueue<(string? To, EmailContent Content, string Template)> queue) : IEmailQueue
    {
        public bool Enqueue(string? to, EmailContent content, string template)
        {
            queue.Enqueue((to, content, template));
            return true;
        }
    }

    private sealed class RecordingPush(ConcurrentQueue<QueuedPush> queue) : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            queue.Enqueue(new QueuedPush(deliveryKey, audience, payload));
            return true;
        }
    }

    private sealed class ClockRestore(Casazen.Tests.Unit.FakeTimeProvider clock, DateTimeOffset original) : IDisposable
    {
        public void Dispose() => clock.SetUtcNow(original);
    }

    private sealed class CapturingProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, sink);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : string.Empty;
                sink.Enqueue($"{category} | {logLevel} | {formatter(state, exception)} | {values} | {exception}");
            }
        }
    }
}

/// <summary>A supplier of the booking tests: active, with a showcase, a published service, hours and online bookings on.</summary>
internal sealed record BookableSupplier(Guid OrgId, string Slug, string Service, string UserId, string ContactEmail);

/// <summary>Seeds and requests of the HTTP tests of the booking from a supplier's showcase (SP-10).</summary>
internal static class PublicBookingTestData
{
    public const string City = "Monza";
    public const string PostalCode = "20900";
    public const string Address = "Via Segretissima 7";
    public const string Floor = "Piano 3, interno 7";
    public const string AccessNotes = "Citofono Rossi, chiavi nella cassetta";
    public const string FullName = "Mario Rossi";
    public const string ServiceName = "Pulizia profonda";

    /// <summary>Tuesday 13 October 2026, 09:00 in Rome: the first slot of the supplier after the 24 hours of notice.</summary>
    public static readonly DateTime TuesdayAt9 = new(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc);

    public static string NewEmail() => $"cliente.{Guid.NewGuid():N}@example.com";

    /// <summary>The texts that must never be in an answer nor in a log: the customer's data (the e-mail given).</summary>
    public static string[] Secrets(string email) =>
        [FullName, "Rossi", email, "3331234567", "333 123", Address, "Segretissima", "Piano 3", "Citofono", "cassetta"];

    public static async Task<BookableSupplier> SeedAsync(CasazenWebApplicationFactory factory, bool onlineBooking = true)
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory, comuni: """["Monza"]""");
        var service = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, ServiceName);
        var userId = $"auth0|sp10-{Guid.NewGuid():N}";

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierSettings.Add(new SupplierSettings { OrgId = supplier.OrgId, OnlineBookingEnabled = onlineBooking });
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == supplier.OrgId);
        db.Users.Add(new User
        {
            Id = userId,
            Email = profile.Email,
            FirstName = "Mario",
            LastName = "Fornitore",
            Role = UserRole.Supplier,
            SupplierOrgId = supplier.OrgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return new BookableSupplier(supplier.OrgId, supplier.Slug, service, userId, profile.Email);
    }

    public static object Body(
        string service,
        DateTime startUtc,
        string email,
        Guid? clientRequestId = null,
        string city = City,
        string postalCode = PostalCode,
        string fullName = FullName,
        bool privacyAccepted = true,
        string noticeVersion = "2026-11-test",
        object? options = null,
        string? website = null) =>
        new
        {
            clientRequestId = clientRequestId ?? Guid.NewGuid(),
            service,
            startUtc,
            city,
            postalCode,
            address = Address,
            floor = Floor,
            accessNotes = AccessNotes,
            fullName,
            email,
            phone = "+39 333 123 4567",
            locale = "it",
            privacyAccepted,
            privacyNoticeVersion = noticeVersion,
            options,
            website,
        };

    /// <summary>The hold and the token of the link in a verification e-mail.</summary>
    public static (Guid HoldId, string Token) LinkOf((string? To, EmailContent Content, string Template) email)
    {
        var match = Regex.Match(email.Content.HtmlBody, "hold=([0-9a-fA-F-]{36})&amp;token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "the verification e-mail has no link with the hold and the token");
        return (Guid.Parse(match.Groups[1].Value), match.Groups[2].Value);
    }

    public static HttpClient Client(this BookableSupplier supplier, CasazenWebApplicationFactory factory) =>
        factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

    public static async Task<JsonElement> InboxAsync(this BookableSupplier supplier, CasazenWebApplicationFactory factory)
    {
        using var client = supplier.Client(factory);
        return await client.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all");
    }
}
