using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01, B1: the landlord's reminders of an unpaid rent (<c>POST api/long-rent/rents/{id}/reminder</c> and the bulk
/// <c>.../reminders</c>), over the real pipeline (PostgreSQL in CI, InMemory in a local run): the email, the note, the payment link
/// of an org with Stripe Connect, the "last reminder" recorded, the frequency limit, what is refused, and who may. The world is
/// described in <see cref="LongRentWorld"/>: "today" is 2026-10-09 in Rome, 10:00.
/// </summary>
public class LongRentRentReminderIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Url(Guid installmentId) => $"/api/long-rent/rents/{installmentId}/reminder";

    // --- One reminder -----------------------------------------------------------------------------------

    [Fact]
    public async Task Reminder_AnOverdueInstallment_QueuesTheEmailRecordsItAndAnswersWithoutPersonalData()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        var installment = world.Installment("ActiveOct");

        var response = await client.PostAsJsonAsync(Url(installment), new { note = "Ciao <b>Giulia</b>, ricordati del bonifico & grazie" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = Parse(body);
        Assert.Equal(installment, result.GetProperty("installmentId").GetGuid());
        Assert.Equal(1, result.GetProperty("reminderCount").GetInt32());
        Assert.Equal(1, result.GetProperty("recipientCount").GetInt32());
        Assert.False(result.GetProperty("includesPaymentLink").GetBoolean());
        Assert.Equal(LongRentAggregatesFactory.Now.UtcDateTime, result.GetProperty("sentAt").GetDateTime().ToUniversalTime());
        Assert.Equal(LongRentAggregatesFactory.Now.UtcDateTime.AddHours(24), result.GetProperty("nextReminderAllowedAt").GetDateTime().ToUniversalTime());
        // The answer names no one and gives no address.
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Giulia", body, StringComparison.Ordinal);

        var email = Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Equal(EmailTemplates.Names.RentReminder, email.Template);
        Assert.Contains("Bilocale Sparano", email.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("05/10/2026", email.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("Gentile Giulia Verdi,", email.Content.HtmlBody, StringComparison.Ordinal);
        // Past its due date: the wording of an overdue installment, the period and the amount.
        Assert.Contains("era in scadenza il <strong>05/10/2026</strong> e non risulta ancora pagato", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("900,00 €", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("dal <strong>01/10/2026</strong> al <strong>31/10/2026</strong>", email.Content.HtmlBody, StringComparison.Ordinal);
        // The note is quoted, encoded: the landlord can add words, never markup.
        Assert.Contains("Messaggio del locatore", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ciao &lt;b&gt;Giulia&lt;/b&gt;, ricordati del bonifico &amp; grazie", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Giulia</b>", email.Content.HtmlBody, StringComparison.Ordinal);
        // Without Stripe Connect there is no link: the tenant pays the way agreed with the landlord.
        Assert.DoesNotContain("/rent/pay/", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Per il pagamento usa il metodo che hai concordato con il locatore", email.Content.HtmlBody, StringComparison.Ordinal);

        var stored = await ReadAsync(installment);
        Assert.Equal(LongRentAggregatesFactory.Now.UtcDateTime, stored.LastReminderAt);
        Assert.Equal(1, stored.ReminderCount);
        Assert.Null(stored.PaymentTokenHash);
        Assert.Equal(RentLedgerStatus.Scheduled, stored.Status);
    }

    [Fact]
    public async Task Reminder_AnInstallmentNotDueYet_SaysItIsDueAndNotThatItWasMissed()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        var response = await client.PostAsync(Url(world.Installment("ActiveNov")), content: null);

        // No body at all is fine: the note is optional.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var email = Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Contains("è da pagare entro il <strong>05/11/2026</strong> e non risulta ancora pagato", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("era in scadenza", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Messaggio del locatore", email.Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reminder_AWhitespaceNote_IsNoNote()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        var response = await client.PostAsJsonAsync(Url(world.Installment("ActiveOct")), new { note = "   \n  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Messaggio del locatore", Assert.Single(EmailsTo(world, "Giulia", "Verdi")).Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reminder_ANoteOfExactlyTheLimit_IsSent_OneMoreCharacterIsRefused()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        var installment = world.Installment("ActiveOct");

        var tooLong = await client.PostAsJsonAsync(Url(installment), new { note = new string('x', RentCharges.MaxReminderNoteLength + 1) });
        var accepted = await client.PostAsJsonAsync(Url(installment), new { note = new string('x', RentCharges.MaxReminderNoteLength) });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        // The refused one recorded nothing and sent nothing.
        Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Equal(1, (await ReadAsync(installment)).ReminderCount);
    }

    [Fact]
    public async Task Reminder_OrgWithStripeConnect_CarriesANewPersonalLinkThatReplacesThePreviousOne()
    {
        var world = await LongRentWorld.SeedAsync(factory, connect: true);
        using var client = world.OwnerClient(factory);
        using var tenant = factory.CreateClient();
        var installment = world.Installment("ActiveOct");

        var first = await client.PostAsync(Url(installment), content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(Parse(await first.Content.ReadAsStringAsync()).GetProperty("includesPaymentLink").GetBoolean());
        var firstEmail = Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Contains("Puoi pagarlo online dal link qui sotto", firstEmail.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Per il pagamento usa il metodo", firstEmail.Content.HtmlBody, StringComparison.Ordinal);
        var firstToken = TokenOf(firstEmail.Content.HtmlBody, installment);
        var stored = await ReadAsync(installment);
        Assert.NotNull(stored.PaymentTokenHash);
        Assert.NotNull(stored.PaymentRequestedAt);
        // The link opens the payment page of that installment.
        var page = await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installment}", new { token = firstToken });
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("Payable", Parse(await page.Content.ReadAsStringAsync()).GetProperty("state").GetString());

        // A day later the second reminder has a new link; the first stops working.
        factory.Clock.Advance(TimeSpan.FromHours(24));
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(installment), content: null)).StatusCode);
            var secondEmail = EmailsTo(world, "Giulia", "Verdi").Last();
            var secondToken = TokenOf(secondEmail.Content.HtmlBody, installment);
            Assert.NotEqual(firstToken, secondToken);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installment}", new { token = firstToken })).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installment}", new { token = secondToken })).StatusCode);
            Assert.Equal(2, (await ReadAsync(installment)).ReminderCount);
        }
        finally
        {
            factory.Clock.SetUtcNow(LongRentAggregatesFactory.Now);
        }
    }

    [Fact]
    public async Task Reminder_EveryTenantWithAnAddressIsReminded_OnceEach()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        await AddTenantAsync(world, world.Active, "Luigi", "Verdi", world.EmailOf("Luigi", "Verdi"), position: 1);
        // A second tenant who shares the address of the first: one email only.
        await AddTenantAsync(world, world.Active, "Carla", "Verdi", world.EmailOf("Giulia", "Verdi").ToUpperInvariant(), position: 2);
        using var client = world.OwnerClient(factory);

        var response = await client.PostAsync(Url(world.Installment("ActiveOct")), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Parse(await response.Content.ReadAsStringAsync()).GetProperty("recipientCount").GetInt32());
        Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
        var luigi = Assert.Single(EmailsTo(world, "Luigi", "Verdi"));
        Assert.Contains("Gentile Luigi Verdi,", luigi.Content.HtmlBody, StringComparison.Ordinal);
    }

    // --- The frequency limit ----------------------------------------------------------------------------

    [Fact]
    public async Task Reminder_ASecondOneWithinTheInterval_IsRefusedAndSendsNothing_ThenItWorksAfterIt()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        var installment = world.Installment("ActiveOct");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(installment), content: null)).StatusCode);

        try
        {
            factory.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
            var tooSoon = await client.PostAsync(Url(installment), content: null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSoon.StatusCode);
            var problem = Parse(await tooSoon.Content.ReadAsStringAsync());
            Assert.Equal("rent_reminder_too_soon", problem.GetProperty("code").GetString());
            Assert.Contains("24", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Single(EmailsTo(world, "Giulia", "Verdi"));
            Assert.Equal(1, (await ReadAsync(installment)).ReminderCount);

            // The register says so too: nothing to click until the interval has passed.
            using var register = world.OwnerClient(factory);
            var row = RegisterRow(await GetRegisterAsync(register), installment);
            Assert.False(row.GetProperty("canRemind").GetBoolean());
            Assert.Equal(1, row.GetProperty("reminderCount").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastReminderAt").ValueKind);

            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(RegisterRow(await GetRegisterAsync(register), installment).GetProperty("canRemind").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(installment), content: null)).StatusCode);
            Assert.Equal(2, EmailsTo(world, "Giulia", "Verdi").Count);
            Assert.Equal(2, (await ReadAsync(installment)).ReminderCount);
        }
        finally
        {
            factory.Clock.SetUtcNow(LongRentAggregatesFactory.Now);
        }
    }

    [Fact]
    public async Task Reminder_TheLimitIsPerInstallment_AnotherOneOfTheSameLeaseGoesOut()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(world.Installment("ActiveAug")), content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(world.Installment("ActiveOct")), content: null)).StatusCode);

        Assert.Equal(2, EmailsTo(world, "Giulia", "Verdi").Count);
    }

    // --- What is refused --------------------------------------------------------------------------------

    [Fact]
    public async Task Reminder_APaidInstallment_Returns409AndSendsNothing()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        var response = await client.PostAsync(Url(world.Installment("ActiveSep")), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("rent_installment_not_payable", Parse(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        Assert.Empty(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Equal(0, (await ReadAsync(world.Installment("ActiveSep"))).ReminderCount);
    }

    [Fact]
    public async Task Reminder_APaymentInFlight_Returns409_ACancelledInstallment_Returns409()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        var inFlight = await client.PostAsync(Url(world.Installment("TransitoryOct")), content: null);
        var cancelled = await client.PostAsync(Url(world.Installment("ExpiringCancelled")), content: null);

        Assert.Equal(HttpStatusCode.Conflict, inFlight.StatusCode);
        Assert.Equal("rent_installment_in_flight", Parse(await inFlight.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal("rent_installment_not_payable", Parse(await cancelled.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        var recipients = factory.Emails.Snapshot().Select(e => e.To).ToList();
        Assert.DoesNotContain(world.EmailOf("Sara", "Gallo"), recipients);
        Assert.DoesNotContain(world.EmailOf("Francesco", "Valli"), recipients);
    }

    [Fact]
    public async Task Reminder_NoTenantWithAnAddress_Returns422AndRecordsNothing()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        // The tenant's data were anonymized (LT-12): no address is left.
        await AnonymizeTenantsAsync(world.Active);
        using var client = world.OwnerClient(factory);
        var installment = world.Installment("ActiveOct");

        var response = await client.PostAsync(Url(installment), content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rent_no_tenant_email", Parse(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        var stored = await ReadAsync(installment);
        Assert.Null(stored.LastReminderAt);
        Assert.Equal(0, stored.ReminderCount);
        // The register does not offer the button for it either.
        using var register = world.OwnerClient(factory);
        Assert.False(RegisterRow(await GetRegisterAsync(register), installment).GetProperty("canRemind").GetBoolean());
    }

    [Fact]
    public async Task Reminder_TheEmailCannotBeQueued_Returns422AndTheInstallmentIsAsItWas()
    {
        var world = await LongRentWorld.SeedAsync(factory, connect: true);
        var installment = world.Installment("ActiveOct");
        // A link emailed before (a payment request): it must still work after a reminder that did not go out.
        await SetPreviousLinkAsync(installment, "previous-link-hash", new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc));
        using var client = world.OwnerClient(factory);

        factory.Emails.Refuse = true;
        try
        {
            var response = await client.PostAsync(Url(installment), content: null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("rent_reminder_not_sent", Parse(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        }
        finally
        {
            factory.Emails.Refuse = false;
        }

        var stored = await ReadAsync(installment);
        Assert.Null(stored.LastReminderAt);
        Assert.Equal(0, stored.ReminderCount);
        Assert.Equal("previous-link-hash", stored.PaymentTokenHash);
        Assert.Equal(new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc), stored.PaymentRequestedAt);
        // Nothing was recorded, so the next try is not "too soon".
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(installment), content: null)).StatusCode);
    }

    // --- Who may ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reminder_AnInstallmentOfAnotherOrg_Returns404_AndAnUnknownOne_Returns404()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        var stranger = await LongRentWorld.SeedAsync(factory);
        using var client = stranger.ManagerClient(factory);

        var other = await client.PostAsync(Url(world.Installment("ActiveOct")), content: null);
        var unknown = await client.PostAsync(Url(Guid.NewGuid()), content: null);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("rent_installment_not_found", Parse(await other.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Equal(0, (await ReadAsync(world.Installment("ActiveOct"))).ReminderCount);
    }

    [Fact]
    public async Task Reminder_ALandlordOfTheSameOrgWithLimitedScope_Returns403OnAnotherOwnersProperty()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var colleague = world.ColleagueClient(factory);

        var denied = await colleague.PostAsync(Url(world.Installment("ActiveOct")), content: null);
        var own = await colleague.PostAsync(Url(world.Installment("ColleagueOct")), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Empty(EmailsTo(world, "Giulia", "Verdi"));
        Assert.Single(EmailsTo(world, "Valentina", "Marra"));
    }

    [Fact]
    public async Task Reminder_AnOrgWideManager_CanRemindAnyPropertyOfTheOrg()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var manager = world.ManagerClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync(Url(world.Installment("ActiveOct")), content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync(Url(world.Installment("ColleagueOct")), content: null)).StatusCode);
    }

    [Fact]
    public async Task Reminder_WithoutLogin_Returns401_AndWithoutTheLongTermRole_403()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var anonymous = factory.CreateClient();
        using var wrongRole = factory.CreateAuthenticatedClient(world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Url(world.Installment("ActiveOct")), content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.PostAsync(Url(world.Installment("ActiveOct")), content: null)).StatusCode);
        Assert.Empty(EmailsTo(world, "Giulia", "Verdi"));
    }

    // --- Many at once -----------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_SendsWhatItCanAndSkipsTheRestWithTheirCode_NeverFailingTheOthers()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        var unknown = Guid.NewGuid();
        var ids = new[]
        {
            world.Installment("ActiveAug"),
            world.Installment("ActiveOct"),
            world.Installment("ActiveSep"), // paid
            world.Installment("TransitoryOct"), // being processed
            unknown,
            world.Installment("ColleagueOct"), // a property of another owner
            world.Installment("ActiveAug"), // repeated
        };

        var response = await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { installmentIds = ids, note = "Grazie" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6, result.GetProperty("requested").GetInt32());
        Assert.Equal(
            [world.Installment("ActiveAug"), world.Installment("ActiveOct")],
            result.GetProperty("sent").EnumerateArray().Select(s => s.GetProperty("installmentId").GetGuid()).ToList());
        var skipped = result.GetProperty("skipped").EnumerateArray()
            .ToDictionary(s => s.GetProperty("installmentId").GetGuid(), s => s.GetProperty("code").GetString());
        Assert.Equal(4, skipped.Count);
        Assert.Equal("rent_installment_not_payable", skipped[world.Installment("ActiveSep")]);
        Assert.Equal("rent_installment_in_flight", skipped[world.Installment("TransitoryOct")]);
        Assert.Equal("rent_installment_not_found", skipped[unknown]);
        // Of another owner: the same as not there at all.
        Assert.Equal("rent_installment_not_found", skipped[world.Installment("ColleagueOct")]);

        // Two installments of the same lease, one email each; the note is in both.
        var emails = EmailsTo(world, "Giulia", "Verdi");
        Assert.Equal(2, emails.Count);
        Assert.All(emails, e => Assert.Contains("Grazie", e.Content.HtmlBody, StringComparison.Ordinal));
        Assert.Empty(EmailsTo(world, "Valentina", "Marra"));
        Assert.Equal(1, (await ReadAsync(world.Installment("ActiveAug"))).ReminderCount);
        Assert.Equal(1, (await ReadAsync(world.Installment("ActiveOct"))).ReminderCount);
    }

    [Fact]
    public async Task Bulk_TheSameBatchAgain_SkipsEverythingAsTooSoon()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        var ids = new[] { world.Installment("ActiveAug"), world.Installment("ActiveOct") };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { installmentIds = ids })).StatusCode);

        var again = await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { installmentIds = ids });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var result = Parse(await again.Content.ReadAsStringAsync());
        Assert.Empty(result.GetProperty("sent").EnumerateArray());
        Assert.All(result.GetProperty("skipped").EnumerateArray(), s => Assert.Equal("rent_reminder_too_soon", s.GetProperty("code").GetString()));
        Assert.Equal(2, EmailsTo(world, "Giulia", "Verdi").Count);
    }

    [Fact]
    public async Task Bulk_AnOrgWideManager_RemindsThePropertiesOfEveryOwner()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var manager = world.ManagerClient(factory);

        var response = await manager.PostAsJsonAsync(
            "/api/long-rent/rents/reminders", new { installmentIds = new[] { world.Installment("ActiveOct"), world.Installment("ColleagueOct") } });

        var result = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, result.GetProperty("sent").GetArrayLength());
        Assert.Equal(0, result.GetProperty("skipped").GetArrayLength());
    }

    [Fact]
    public async Task Bulk_ABatchOfNoneOfTooManyOrWithAnEmptyId_Returns400()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);

        var none = await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { installmentIds = Array.Empty<Guid>() });
        var missing = await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { note = "x" });
        var tooMany = await client.PostAsJsonAsync(
            "/api/long-rent/rents/reminders", new { installmentIds = Enumerable.Range(0, RentCharges.MaxBulkReminders + 1).Select(_ => Guid.NewGuid()).ToArray() });
        var empty = await client.PostAsJsonAsync("/api/long-rent/rents/reminders", new { installmentIds = new[] { Guid.Empty } });
        var atTheLimit = await client.PostAsJsonAsync(
            "/api/long-rent/rents/reminders", new { installmentIds = Enumerable.Range(0, RentCharges.MaxBulkReminders).Select(_ => Guid.NewGuid()).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("rent_reminder_batch_invalid", Parse(await none.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        // Exactly 50 is allowed; none of them exists, so all are skipped.
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        Assert.Equal(RentCharges.MaxBulkReminders, Parse(await atTheLimit.Content.ReadAsStringAsync()).GetProperty("skipped").GetArrayLength());
    }

    [Fact]
    public async Task Bulk_WithoutLogin_Returns401_AndWithoutTheLongTermRole_403()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var anonymous = factory.CreateClient();
        using var wrongRole = factory.CreateAuthenticatedClient(world.OwnerId, "PropertyOwner");
        var body = new { installmentIds = new[] { world.Installment("ActiveOct") } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/long-rent/rents/reminders", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.PostAsJsonAsync("/api/long-rent/rents/reminders", body)).StatusCode);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

    private IReadOnlyList<(string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template)> EmailsTo(
        LongRentWorld world, string first, string last) =>
        factory.Emails.Snapshot().Where(e => e.To == world.EmailOf(first, last)).ToList();

    private static string TokenOf(string html, Guid installmentId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, $@"/rent/pay/{installmentId:D}\?token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "the payment link of the installment is expected in the email");
        return match.Groups[1].Value;
    }

    private async Task<RentLedgerEntry> ReadAsync(Guid installmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == installmentId);
    }

    private static async Task<JsonElement> GetRegisterAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/long-rent/rents?month=2026-10&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement RegisterRow(JsonElement page, Guid installmentId) =>
        page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == installmentId);

    private async Task AddTenantAsync(LongRentWorld world, Guid leaseId, string first, string last, string email, int position)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Parties.Add(new Party
        {
            LeaseContractId = leaseId,
            Role = PartyRole.Tenant,
            Position = position,
            FirstName = first,
            LastName = last,
            FiscalCode = "BNCLGU90A01H501Z",
            Citizenship = "IT",
            ContactEmail = email,
        });
        await db.SaveChangesAsync();
    }

    private async Task AnonymizeTenantsAsync(Guid leaseId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var tenant in await db.Parties.Where(p => p.LeaseContractId == leaseId && p.Role == PartyRole.Tenant).ToListAsync())
            Casazen.Infrastructure.Services.LeasePartyPrivacyService.AnonymizeParty(tenant, LongRentAggregatesFactory.Now.UtcDateTime);
        await db.SaveChangesAsync();
    }

    private async Task SetPreviousLinkAsync(Guid installmentId, string tokenHash, DateTime requestedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.RentLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == installmentId);
        entry.PaymentTokenHash = tokenHash;
        entry.PaymentRequestedAt = requestedAt;
        await db.SaveChangesAsync();
    }
}
