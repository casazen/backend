using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10b on the real pipeline, with the flag <c>SupplierShowcaseBooking</c> on: an anonymous customer describes what it needs, follows the link of
/// the e-mail it receives, and only then the supplier has a request — with "Nome C." and the comune, and none of the customer's words, name,
/// e-mail or phone. The answers are never cached or indexed and the logs carry nothing personal. Runs on PostgreSQL in CI and on the in-memory
/// fallback locally.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierInquiryIntegrationTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    private const string Words = "Vorrei far sistemare la ringhiera del balcone e controllare la porta d'ingresso.";

    [Fact]
    public async Task Inquiry_FromTheWordsToTheRequestOfTheSupplier_EndToEnd()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();

        // 1. The customer writes: an inquiry, an e-mail to check the address, and the supplier hears nothing.
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertPrivateAnswer(created);
        var inquiry = await created.Content.ReadFromJsonAsync<JsonElement>();
        var inquiryId = inquiry.GetProperty("id").GetGuid();
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), inquiry.GetProperty("expiresAt").GetDateTime().ToUniversalTime());
        Assert.Equal(new[] { "expiresAt", "id" }, inquiry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var verification = Assert.Single(factory.EmailsTo(email));
        Assert.Equal(EmailTemplates.Names.SupplierInquiryVerification, verification.Template);
        var (linkInquiry, token) = LinkOf(verification);
        Assert.Equal(inquiryId, linkInquiry);
        Assert.Empty(factory.EmailsTo(supplier.ContactEmail));
        Assert.Equal(0, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());

        // 2. A wrong token is the same 404 as a link that does not exist, and changes nothing.
        var wrong = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token = "token-sbagliato-di-prova-0123456789" });
        await AssertProblemAsync(wrong, HttpStatusCode.NotFound, "supplier_booking_link_invalid");
        Assert.Equal(0, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());

        // 3. It follows the link: now the supplier has a request, and both sides are told.
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        AssertPrivateAnswer(confirmed);
        var request = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[0-9A-Z]{5}-[0-9A-Z]{5}$", request.GetProperty("publicCode").GetString());
        Assert.Equal("Richiesto", request.GetProperty("status").GetString());
        Assert.False(request.GetProperty("alreadyConfirmed").GetBoolean());
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(60), request.GetProperty("respondBy").GetDateTime().ToUniversalTime());
        Assert.False(request.TryGetProperty("serviceName", out _), "a request about no service names none");

        Assert.Contains(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierInquiryReceipt);
        var toSupplier = Assert.Single(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierInquiryNewRequest);
        var mailed = toSupplier.Content.Subject + toSupplier.Content.HtmlBody;
        Assert.Contains("Mario R.", mailed);
        foreach (var secret in PublicBookingTestData.Secrets(email).Append("ringhiera").Append("balcone"))
            Assert.DoesNotContain(secret, mailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Pushes.ToArray(), p => p.Audience == PushAudience.SupplierOrg(supplier.OrgId));

        // 4. The supplier sees "Nome C." and the comune: a request with no time, none of the customer's contacts, none of its words.
        var inbox = await supplier.InboxAsync(factory);
        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal("showcase", item.GetProperty("source").GetString());
        Assert.Equal("Richiesto", item.GetProperty("status").GetString());
        Assert.Equal("Mario R.", item.GetProperty("clientName").GetString());
        Assert.Equal(PublicBookingTestData.City, item.GetProperty("city").GetString());
        Assert.False(item.GetProperty("contactDisclosed").GetBoolean());
        Assert.True(!item.TryGetProperty("scheduledStart", out var start) || start.ValueKind == JsonValueKind.Null);
        var beforeTake = item.ToString();
        foreach (var secret in PublicBookingTestData.Secrets(email).Append("ringhiera"))
            Assert.DoesNotContain(secret, beforeTake, StringComparison.OrdinalIgnoreCase);

        // 5. A second click on the link answers the same, and does nothing again.
        var emailsBefore = factory.EmailsTo(email).Count;
        var again = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replay.GetProperty("alreadyConfirmed").GetBoolean());
        Assert.Equal(request.GetProperty("publicCode").GetString(), replay.GetProperty("publicCode").GetString());
        Assert.Equal(emailsBefore, factory.EmailsTo(email).Count);
        Assert.Equal(1, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Inquiry_TheSupplierReadsAndAnswers_TheCustomerIsToldWithoutTheWords_AndTheRequestIsNotTaken()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var other = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email));
        var inquiryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = LinkOf(Assert.Single(factory.EmailsTo(email)));
        await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token });

        // 1. The console knows the kind of the request, and the thread has what the customer wrote.
        var item = Assert.Single((await supplier.InboxAsync(factory)).GetProperty("items").EnumerateArray());
        Assert.Equal("Custom", item.GetProperty("kind").GetString());
        Assert.False(IsNull(item, "respondBy"));
        var id = item.GetProperty("id").GetGuid();
        using var asSupplier = supplier.Client(factory);
        var thread = await asSupplier.GetFromJsonAsync<JsonElement>($"/api/supplier/requests/{id}/thread");
        Assert.True(thread.GetProperty("canWrite").GetBoolean());
        var first = Assert.Single(thread.GetProperty("messages").EnumerateArray());
        Assert.Equal("Customer", first.GetProperty("author").GetString());
        Assert.Equal(Words, first.GetProperty("body").GetString());

        // 2. Another supplier gets the same 404 for reading and for writing, and a request that is not on measure is one too.
        using var asOther = other.Client(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await asOther.GetAsync($"/api/supplier/requests/{id}/thread")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asOther.PostAsJsonAsync($"/api/supplier/requests/{id}/messages", new { body = "Buongiorno a tutti" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await asSupplier.GetAsync($"/api/supplier/requests/{Guid.NewGuid()}/thread")).StatusCode);

        // 3. The supplier answers: the customer is told with a link and none of the words, and the hour stops.
        var posted = await asSupplier.PostAsJsonAsync($"/api/supplier/requests/{id}/messages", new { body = "Posso passare giovedì per un sopralluogo gratuito." });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Equal("Supplier", (await posted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("author").GetString());
        var reply = Assert.Single(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierInquiryReply);
        Assert.Contains("/fornitori/", reply.Content.HtmlBody);
        foreach (var words in new[] { "giovedì", "sopralluogo", "ringhiera" })
            Assert.DoesNotContain(words, reply.Content.Subject + reply.Content.HtmlBody);
        var answered = Assert.Single((await supplier.InboxAsync(factory)).GetProperty("items").EnumerateArray());
        Assert.True(IsNull(answered, "respondBy"));
        Assert.Equal(2, (await asSupplier.GetFromJsonAsync<JsonElement>($"/api/supplier/requests/{id}/thread")).GetProperty("messages").GetArrayLength());

        // 4. A message that is not one is a 422; the request is not taken the way a booking is: it is refused or answered.
        await AssertProblemAsync(
            await asSupplier.PostAsJsonAsync($"/api/supplier/requests/{id}/messages", new { body = "   " }),
            (HttpStatusCode)422,
            "service_request_message_invalid");
        await AssertProblemAsync(
            await asSupplier.PostAsync($"/api/service-requests/{id}/take", content: null),
            (HttpStatusCode)422,
            "supplier_inquiry_needs_offer");

        // 5. Refused: the customer is told with the reason, and the thread is closed.
        var rejected = await asSupplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non copro questo tipo di lavoro" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierInquiryDeclined);
        await AssertProblemAsync(
            await asSupplier.PostAsJsonAsync($"/api/supplier/requests/{id}/messages", new { body = "Ci ho ripensato, mi dica pure." }),
            HttpStatusCode.Conflict,
            "service_request_thread_closed");
    }

    [Fact]
    public async Task Inquiry_TheCustomerReadsAndWritesInItsOwnArea_TheSupplierIsToldWithoutTheWords_AndTheCustomerClosesTheRequest()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email));
        var inquiryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = LinkOf(Assert.Single(factory.EmailsTo(email)));
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token });
        var code = (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicCode").GetString();
        var access = new { slug = supplier.Slug, code, email };

        // 1. The customer finds its request again: a request on measure, waiting for the supplier, with its own words as the first message.
        var lookup = await client.PostAsJsonAsync("/api/public/supplier-bookings/lookup", access);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        AssertPrivateAnswer(lookup);
        var waiting = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Custom", waiting.GetProperty("kind").GetString());
        Assert.Equal("AwaitingSupplier", waiting.GetProperty("negotiation").GetString());
        Assert.True(waiting.GetProperty("thread").GetProperty("canWrite").GetBoolean());
        var first = Assert.Single(waiting.GetProperty("thread").GetProperty("messages").EnumerateArray());
        Assert.Equal("customer", first.GetProperty("author").GetString());
        Assert.Equal(Words, first.GetProperty("body").GetString());

        // 2. The supplier answers; the customer sees it as new, once. The clock of the host is frozen: the answer comes a minute after the
        // customer's words, otherwise both messages have the same instant and the thread lists them in the order of their random ids.
        var requestId = Assert.Single((await supplier.InboxAsync(factory)).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        using var asSupplier = supplier.Client(factory);
        using (factory.MoveClock(TimeSpan.FromMinutes(1)))
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await asSupplier.PostAsJsonAsync($"/api/supplier/requests/{requestId}/messages", new { body = "Posso passare giovedì per un sopralluogo." })).StatusCode);
            var talking = await (await client.PostAsJsonAsync("/api/public/supplier-bookings/lookup", access)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("InConversation", talking.GetProperty("negotiation").GetString());
            var answer = talking.GetProperty("thread").GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("supplier", answer.GetProperty("author").GetString());
            Assert.True(IsNull(answer, "readAt"));
            var read = await (await client.PostAsJsonAsync("/api/public/supplier-bookings/lookup", access)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(IsNull(read.GetProperty("thread").GetProperty("messages").EnumerateArray().Last(), "readAt"));
        }

        // 3. The customer writes (after the quiet period of the first message): the supplier is told with "Nome C." and never the words.
        using (factory.MoveClock(TimeSpan.FromMinutes(11)))
        {
            var posted = await client.PostAsJsonAsync(
                "/api/public/supplier-bookings/messages", new { slug = supplier.Slug, code, email, body = "Perfetto, venerdì alle nove va bene?" });
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            AssertPrivateAnswer(posted);
            Assert.Equal(3, (await posted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("thread").GetProperty("messages").GetArrayLength());
        }

        var toSupplier = Assert.Single(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierInquiryMessage);
        var mailed = toSupplier.Content.Subject + toSupplier.Content.HtmlBody;
        Assert.Contains("Mario R.", mailed);
        foreach (var secret in PublicBookingTestData.Secrets(email).Append("venerdì").Append("alle nove"))
            Assert.DoesNotContain(secret, mailed, StringComparison.OrdinalIgnoreCase);
        var supplierThread = await asSupplier.GetFromJsonAsync<JsonElement>($"/api/supplier/requests/{requestId}/thread");
        Assert.Equal(3, supplierThread.GetProperty("messages").GetArrayLength());
        Assert.True(IsNull(supplierThread.GetProperty("messages").EnumerateArray().Last(), "readAt"));

        // 4. Who is not the customer is the same 404, and the thread is not touched.
        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/public/supplier-bookings/messages", new { slug = supplier.Slug, code, email = PublicBookingTestData.NewEmail(), body = "Sono io, davvero." }),
            HttpStatusCode.NotFound,
            "supplier_booking_not_found");

        // 5. The customer closes the request: free, the supplier is told without a day, the thread is closed for both.
        var cancelled = await client.PostAsJsonAsync("/api/public/supplier-bookings/cancel", new { slug = supplier.Slug, code, email, reason = "Ho trovato un altro fornitore" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var closed = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Annullato", closed.GetProperty("status").GetString());
        Assert.Equal("Closed", closed.GetProperty("negotiation").GetString());
        Assert.False(closed.GetProperty("thread").GetProperty("canWrite").GetBoolean());
        Assert.Contains(factory.EmailsTo(supplier.ContactEmail), e => e.Template == EmailTemplates.Names.SupplierInquiryClosedByCustomer);
        Assert.DoesNotContain(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingCancellationReceipt);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/public/supplier-bookings/messages", new { slug = supplier.Slug, code, email, body = "Un ultimo pensiero." }),
            HttpStatusCode.Conflict,
            "service_request_thread_closed");
        await AssertProblemAsync(
            await asSupplier.PostAsJsonAsync($"/api/supplier/requests/{requestId}/messages", new { body = "Capisco, grazie lo stesso." }),
            HttpStatusCode.Conflict,
            "service_request_thread_closed");
    }

    [Fact]
    public async Task Inquiry_EveryAnswerIsPrivate_AndNeitherTheAnswersNorTheLogsCarryAnythingPersonalOrTheWords()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email));
        var inquiryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = LinkOf(Assert.Single(factory.EmailsTo(email)));

        var responses = new List<HttpResponseMessage>
        {
            created,
            // Refusals: a name that is not one, no service of that name, a supplier that does not exist, a link that is not theirs.
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email, fullName: "A")),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email, service: "non-esiste")),
            await client.PostAsJsonAsync("/api/public/suppliers/non-esiste/inquiries", Body(email)),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token = "token-sbagliato-di-prova-0123456789" }),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token }),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{inquiryId}/confirm-email", new { token }),
        };

        var secrets = PublicBookingTestData.Secrets(email).Append("ringhiera").Append("balcone").ToList();
        foreach (var response in responses)
        {
            AssertPrivateAnswer(response);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        var logs = factory.LogText();
        Assert.NotEmpty(logs);
        foreach (var secret in secrets.Append(token))
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inquiry_TheTrapOfTheRobots_IsAnsweredLikeAnInquiry_AndNothingIsStoredOrSent()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", Body(email, website: "https://spam.example"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(Guid.Empty, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Empty(factory.EmailsTo(email));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.ShowcaseInquiries.Where(i => i.OrgId == supplier.OrgId).CountAsync());
    }

    /// <summary>A member that is null, or left out of the JSON when it is.</summary>
    private static bool IsNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static object Body(
        string email,
        Guid? clientRequestId = null,
        string? service = null,
        string? website = null,
        string fullName = PublicBookingTestData.FullName) =>
        new
        {
            clientRequestId = clientRequestId ?? Guid.NewGuid(),
            service,
            city = PublicBookingTestData.City,
            message = Words,
            fullName,
            email,
            phone = "+39 333 123 4567",
            locale = "it",
            privacyAccepted = true,
            privacyNoticeVersion = "2026-11-test",
            website,
        };

    /// <summary>The inquiry and the token of the link in a verification e-mail (the token is in the fragment of the link).</summary>
    private static (Guid InquiryId, string Token) LinkOf((string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template) email)
    {
        var match = Regex.Match(email.Content.HtmlBody, "inquiry=([0-9a-fA-F-]{36})#token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "the verification e-mail has no link with the inquiry and the token");
        return (Guid.Parse(match.Groups[1].Value), match.Groups[2].Value);
    }

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
        return problem;
    }
}

/// <summary>
/// SP-10b with the flag <c>SupplierShowcaseBooking</c> <b>off</b> (the default of every environment): the two endpoints answer 404 like a route
/// that does not exist, whatever the supplier published and whatever the request, and nothing is stored or sent.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierInquiryFlagOffIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task BothEndpoints_AnswerTheSame404AsARouteThatDoesNotExist_EvenForARealSupplier()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/public/suppliers/non-esiste/non-esiste/non-esiste/non-esiste");
        var responses = new[]
        {
            await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/inquiries",
                new { clientRequestId = Guid.NewGuid(), city = "Monza", message = "Vorrei far sistemare la ringhiera.", fullName = "Mario Rossi", email = PublicBookingTestData.NewEmail() }),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{Guid.NewGuid()}/confirm-email", new { token = "x" }),
            // A request that would be refused is a 404 too, never a 400 or a 422: nothing is read.
            await client.PostAsync($"/api/public/suppliers/{supplier.Slug}/inquiries", new StringContent("{ not json", Encoding.UTF8, "application/json")),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/inquiries/{Guid.NewGuid()}/confirm-email", new { }),
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
        Assert.Equal(0, await db.ShowcaseInquiries.Where(i => i.OrgId == supplier.OrgId).CountAsync());
    }
}
