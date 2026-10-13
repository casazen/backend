using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Xunit;
using Http = Casazen.Tests.Integration.SupplierBookingManageHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-11 on the real pipeline, with the flag <c>SupplierShowcaseBooking</c> on and the clock on Monday 12 October 2026, 08:45 in
/// Rome: the customer of a supplier's showcase, with no account, finds its booking again with the code and the e-mail address,
/// cancels it, moves it to another time and answers a time the supplier proposed. Everything that does not identify a booking is
/// the same 404 with the same body, the answers carry nothing of anybody else and nothing personal is logged, the supplier and the
/// customer are told. Runs on PostgreSQL in CI (the encrypted address is compared after it is decrypted) and on the in-memory
/// fallback locally; the races with the supplier need PostgreSQL: <see cref="ShowcaseBookingManagementPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingManagementIntegrationTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    private static readonly string[] Paths =
    [
        "lookup", "cancel", "reschedule", "proposal/accept", "proposal/reject",
    ];

    // ─── The whole journey ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Journey_FindTheBooking_TheSupplierTakesIt_TheCustomerCancels_TheSlotIsFreeAgain()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        // 1. It finds the booking again: the status, what it booked, the comune, what it can do. Not the street.
        var lookup = await Http.PostAsync(client, "lookup", booked.Access());
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Http.AssertPrivateAnswer(lookup);
        var view = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(booked.Code, view.GetProperty("publicCode").GetString());
        Assert.Equal("Richiesto", view.GetProperty("status").GetString());
        Assert.Equal(PublicBookingTestData.ServiceName, view.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal(supplier.Slug, view.GetProperty("supplier").GetProperty("slug").GetString());
        Assert.Equal(PublicBookingTestData.TuesdayAt9, view.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(2), view.GetProperty("endUtc").GetDateTime() - view.GetProperty("startUtc").GetDateTime());
        Assert.Equal(Http.Offset(PublicBookingTestData.TuesdayAt9), view.GetProperty("startLocal").GetDateTimeOffset().Offset);
        var place = view.GetProperty("place");
        Assert.Equal(PublicBookingTestData.City, place.GetProperty("city").GetString());
        Assert.Equal(PublicBookingTestData.PostalCode, place.GetProperty("postalCode").GetString());
        Assert.True(Http.IsNull(place, "address"));
        Assert.True(Http.IsNull(place, "floor"));
        Assert.True(Http.IsNull(place, "accessNotes"));
        Assert.Equal(6000, view.GetProperty("price").GetProperty("amountCents").GetInt32());
        Assert.Equal("estimate", view.GetProperty("price").GetProperty("basis").GetString());
        Assert.True(view.GetProperty("actions").GetProperty("canCancel").GetBoolean());
        Assert.True(view.GetProperty("actions").GetProperty("canReschedule").GetBoolean());
        Assert.False(view.GetProperty("actions").GetProperty("canRespondToProposal").GetBoolean());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), view.GetProperty("respondBy").GetDateTime().ToUniversalTime());
        Assert.True(Http.IsNull(view, "proposal"));
        Assert.True(Http.IsNull(view, "cancellation"));
        // Nothing of the customer's data comes back before the supplier took the request: not the name, not the address, not the phone.
        var body = view.ToString();
        foreach (var secret in PublicBookingTestData.Secrets(booked.Email))
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);

        // 2. The supplier takes it: now the customer reads the exact address, and the time can no longer be moved.
        using (var asSupplier = supplier.Client(factory))
        {
            var taken = await asSupplier.PostAsync($"/api/service-requests/{booked.RequestId}/take", content: null);
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        var afterTake = await (await Http.PostAsync(client, "lookup", booked.Access())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PresoInCarico", afterTake.GetProperty("status").GetString());
        Assert.Equal(PublicBookingTestData.Address, afterTake.GetProperty("place").GetProperty("address").GetString());
        Assert.Equal(PublicBookingTestData.Floor, afterTake.GetProperty("place").GetProperty("floor").GetString());
        Assert.Equal(PublicBookingTestData.AccessNotes, afterTake.GetProperty("place").GetProperty("accessNotes").GetString());
        Assert.False(afterTake.GetProperty("actions").GetProperty("canReschedule").GetBoolean());
        Assert.True(afterTake.GetProperty("actions").GetProperty("canCancel").GetBoolean());
        Assert.False(afterTake.ToString().Contains(booked.Email, StringComparison.OrdinalIgnoreCase));

        // 3. The slot is taken for anybody else.
        var taken2 = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        await Http.AssertProblemAsync(taken2, HttpStatusCode.Conflict, "supplier_slot_unavailable");

        // 4. The customer cancels, with its reason: the answer is the booking as it is now. Half an hour later the 24 hours of free
        // cancellation (Monday 09:00 in Rome) have passed: it is cancelled all the same, at no cost, and the supplier is told the notice
        // was short.
        var emailsBefore = factory.EmailsTo(booked.Email).Count;
        HttpResponseMessage cancelled;
        using (factory.MoveClock(TimeSpan.FromMinutes(30)))
        {
            var late = await Http.PostAsync(client, "lookup", booked.Access());
            Assert.False((await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancellationTerms").GetProperty("isFree").GetBoolean());
            cancelled = await Http.PostAsync(client, "cancel", booked.Access(new { reason = "Cambio programma" }));
        }

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Http.AssertPrivateAnswer(cancelled);
        var after = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Annullato", after.GetProperty("status").GetString());
        Assert.Equal("customer", after.GetProperty("cancellation").GetProperty("by").GetString());
        Assert.Equal("Cambio programma", after.GetProperty("cancellation").GetProperty("reason").GetString());
        Assert.False(after.GetProperty("actions").GetProperty("canCancel").GetBoolean());
        Assert.True(Http.IsNull(after.GetProperty("place"), "address"));

        // 5. Both are told: the supplier with "Nome C." and the comune, the customer with a receipt.
        var toSupplier = Assert.Single(
            factory.EmailsTo(supplier.ContactEmail),
            e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer);
        Assert.Contains("annullata dal cliente", toSupplier.Content.HtmlBody);
        Assert.Contains("con meno di <strong>24 h</strong> di preavviso", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain(PublicBookingTestData.Address, toSupplier.Content.HtmlBody);
        var receipt = Assert.Single(factory.EmailsTo(booked.Email), e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
        Assert.Contains(booked.Code, receipt.Content.HtmlBody);
        Assert.Equal(emailsBefore + 1, factory.EmailsTo(booked.Email).Count);
        Assert.Contains(
            factory.Pushes.ToArray(),
            p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId) && p.Payload.Type == PushTypes.ServiceRequestCancelled);

        // 6. The supplier sees who cancelled, and why.
        using (var asSupplier = supplier.Client(factory))
        {
            var detail = await asSupplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{booked.RequestId}");
            Assert.Equal("Annullato", detail.GetProperty("status").GetString());
            Assert.Equal("Customer", detail.GetProperty("cancelledBy").GetString());
            Assert.Equal("Cambio programma", detail.GetProperty("cancellationReason").GetString());
            Assert.Equal("Mario R.", detail.GetProperty("clientName").GetString());
        }

        // 7. A second cancellation answers the same and does nothing again.
        var again = await Http.PostAsync(client, "cancel", booked.Access(new { reason = "Un altro motivo" }));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Cambio programma", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancellation").GetProperty("reason").GetString());
        Assert.Equal(emailsBefore + 1, factory.EmailsTo(booked.Email).Count);

        // 8. The slot is free again and another customer books it.
        var next = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [Fact]
    public async Task Lookup_TheCodeIsReadTheWayPeopleWriteIt_AndTheAddressWithoutCaseOrSpaces()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var plain = booked.Code.Replace("-", string.Empty, StringComparison.Ordinal);

        foreach (var code in new[] { booked.Code, plain, plain.ToLowerInvariant(), $"  {booked.Code.ToLowerInvariant()} ", $"{plain[..5]} {plain[5..]}" })
        {
            var response = await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code, email = $"  {booked.Email.ToUpperInvariant()} " });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(booked.Code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicCode").GetString());
        }
    }

    // ─── One answer for everything that does not identify a booking ─────────────────────────────────────────────────────

    [Fact]
    public async Task NotFound_AWrongCode_AWrongAddress_AnotherSuppliersBooking_AnUnknownSupplier_Are_TheSame404_OnEveryEndpoint()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var other = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var theirs = await BookAsync(client, other, PublicBookingTestData.TuesdayAt9);
        var start = PublicBookingTestData.TuesdayAt9.AddDays(1);

        var credentials = new Dictionary<string, object>
        {
            ["a code that does not exist"] = new { slug = supplier.Slug, code = "7K2XM-9QD4T", email = booked.Email },
            ["a code that is not a code"] = new { slug = supplier.Slug, code = "non-un-codice", email = booked.Email },
            ["another address"] = new { slug = supplier.Slug, code = booked.Code, email = "un.altro@example.com" },
            ["a supplier that does not exist"] = new { slug = "non-esiste", code = booked.Code, email = booked.Email },
            ["the code of another supplier's booking"] = new { slug = supplier.Slug, code = theirs.Code, email = theirs.Email },
            ["the slug of another supplier"] = new { slug = other.Slug, code = booked.Code, email = booked.Email },
            ["the address of another supplier's booking"] = new { slug = supplier.Slug, code = booked.Code, email = theirs.Email },
        };

        foreach (var path in Paths)
        {
            var bodies = new Dictionary<string, string>();
            foreach (var (name, access) in credentials)
            {
                var body = path == "reschedule" ? Http.Merge(access, new { startUtc = start }) : path == "cancel" ? Http.Merge(access, new { reason = "x" }) : access;
                var response = await Http.PostAsync(client, path, body);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} / {name}: {(int)response.StatusCode}");
                Http.AssertPrivateAnswer(response);
                bodies[name] = Http.SameBody(await response.Content.ReadFromJsonAsync<JsonElement>());
            }

            // One answer: the same status, the same code, the same words, the same shape — whichever of the seven it was.
            Assert.True(bodies.Values.Distinct().Count() == 1, $"{path}: {string.Join(" | ", bodies.Values.Distinct())}");
            Assert.Contains("\"code\":\"supplier_booking_not_found\"", bodies.Values.First());
        }

        // Nothing was changed by any of them, at either supplier.
        foreach (var owner in new[] { supplier, other })
        {
            var inbox = await owner.InboxAsync(factory);
            Assert.Equal("Richiesto", inbox.GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task NotFound_TheBodyNamesNothingOfTheBookingTheSupplierOrTheAddress()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        var response = await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = booked.Code, email = "un.altro@example.com" });

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("un.altro@example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(booked.Code, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(supplier.Slug, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SupplierBooking", text);
        var problem = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("supplier_booking_not_found", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task NotFound_TwoCustomersOfTheSameSupplier_NeverFindEachOthersBooking()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var mario = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var anna = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1));

        var marioView = await (await Http.PostAsync(client, "lookup", mario.Access())).Content.ReadFromJsonAsync<JsonElement>();
        var annaView = await (await Http.PostAsync(client, "lookup", anna.Access())).Content.ReadFromJsonAsync<JsonElement>();
        var crossed = await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = mario.Code, email = anna.Email });
        var crossedCancel = await Http.PostAsync(client, "cancel", new { slug = supplier.Slug, code = anna.Code, email = mario.Email });

        Assert.Equal(PublicBookingTestData.TuesdayAt9, marioView.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(PublicBookingTestData.TuesdayAt9.AddDays(1), annaView.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossedCancel.StatusCode);
        Assert.Equal("Richiesto", (await (await Http.PostAsync(client, "lookup", anna.Access())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Validation_ABodyWithoutTheSlugTheCodeOrTheAddress_Is400_ForEveryone_AndTellsNothingOfAnyBooking()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        foreach (var path in Paths)
        {
            foreach (var body in new object[]
                     {
                         new { },
                         new { slug = supplier.Slug, code = booked.Code },
                         new { slug = supplier.Slug, email = booked.Email },
                         new { code = booked.Code, email = booked.Email },
                         new { slug = "", code = booked.Code, email = booked.Email },
                         new { slug = supplier.Slug, code = booked.Code, email = "   " },
                     })
            {
                var response = await Http.PostAsync(client, path, body);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{path}: {(int)response.StatusCode} for {JsonSerializer.Serialize(body)}");
                // The answers MVC gives on its own, before the action runs, are as private as the others.
                Http.AssertPrivateAnswer(response);
            }
        }

        // And a body that is not JSON at all.
        var notJson = await client.PostAsync(
            "/api/public/supplier-bookings/lookup", new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Http.AssertPrivateAnswer(notJson);
    }

    [Fact]
    public async Task Validation_RescheduleWithoutAStart_Is400_AndABodyThatIsTooBigIsRefusedBeforeItIsRead()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        var noStart = await Http.PostAsync(client, "reschedule", booked.Access());
        var huge = await client.PostAsync(
            "/api/public/supplier-bookings/cancel",
            new StringContent($"{{\"reason\":\"{new string('a', 200_000)}\"}}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, noStart.StatusCode);
        Assert.True(huge.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"{(int)huge.StatusCode}");
    }

    [Fact]
    public async Task TheCodeAndTheAddress_AreNotAcceptedInTheUrl()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        // Only POST with a body: a GET with the credentials in the query string finds nothing (the route does not exist).
        var query = $"slug={supplier.Slug}&code={booked.Code}&email={Uri.EscapeDataString(booked.Email)}";
        foreach (var path in Paths)
        {
            var get = await client.GetAsync($"/api/public/supplier-bookings/{path}?{query}");
            var post = await client.PostAsync($"/api/public/supplier-bookings/{path}?{query}", content: null);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            Assert.True(post.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest, $"{path}: {(int)post.StatusCode}");
        }
    }

    // ─── Reschedule ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reschedule_MovesTheRequest_FreesTheOldSlot_AndTellsTheSupplier()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var newStart = PublicBookingTestData.TuesdayAt9.AddDays(1);

        var response = await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = newStart }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Http.AssertPrivateAnswer(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", view.GetProperty("status").GetString());
        Assert.Equal(newStart, view.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), view.GetProperty("respondBy").GetDateTime().ToUniversalTime());
        var toSupplier = Assert.Single(
            factory.EmailsTo(supplier.ContactEmail),
            e => e.Template == EmailTemplates.Names.SupplierBookingRescheduledByCustomer);
        Assert.Contains("Cliente: <strong>Mario R.</strong>", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        Assert.Contains(
            factory.Pushes.ToArray(),
            p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId) && p.Payload.Type == PushTypes.ServiceRequestRescheduled);

        // The old slot is bookable, the new one is not.
        var old = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        var taken = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings",
            PublicBookingTestData.Body(supplier.Service, newStart, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, old.StatusCode);
        await Http.AssertProblemAsync(taken, HttpStatusCode.Conflict, "supplier_slot_unavailable");
    }

    [Fact]
    public async Task Reschedule_ASlotThatIsNotFree_OrNoSlotCanHave_Is409_NeverA500_AndTheRequestStays()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var mine = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1));

        foreach (var start in new[]
                 {
                     PublicBookingTestData.TuesdayAt9.AddDays(1), // taken by another customer
                     PublicBookingTestData.TuesdayAt9.AddDays(5), // a Sunday
                     PublicBookingTestData.TuesdayAt9.AddDays(-7), // the past
                     PublicBookingTestData.TuesdayAt9.AddMinutes(20), // not on the grid
                     new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(9999, 12, 31, 22, 30, 0, DateTimeKind.Utc),
                 })
        {
            var response = await Http.PostAsync(client, "reschedule", mine.Access(new { startUtc = start }));
            await Http.AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_slot_unavailable");
        }

        var view = await (await Http.PostAsync(client, "lookup", mine.Access())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PublicBookingTestData.TuesdayAt9, view.GetProperty("startUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Reschedule_AStartThatIsNotAWholeMinute_Is422NamingTheField_AndATakenRequestCannotBeMoved()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        var seconds = await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(1).AddSeconds(30) }));
        using (var asSupplier = supplier.Client(factory))
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{booked.RequestId}/take", content: null)).StatusCode);
        var afterTake = await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(1) }));

        var invalid = await Http.AssertProblemAsync(seconds, (HttpStatusCode)422, "supplier_booking_invalid");
        Assert.Equal(new[] { "startUtc" }, invalid.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        await Http.AssertProblemAsync(afterTake, (HttpStatusCode)422, "supplier_booking_cannot_reschedule");
    }

    // ─── Cancel ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_AReasonThatIsTooLong_Is422NamingTheField_AndATakenRequestOnceItsTimeHasPassedCannotBeCancelled()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        var tooLong = await Http.PostAsync(client, "cancel", booked.Access(new { reason = new string('x', 501) }));
        using (var asSupplier = supplier.Client(factory))
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{booked.RequestId}/take", content: null)).StatusCode);

        var invalid = await Http.AssertProblemAsync(tooLong, (HttpStatusCode)422, "supplier_booking_invalid");
        Assert.Equal(new[] { "reason" }, invalid.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));

        using (factory.MoveClock(TimeSpan.FromDays(1) + TimeSpan.FromHours(3)))
        {
            // Tuesday 13 October 12:00 in Rome: the work started at 09:00 and nobody marked it.
            var late = await Http.PostAsync(client, "cancel", booked.Access());
            await Http.AssertProblemAsync(late, (HttpStatusCode)422, "supplier_booking_cannot_cancel");
            var view = await (await Http.PostAsync(client, "lookup", booked.Access())).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(view.GetProperty("actions").GetProperty("canCancel").GetBoolean());
        }
    }

    // ─── The proposed time ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Proposal_TheSupplierProposesAnotherTime_TheCustomerAccepts_AndBothAreTold()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var proposed = PublicBookingTestData.TuesdayAt9.AddHours(5); // 14:00
        await ProposeAsync(supplier, booked, proposed, "Il mattino sono già impegnato");

        var lookup = await (await Http.PostAsync(client, "lookup", booked.Access())).Content.ReadFromJsonAsync<JsonElement>();
        var proposal = lookup.GetProperty("proposal");
        Assert.Equal(proposed, proposal.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("Il mattino sono già impegnato", proposal.GetProperty("message").GetString());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddHours(24), proposal.GetProperty("answerBy").GetDateTime().ToUniversalTime());
        Assert.True(lookup.GetProperty("actions").GetProperty("canRespondToProposal").GetBoolean());
        Assert.True(Http.IsNull(lookup, "respondBy"));
        // The booking keeps the time it was asked for until the customer accepts.
        Assert.Equal(PublicBookingTestData.TuesdayAt9, lookup.GetProperty("startUtc").GetDateTime().ToUniversalTime());

        var accepted = await Http.PostAsync(client, "proposal/accept", booked.Access());

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Http.AssertPrivateAnswer(accepted);
        var view = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PresoInCarico", view.GetProperty("status").GetString());
        Assert.Equal(proposed, view.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.True(Http.IsNull(view, "proposal"));
        Assert.Equal(PublicBookingTestData.Address, view.GetProperty("place").GetProperty("address").GetString());
        var toSupplier = Assert.Single(
            factory.EmailsTo(supplier.ContactEmail),
            e => e.Template == EmailTemplates.Names.SupplierBookingProposalAnsweredByCustomer);
        Assert.Contains("ha <strong>accettato</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains(
            factory.EmailsTo(booked.Email),
            e => e.Template == EmailTemplates.Names.SupplierBookingAccepted && e.Content.HtmlBody.Contains("14:00", StringComparison.Ordinal));
        Assert.Contains(
            factory.Pushes.ToArray(),
            p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId) && p.Payload.Type == PushTypes.ServiceRequestProposalAccepted);

        // A second answer finds no proposal.
        var again = await Http.PostAsync(client, "proposal/accept", booked.Access());
        await Http.AssertProblemAsync(again, (HttpStatusCode)422, "supplier_booking_no_proposal");
    }

    [Fact]
    public async Task Proposal_TheCustomerTurnsItDown_TheRequestStaysNewAtItsTime_AndTheProposalCanBeMadeAgain()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        await ProposeAsync(supplier, booked, PublicBookingTestData.TuesdayAt9.AddHours(5), null);

        var rejected = await Http.PostAsync(client, "proposal/reject", booked.Access());

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var view = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", view.GetProperty("status").GetString());
        Assert.True(Http.IsNull(view, "proposal"));
        Assert.Equal(PublicBookingTestData.TuesdayAt9, view.GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), view.GetProperty("respondBy").GetDateTime().ToUniversalTime());
        var toSupplier = Assert.Single(
            factory.EmailsTo(supplier.ContactEmail),
            e => e.Template == EmailTemplates.Names.SupplierBookingProposalAnsweredByCustomer);
        Assert.Contains("ha <strong>rifiutato</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains(
            factory.Pushes.ToArray(),
            p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId) && p.Payload.Type == PushTypes.ServiceRequestProposalRejected);

        var none = await Http.PostAsync(client, "proposal/reject", booked.Access());
        await Http.AssertProblemAsync(none, (HttpStatusCode)422, "supplier_booking_no_proposal");
        await ProposeAsync(supplier, booked, PublicBookingTestData.TuesdayAt9.AddDays(1), "Ho un buco il giorno dopo");
        var again = await Http.PostAsync(client, "proposal/accept", booked.Access());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Proposal_AfterTheDayTheCustomerHas_BothAnswersAreRefused()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        await ProposeAsync(supplier, booked, PublicBookingTestData.TuesdayAt9.AddHours(5), null);

        using (factory.MoveClock(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1)))
        {
            var accept = await Http.PostAsync(client, "proposal/accept", booked.Access());
            var reject = await Http.PostAsync(client, "proposal/reject", booked.Access());
            var view = await (await Http.PostAsync(client, "lookup", booked.Access())).Content.ReadFromJsonAsync<JsonElement>();

            await Http.AssertProblemAsync(accept, (HttpStatusCode)422, "supplier_booking_proposal_expired");
            await Http.AssertProblemAsync(reject, (HttpStatusCode)422, "supplier_booking_proposal_expired");
            Assert.False(view.GetProperty("actions").GetProperty("canRespondToProposal").GetBoolean());
        }
    }

    [Fact]
    public async Task Proposal_WithNoProposalAtAll_Is422_ForBothAnswers()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);

        await Http.AssertProblemAsync(await Http.PostAsync(client, "proposal/accept", booked.Access()), (HttpStatusCode)422, "supplier_booking_no_proposal");
        await Http.AssertProblemAsync(await Http.PostAsync(client, "proposal/reject", booked.Access()), (HttpStatusCode)422, "supplier_booking_no_proposal");
    }

    // ─── What nobody may learn ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryAnswer_IsNotIndexableNotCacheableAndSetsNoCookie_AndEchoesNothingPersonal()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        var responses = new List<HttpResponseMessage>
        {
            await Http.PostAsync(client, "lookup", booked.Access()),
            await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = booked.Code, email = "un.altro@example.com" }),
            await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(5) })),
            await Http.PostAsync(client, "proposal/accept", booked.Access()),
            await Http.PostAsync(client, "cancel", booked.Access(new { reason = new string('x', 501) })),
        };

        foreach (var response in responses)
        {
            Http.AssertPrivateAnswer(response);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Mario", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(booked.Email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("3331234567", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PublicBookingTestData.Address, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheLogs_CarryNoCodeNoAddressNoNameNoPhoneAndNoReason_ForAnyOfTheFiveEndpoints()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var booked = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9);
        const string reason = "Motivo-segretissimo-del-cliente";
        await Http.PostAsync(client, "lookup", booked.Access());
        await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = booked.Code, email = "chi.ero@example.com" });
        await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(1) }));
        await Http.PostAsync(client, "proposal/accept", booked.Access());
        await Http.PostAsync(client, "proposal/reject", booked.Access());
        await Http.PostAsync(client, "cancel", booked.Access(new { reason }));
        await Http.PostAsync(client, "cancel", booked.Access(new { reason }));

        var logs = factory.LogText();

        Assert.NotEmpty(logs);
        var code = booked.Code.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var secret in PublicBookingTestData.Secrets(booked.Email).Concat([booked.Code, code, reason, "chi.ero@example.com"]))
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
    }

    // ─── helpers ───

    private Task<BookedForManagement> BookAsync(HttpClient client, BookableSupplier supplier, DateTime start) =>
        Http.BookAsync(factory, client, supplier, start);

    private Task ProposeAsync(BookableSupplier supplier, BookedForManagement booked, DateTime start, string? message) =>
        Http.ProposeAsync(factory, supplier, booked, start, message);
}

/// <summary>The showcase host with the per-IP limit of the customer's area at three requests per five minutes.</summary>
public sealed class PublicBookingManagePerIpThrottledFactory : PublicBookingFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("RateLimiting:PublicGuestBookingLookup:PermitLimit", "3"),
    ];
}

/// <summary>The showcase host with the per-address limit of the customer's area at three requests per fifteen minutes.</summary>
public sealed class PublicBookingManagePerEmailThrottledFactory : PublicBookingFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("RateLimiting:SupplierBookingManagePerEmail:PermitLimit", "3"),
    ];
}

/// <summary>
/// SP-11: the two limits of the customer's own area of a booking — per IP (the policy of "Le mie prenotazioni") and per address and
/// supplier — answer the same 429, every attempt counts whether or not it finds a booking, and the limit per address always names the
/// whole window, so neither tells whether the address has a booking or when somebody last looked for one.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingManagementRateLimitIntegrationTests(
    PublicBookingManagePerIpThrottledFactory perIp,
    PublicBookingManagePerEmailThrottledFactory perEmail)
    : IClassFixture<PublicBookingManagePerIpThrottledFactory>, IClassFixture<PublicBookingManagePerEmailThrottledFactory>
{
    [Fact]
    public async Task PerIp_TheFourthCallInTheWindow_Is429_WhateverTheEndpoint_AnotherIpIsNot()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perIp);
        using var client = perIp.CreateClient();
        var booked = await Http.BookAsync(perIp, client, supplier, PublicBookingTestData.TuesdayAt9);
        const string ip = "203.0.113.91";

        // Three calls use the budget of the IP, found or not, of any of the five endpoints.
        var first = await Http.PostAsync(client, "lookup", booked.Access(), ip);
        var second = await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = "7K2XM-9QD4T", email = booked.Email }, ip);
        var third = await Http.PostAsync(client, "proposal/accept", booked.Access(), ip);
        var limited = await Http.PostAsync(client, "cancel", booked.Access(), ip);
        var otherIp = await Http.PostAsync(client, "lookup", booked.Access(), "203.0.113.92");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal((HttpStatusCode)422, third.StatusCode);
        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        // The middleware answers before MVC, and the answer is as private as the ones of the controller.
        Http.AssertPrivateAnswer(limited);
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);

        // The cancellation that was refused did not happen.
        var view = await (await Http.PostAsync(client, "lookup", booked.Access(), "203.0.113.93")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", view.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PerAddress_EveryAttemptCounts_TheFourthIs429NamingTheWholeWindow_AnotherAddressOrSupplierIsNot()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perEmail);
        var other = await PublicBookingTestData.SeedAsync(perEmail);
        using var client = perEmail.CreateClient();
        var booked = await Http.BookAsync(perEmail, client, supplier, PublicBookingTestData.TuesdayAt9);
        var wrong = new { slug = supplier.Slug, code = "7K2XM-9QD4T", email = booked.Email };

        // A miss, a hit, a miss: three attempts of the same address at the same supplier, from three different IPs.
        var first = await Http.PostAsync(client, "lookup", wrong, "203.0.113.101");
        var second = await Http.PostAsync(client, "lookup", booked.Access(), "203.0.113.102");
        var third = await Http.PostAsync(client, "reschedule", booked.Access(new { startUtc = PublicBookingTestData.TuesdayAt9.AddDays(5) }), "203.0.113.103");
        var limited = await Http.PostAsync(client, "lookup", booked.Access(), "203.0.113.104");

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        // The whole window of the limit, to the second, however long ago the first attempt was: the time that is left would tell
        // when somebody last looked for a booking with this address.
        Assert.Equal(TimeSpan.FromMinutes(15), limited.Headers.RetryAfter?.Delta);
        Assert.Equal(15 * 60, problem.GetProperty("retryAfterSeconds").GetInt32());
        Http.AssertPrivateAnswer(first);
        Http.AssertPrivateAnswer(limited);

        // Another address at the same supplier, and the same address at another supplier, have budgets of their own.
        var anotherAddress = await Http.PostAsync(client, "lookup", new { slug = supplier.Slug, code = booked.Code, email = "un.altro@example.com" }, "203.0.113.105");
        var anotherSupplier = await Http.PostAsync(client, "lookup", new { slug = other.Slug, code = booked.Code, email = booked.Email }, "203.0.113.106");
        Assert.Equal(HttpStatusCode.NotFound, anotherAddress.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, anotherSupplier.StatusCode);
    }

    [Fact]
    public async Task TheTwoLimits_AnswerTheSameShape()
    {
        var supplier = await PublicBookingTestData.SeedAsync(perIp);
        var supplier2 = await PublicBookingTestData.SeedAsync(perEmail);
        using var ipClient = perIp.CreateClient();
        using var emailClient = perEmail.CreateClient();
        var byIp = await Http.BookAsync(perIp, ipClient, supplier, PublicBookingTestData.TuesdayAt9);
        var byEmail = await Http.BookAsync(perEmail, emailClient, supplier2, PublicBookingTestData.TuesdayAt9);
        for (var i = 0; i < 3; i++)
        {
            await Http.PostAsync(ipClient, "lookup", byIp.Access(), "203.0.113.111");
            await Http.PostAsync(emailClient, "lookup", byEmail.Access(), "203.0.113.112");
        }

        var limitedByIp = await Http.PostAsync(ipClient, "lookup", byIp.Access(), "203.0.113.111");
        var limitedByEmail = await Http.PostAsync(emailClient, "lookup", byEmail.Access(), "203.0.113.113");

        var ipProblem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limitedByIp);
        var emailProblem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limitedByEmail);
        Assert.Equal(
            ipProblem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            emailProblem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ipProblem.GetProperty("code").GetString(), emailProblem.GetProperty("code").GetString());
        Assert.Equal(ipProblem.GetProperty("title").GetString(), emailProblem.GetProperty("title").GetString());
        Assert.Equal(ipProblem.GetProperty("status").GetInt32(), emailProblem.GetProperty("status").GetInt32());
        Assert.Equal(limitedByIp.Content.Headers.ContentType?.MediaType, limitedByEmail.Content.Headers.ContentType?.MediaType);
    }
}

/// <summary>
/// SP-11 with the flag <c>SupplierShowcaseBooking</c> <b>off</b> (the default of every environment): the five endpoints answer 404 like
/// a route that does not exist, whatever the body, before authentication and model binding, and nothing is read or changed.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierBookingManagementFlagOffIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task AllFiveEndpoints_AnswerTheSame404AsARouteThatDoesNotExist_WhateverTheBody()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateClient();
        var missing = await client.GetAsync("/api/public/non-esiste/non-esiste");
        var missingDetail = (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        var access = new { slug = supplier.Slug, code = "7K2XM-9QD4T", email = "mario@example.com" };

        foreach (var path in new[] { "lookup", "cancel", "reschedule", "proposal/accept", "proposal/reject" })
        {
            var responses = new[]
            {
                await Http.PostAsync(client, path, access),
                await Http.PostAsync(client, path, Http.Merge(access, new { startUtc = DateTime.UtcNow.AddDays(3), reason = "x" })),
                // A body that would be refused is a 404 too, never a 400: nothing is read.
                await Http.PostAsync(client, path, new { }),
                await client.PostAsync($"/api/public/supplier-bookings/{path}", new StringContent("{ not json", Encoding.UTF8, "application/json")),
                await client.PostAsync($"/api/public/supplier-bookings/{path}", content: null),
            };

            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("not_found", body.GetProperty("code").GetString());
                Assert.Equal(missingDetail, body.GetProperty("detail").GetString());
            }
        }
    }
}
