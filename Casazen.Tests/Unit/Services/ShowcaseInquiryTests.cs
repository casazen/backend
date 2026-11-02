using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10b: the request on measure from a supplier's public showcase — the customer describes what it needs, checks its e-mail address, and
/// only then the supplier has a request (<c>Kind = Custom</c>, <c>Richiesto</c>, no time) whose first message is what the customer wrote.
/// The rules that cost privacy or trust when they are wrong are the ones tested: nothing reaches the supplier before the check, never a
/// word of the customer (nor its name, e-mail or phone) goes in a mail or a push to the supplier, a link that is not the customer's is one
/// 404, the check can be repeated without doing anything twice, and one address cannot fill a supplier's inbox. The lock that makes two
/// checks at once end in one request needs PostgreSQL (the booking's own tests cover the same lock); here the rules run in sequence.
/// </summary>
public class ShowcaseInquiryTests
{
    private const string Words = "Vorrei far sistemare la ringhiera del balcone e controllare la porta d'ingresso che si blocca.";

    // ─── Before the check ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_KeepsWhatTheCustomerWrote_OnlyTheCustomerIsWrittenTo_TheTokenIsInTheFragmentOfTheLink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        s.ForgetNotifications();

        var result = await s.Kit.Inquiry.CreateAsync(supplier, Input());

        var now = s.Clock.GetUtcNow().UtcDateTime;
        var inquiry = Assert.Single(await InquiriesAsync(s));
        Assert.Equal(result.Id, inquiry.Id);
        Assert.Equal(now.AddMinutes(30), result.ExpiresAt);
        Assert.Equal(result.ExpiresAt, inquiry.ExpiresAt);
        Assert.Null(inquiry.ConsumedAt);
        Assert.Null(inquiry.ServiceRequestId);
        // The column encrypts it (EncryptedColumnsTests); here it is only there to be read when the address is checked.
        Assert.Contains("ringhiera", inquiry.Payload);

        // Decision D34: nothing a stranger types reaches the supplier before the address is checked.
        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.Empty(s.Pushes);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryVerification, email.Template);
        // The id is in the query and the token in the fragment, which a browser never sends to a server: it is in no access log.
        Assert.Matches($"/fornitori/{ShowcaseScenario.Slug}/conferma\\?inquiry={result.Id:D}#token=[A-Za-z0-9_-]{{43}}", email.Content.HtmlBody);
        Assert.DoesNotContain("ringhiera", email.Content.Subject + email.Content.HtmlBody);
    }

    [Fact]
    public async Task Create_ARetryWithTheSameClientRequestId_IsTheSameInquiry_AndSendsNoSecondEmail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var clientRequestId = Guid.NewGuid();
        var first = await s.Kit.Inquiry.CreateAsync(supplier, Input(clientRequestId: clientRequestId));
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var again = await s.Kit.Inquiry.CreateAsync(supplier, Input(clientRequestId: clientRequestId));

        Assert.Equal(first, again);
        Assert.Single(await InquiriesAsync(s));
        Assert.Empty(s.Emails.Snapshot());

        // Once it lapsed (and the upkeep job has not yet deleted it) the same id starts again, with a new link.
        s.Clock.Advance(TimeSpan.FromMinutes(40));
        var afterTheTime = await s.Kit.Inquiry.CreateAsync(supplier, Input(clientRequestId: clientRequestId));

        Assert.NotEqual(first.Id, afterTheTime.Id);
        Assert.Equal(afterTheTime.Id, Assert.Single(await InquiriesAsync(s)).Id);
        Assert.Single(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Create_TheFourthRequestOfTheSameAddress_IsRefused_AndTheOnesTheSupplierDidNotAnswerCountToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var first = await CreatedAsync(s, supplier);
        await CreatedAsync(s, supplier);
        await CreatedAsync(s, supplier);
        s.ForgetNotifications();

        await Assert.ThrowsAsync<ShowcaseBookingTooManyHoldsException>(async () => await s.Kit.Inquiry.CreateAsync(supplier, Input()));
        // The spelling of the address does not matter: capitals are the same address.
        await Assert.ThrowsAsync<ShowcaseBookingTooManyHoldsException>(
            async () => await s.Kit.Inquiry.CreateAsync(supplier, Input(email: "MARIO.ROSSI@Example.com")));
        Assert.Equal(3, (await InquiriesAsync(s)).Count);
        Assert.Empty(s.Emails.Snapshot());

        // Checking one does not free the place: the request it became waits for the supplier's answer, and counts as much as before.
        await s.Kit.Inquiry.ConfirmEmailAsync(supplier, first.Result.Id, first.Token);
        s.ForgetNotifications();
        await Assert.ThrowsAsync<ShowcaseBookingTooManyHoldsException>(async () => await s.Kit.Inquiry.CreateAsync(supplier, Input()));

        // Another person is not held back by it.
        await s.Kit.Inquiry.CreateAsync(supplier, Input(email: "altra.persona@example.com"));
        Assert.Single(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Create_RefusesWhatTheSupplierCannotServe_AndStoresNothingNorSendsAnything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        s.ForgetNotifications();

        var outside = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Inquiry.CreateAsync(supplier, Input(change: i => i with { City = "Napoli" })));
        var unknownService = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.CreateAsync(supplier, Input(service: "un-servizio-che-non-esiste")));

        Assert.Equal("supplier_booking_outside_zone", outside.Code);
        Assert.Equal("supplier_service_not_found", unknownService.Code);

        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.CreateAsync(await s.SupplierAsync(), Input()));
        Assert.Equal("not_found", suspended.Code);

        Assert.Empty(await InquiriesAsync(s));
        Assert.Empty(s.Emails.Snapshot());
    }

    // ─── The check of the address ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmEmail_CreatesTheRequestOnMeasure_WithoutATime_AndTheWordsAreItsFirstMessage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(10));

        var confirmation = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);

        var now = s.Clock.GetUtcNow().UtcDateTime;
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(ServiceRequestKind.Custom, request.Kind);
        Assert.Equal(ServiceRequestStatus.Richiesto, request.Status);
        Assert.Equal(ServiceRequestRentalContext.Showcase, request.RentalContext);
        Assert.Equal(ServiceRequestSource.Showcase, request.Source);
        Assert.Equal(s.SupplierOrgId, request.OrgId);
        Assert.Equal(s.SupplierOrgId, request.SupplierOrgId);
        Assert.Null(request.PropertyId);
        Assert.Null(request.BookingId);
        Assert.Null(request.ScheduledStartUtc);
        Assert.Null(request.ScheduledEndUtc);
        Assert.Null(request.EstimatedAmountCents);
        Assert.Null(request.ServiceListingId);
        Assert.Equal(customer.Id, request.CustomerId);
        Assert.Equal(ShowcaseScenario.City, request.LocationCity);
        Assert.Null(request.LocationAddress);
        Assert.True(ServiceCategories.IsKnown(request.Category));
        Assert.Equal(string.Empty, request.Notes);
        // The hour starts when the address is checked, not when the customer wrote.
        Assert.Equal(now.AddMinutes(60), request.ResponseDueAt);

        var message = Assert.Single(await MessagesAsync(s, request.Id));
        Assert.Equal(ServiceRequestMessageAuthor.Customer, message.Author);
        Assert.Equal(Words, message.Body);
        Assert.Equal(s.SupplierOrgId, message.OrgId);
        Assert.Equal(now, message.CreatedAt);
        Assert.Null(message.ReadAt);

        Assert.Equal(
            new ShowcaseInquiryConfirmation(request.PublicCode!, ServiceRequestStatus.Richiesto, null, request.ResponseDueAt, AlreadyConfirmed: false),
            confirmation);

        // The inquiry becomes the request: its payload goes (the words are the message now), the token hash stays for the replay.
        var consumed = Assert.Single(await InquiriesAsync(s));
        Assert.Equal(now, consumed.ConsumedAt);
        Assert.Equal(request.Id, consumed.ServiceRequestId);
        Assert.Null(consumed.Payload);
        Assert.Equal(ShowcaseBookingTokens.Hash(token), consumed.TokenHash);

        // The customer of the supplier is the one of the bookings: name, e-mail, phone, language and the consent given.
        Assert.Equal(ShowcaseScenario.CustomerName, customer.FullName);
        Assert.Equal(ShowcaseScenario.CustomerEmail, customer.Email);
        Assert.Equal("+393331234567", customer.Phone);
        Assert.Equal(ServiceRequestTestKit.PrivacyNoticeVersion, customer.PrivacyNoticeVersion);
        Assert.Equal(ShowcaseScenario.ConsentIp, customer.ConsentIp);
    }

    [Fact]
    public async Task ConfirmEmail_ARequestAboutAServiceKeepsItsNameAndCategory()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier, Input(service: await s.ListingSlugAsync()));

        var confirmation = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);

        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Equal(s.ListingId, request.ServiceListingId);
        Assert.Equal(ServiceRequestScenario.ServiceName, request.ServiceNameSnapshot);
        Assert.Equal(ServiceCategories.Cleaning, request.Category);
        Assert.Null(request.EstimatedAmountCents);
        Assert.Equal(ServiceRequestScenario.ServiceName, confirmation.ServiceName);
    }

    [Fact]
    public async Task ConfirmEmail_TheSupplierIsToldTheComuneAndAShortName_NeverTheWordsTheNameTheAddressOrThePhone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier);
        s.ForgetNotifications();

        var confirmation = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);

        var emails = s.Emails.Snapshot();
        Assert.Equal(2, emails.Count);

        var toSupplier = Assert.Single(emails, e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.SupplierInquiryNewRequest, toSupplier.Template);
        Assert.Contains(ShowcaseScenario.City, toSupplier.Content.Subject);
        Assert.Contains("Mario R.", toSupplier.Content.HtmlBody);
        var everything = toSupplier.Content.Subject + toSupplier.Content.HtmlBody;
        foreach (var secret in new[] { "Rossi", "ringhiera", "balcone", ShowcaseScenario.CustomerEmail, "3331234567", ShowcaseScenario.ConsentIp })
            Assert.DoesNotContain(secret, everything);

        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestCreated, push.Payload.Type);
        var pushText = push.Payload.Title + push.Payload.Body;
        Assert.Contains(ShowcaseScenario.City, pushText);
        foreach (var secret in new[] { "Mario", "ringhiera", "balcone" })
            Assert.DoesNotContain(secret, pushText);

        // The customer gets the receipt, with the code to find the request again; not its own words either.
        var receipt = Assert.Single(emails, e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryReceipt, receipt.Template);
        Assert.Contains(BookingCodes.Format(confirmation.PublicCode), receipt.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test/richiesta", receipt.Content.HtmlBody);
        Assert.DoesNotContain("ringhiera", receipt.Content.HtmlBody);
    }

    [Fact]
    public async Task ConfirmEmail_TheReceiptIsInTheLanguageOfTheCustomer_TheSupplierAlwaysInItalian()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier, Input(change: i => i with { Locale = "en" }));
        s.ForgetNotifications();

        await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);

        Assert.Contains("Request sent to Supplier Srl", Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail).Content.Subject);
        Assert.Contains("Nuova richiesta su misura", Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com").Content.Subject);
    }

    [Fact]
    public async Task ConfirmEmail_ASecondClick_AnswersTheSame_AndDoesNothingAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier);
        var first = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token);

        Assert.Equal(first with { AlreadyConfirmed = true }, second);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Single(await MessagesAsync(s, request.Id));
        Assert.Single(await s.CustomersAsync());
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task ConfirmEmail_ALinkThatIsNotTheirs_IsOneAnswer_AndNothingHappens()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (inquiry, token) = await CreatedAsync(s, supplier);

        var wrongToken = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, token + "x"));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(supplier, Guid.NewGuid(), token));
        var ofAnother = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(other.Profile, inquiry.Id, token));
        var noToken = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inquiry.Id, null));

        foreach (var ex in new[] { wrongToken, unknown, ofAnother, noToken })
        {
            Assert.Equal("supplier_booking_link_invalid", ex.Code);
            Assert.Equal(wrongToken.Message, ex.Message);
        }

        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.Null(Assert.Single(await InquiriesAsync(s)).ConsumedAt);
        Assert.Empty(await InquiriesAsync(s, other.OrgId));
    }

    [Fact]
    public async Task ConfirmEmail_AfterTheTime_Is409_ASupplierSuspendedMeanwhile_Is422_AndNoRequestIsCreated()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var late = await CreatedAsync(s, supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(31));

        var expired = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(supplier, late.Result.Id, late.Token));
        Assert.Equal("supplier_booking_link_expired", expired.Code);

        var inTime = await CreatedAsync(s, supplier, Input(email: "altra.persona@example.com"));
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.ForgetNotifications();

        var suspended = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Inquiry.ConfirmEmailAsync(supplier, inTime.Result.Id, inTime.Token));
        Assert.Equal("supplier_booking_supplier_unavailable", suspended.Code);

        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.Empty(await s.CustomersAsync());
        Assert.Empty(s.Emails.Snapshot());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ShowcaseInquiryInput Input(
        string? email = ShowcaseScenario.CustomerEmail,
        Guid? clientRequestId = null,
        string? service = null,
        Func<ShowcaseInquiryInput, ShowcaseInquiryInput>? change = null)
    {
        var input = new ShowcaseInquiryInput(
            clientRequestId ?? Guid.NewGuid(),
            service,
            null,
            ShowcaseScenario.City,
            Words,
            ShowcaseScenario.CustomerName,
            email,
            ShowcaseScenario.CustomerPhone,
            "it",
            true,
            ServiceRequestTestKit.PrivacyNoticeVersion,
            ShowcaseScenario.ConsentIp);
        return change is null ? input : change(input);
    }

    /// <summary>An inquiry made through the service: the result, and the token that the e-mail to the customer carries.</summary>
    private static async Task<(ShowcaseInquiryResult Result, string Token)> CreatedAsync(
        ServiceRequestScenario s,
        SupplierProfile supplier,
        ShowcaseInquiryInput? input = null)
    {
        s.ForgetNotifications();
        var result = await s.Kit.Inquiry.CreateAsync(supplier, input ?? Input());
        var email = s.Emails.Snapshot().Last(e => e.Template == EmailTemplates.Names.SupplierInquiryVerification);
        var match = Regex.Match(email.Content.HtmlBody, $"inquiry={result.Id:D}#token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "the verification e-mail has no link with the inquiry and the token");
        return (result, match.Groups[1].Value);
    }

    private static Task<List<ShowcaseInquiry>> InquiriesAsync(ServiceRequestScenario s, Guid? supplierOrgId = null) =>
        s.Db.ShowcaseInquiries.AsNoTracking().Where(i => i.OrgId == (supplierOrgId ?? s.SupplierOrgId)).OrderBy(i => i.CreatedAt).ToListAsync();

    private static Task<List<ServiceRequestMessage>> MessagesAsync(ServiceRequestScenario s, Guid requestId) =>
        s.Db.ServiceRequestMessages
            .AsNoTracking()
            .Where(m => m.OrgId == s.SupplierOrgId && m.ServiceRequestId == requestId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
}
