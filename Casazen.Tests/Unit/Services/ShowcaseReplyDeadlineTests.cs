using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10c: the hour a supplier has to answer a request on measure, and what happens around it. The supplier is reminded at half of the hour and ten
/// minutes before the deadline, each once and never if it answered; a request nobody answered closes, the customer is told with the other suppliers
/// that could help (only then), the missed answer weighs on the supplier (and a silence of the customer never does); and the customer finds the same
/// alternatives when it looks the request up. The race with the supplier on PostgreSQL is the one of the upkeep of SP-10
/// (<c>ShowcaseBookingUpkeepPostgresTests</c>): here the save that finds the request changed is simulated.
/// </summary>
public class ShowcaseReplyDeadlineTests
{
    private const string SupplierEmail = "supplier@test.com";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The upkeep job of SP-10 with its second pass (the reminders), over the reliability of the kit: what the real job does every five minutes.</summary>
    private static ServiceRequestExpiryService Job(ServiceRequestScenario s) =>
        new(
            s.Db,
            new ServiceRequestAutoCancelService(
                s.Db,
                s.Notifier,
                Mock.Of<IFeatureFlags>(),
                NullLogger<ServiceRequestAutoCancelService>.Instance,
                s.Clock,
                reliability: s.Kit.Reliability),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock,
            reminders: s.Kit.Reminders);

    private static ShowcaseBookingCredentials CredentialsOf(ServiceRequest request) =>
        ShowcaseManageScenario.CredentialsOf(request.PublicCode!, ShowcaseScenario.CustomerEmail);

    private static FakeShowcaseAlternatives TwoAlternatives() =>
        new(
            FakeShowcaseAlternatives.Supplier("Pulizie Rossi", "pulizie-rossi"),
            FakeShowcaseAlternatives.Supplier("Casa Pronta", "casa-pronta"));

    private static Task<ServiceRequest> SendAnOfferAsync(ServiceRequestScenario s, Guid requestId) =>
        s.Service.SendOfferAsync(
            requestId,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestOfferCommand(
                [new ServiceRequestOfferLine("Posa del corrimano", 2, 4500)],
                ServiceRequestScenario.FridayAt10,
                ServiceRequestScenario.FridayAt10.AddHours(2),
                null));

    // ─── The reminders ───

    [Fact]
    public async Task Reminders_AtHalfOfTheHourAndTenMinutesBeforeTheDeadline_OnceEach_ToTheSupplierOnly()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);

        // Twenty-nine minutes in: too early.
        s.Clock.Advance(TimeSpan.FromMinutes(29));
        var early = await Job(s).RunAsync();
        Assert.Equal(0, early.RemindersSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        // Half of the hour: the first reminder, by e-mail and push, to the supplier.
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var half = await Job(s).RunAsync();
        Assert.Equal(1, half.RemindersSent);
        var halfMail = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((SupplierEmail, EmailTemplates.Names.SupplierInquiryReplyReminder), (halfMail.To, halfMail.Template));
        Assert.Contains("Promemoria", halfMail.Content.Subject);
        var halfPush = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestReplyReminder, halfPush.Payload.Type);
        Assert.Equal(PushDeliveryKeys.ServiceRequestReplyReminder(request.Id, "half"), halfPush.DeliveryKey);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), halfPush.Audience);
        // Like every push to a supplier org about a request: the tap opens the request in the supplier area of the app.
        Assert.Equal(PushRoutes.SupplierRequest(request.Id), halfPush.Payload.Route);
        var afterHalf = await s.ReadAsync(request.Id);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, afterHalf.ReplyReminderHalfSentAt);
        Assert.Null(afterHalf.ReplyReminderLastSentAt);

        // The same window again, as many runs as there are: nothing more.
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await Job(s).RunAsync();
        await Job(s).RunAsync();
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        // Ten minutes before the deadline: the last call, once.
        s.Clock.Advance(TimeSpan.FromMinutes(15));
        var last = await Job(s).RunAsync();
        var again = await Job(s).RunAsync();
        Assert.Equal(1, last.RemindersSent);
        Assert.Equal(0, again.RemindersSent);
        var lastMail = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((SupplierEmail, EmailTemplates.Names.SupplierInquiryReplyReminder), (lastMail.To, lastMail.Template));
        Assert.Contains("Ultimi minuti", lastMail.Content.Subject);
        Assert.Equal(PushDeliveryKeys.ServiceRequestReplyReminder(request.Id, "last"), Assert.Single(s.Pushes).DeliveryKey);

        // The request is still waiting, and the customer was told nothing.
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.NotNull(saved.ReplyReminderLastSentAt);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
    }

    [Fact]
    public async Task Reminders_NameTheComuneAndTheShortNameOnly_AndTheTimeToAnswerBy_NeverTheWordsOfTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(30));

        await Job(s).RunAsync();

        var mail = Assert.Single(s.Emails.Snapshot());
        var html = mail.Content.HtmlBody;
        Assert.Contains(ShowcaseScenario.City, html);
        Assert.Contains("Mario R.", html);
        // Eleven in London, thirteen in Rome: the deadline in Italian time.
        Assert.Contains("13:00", html);
        Assert.Contains("si chiude da sola", html);
        foreach (var secret in new[] { InquiryScenario.Words, ShowcaseScenario.CustomerName, ShowcaseScenario.CustomerEmail, ShowcaseScenario.CustomerPhone })
            Assert.DoesNotContain(secret, html);

        var push = Assert.Single(s.Pushes);
        Assert.Contains(ShowcaseScenario.City, push.Payload.Body);
        Assert.Contains("13:00", push.Payload.Body);
        Assert.DoesNotContain(ShowcaseScenario.CustomerName, push.Payload.Body);
    }

    [Fact]
    public async Task Reminders_ASupplierWhoAnswersInTime_IsNotRemindedAndTheRequestIsNotClosed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(20));
        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "Buongiorno, posso passare domani mattina.");
        s.ForgetNotifications();

        // Past the half, past the last call, past the hour it had: nothing to remind of and nothing to close.
        s.Clock.Advance(TimeSpan.FromMinutes(15));
        var middle = await Job(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(40));
        var after = await Job(s).RunAsync();

        Assert.Equal(0, middle.RemindersSent);
        Assert.Equal(0, after.RemindersSent);
        Assert.Equal(0, after.RequestsCancelled);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.ReplyReminderHalfSentAt);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task Reminders_ASupplierWhoAnswersAtTheSameMomentAsTheRun_WinsAndIsNotReminded_TheNextRunDoesItIfItDidNot()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(35));
        s.ForgetNotifications();

        // The save of the reminder finds the request changed by someone else (xmin): the winner's change stays and nobody is reminded.
        interceptor.Failure = new DbUpdateConcurrencyException("the supplier answered at the same moment");
        var raced = await Job(s).RunAsync();
        interceptor.Failure = null;

        Assert.Equal(0, raced.RemindersSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.Null((await s.ReadAsync(request.Id)).ReplyReminderHalfSentAt);

        // Nothing was marked, so a supplier that did not answer after all is still reminded, once.
        var next = await Job(s).RunAsync();
        Assert.Equal(1, next.RemindersSent);
        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierInquiryReplyReminder);
    }

    [Fact]
    public async Task Reminders_AnOfferWaitingForTheCustomer_IsNotAReminderForTheSupplier_ItsDeadlineIsTheCustomers()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        await SendAnOfferAsync(s, request.Id);
        s.ForgetNotifications();

        s.Clock.Advance(TimeSpan.FromMinutes(45));
        var run = await Job(s).RunAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Reminders_ASupplierThatIsNotActive_IsNotReminded_AndTheRequestIsNotMarked_SoItIsRemindedOnceItIsBack()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.Clock.Advance(TimeSpan.FromMinutes(35));
        s.ForgetNotifications();

        var suspended = await Job(s).RunAsync();
        Assert.Equal(0, suspended.RemindersSent);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Null((await s.ReadAsync(request.Id)).ReplyReminderHalfSentAt);

        await s.SetSupplierStatusAsync(SupplierStatus.Active);
        var back = await Job(s).RunAsync();
        Assert.Equal(1, back.RemindersSent);
    }

    [Fact]
    public async Task Reminders_ABookingOnAListedService_HasTheSuppliersOwnSetting_NotTheReminderOfTheHour()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.BookedAsync();
        s.ForgetNotifications();

        s.Clock.Advance(TimeSpan.FromMinutes(100));
        var run = await Job(s).RunAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Equal(0, run.RequestsCancelled);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    // ─── The silence ───

    [Fact]
    public async Task ARequestNobodyAnswered_Closes_TheSilenceIsWrittenAsNoReplyInTime_AndNobodyIsRemindedOfADeadlineThatPassed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));

        var run = await Job(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(0, run.RemindersSent);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, saved.CancellationReason);
        Assert.Null(saved.ResponseDueAt);

        // The event of the ranking: the supplier did not answer in the hour. Not as a backup, linked to the request.
        var silence = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal((SupplierReliabilityEventKind.NoReplyInTime, request.Id, false), (silence.Kind, silence.RequestId, silence.AsBackup));

        // The supplier is told, with the comune and not the customer, and that it costs points (the points are real: SP-REL).
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.ServiceRequestCancelledToSupplier, toSupplier.Template);
        Assert.Contains("punteggio di affidabilità", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain(ShowcaseScenario.CustomerName, toSupplier.Content.HtmlBody);
        // The customer is told the truth: the supplier did not answer in time, and nothing was paid.
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryExpired, toCustomer.Template);
        Assert.DoesNotContain("richiamat", toCustomer.Content.HtmlBody);
    }

    [Fact]
    public async Task ARunTwiceAfterTheSilence_WritesOneEvent_AndTellsNobodyTwice()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));
        await Job(s).RunAsync();
        s.ForgetNotifications();

        var second = await Job(s).RunAsync();

        Assert.Equal(0, second.RequestsCancelled);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Single(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task ARequestTheCustomerLetLapse_IsNotTheSuppliersSilence_NoEvent_NoAlternatives()
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        await SendAnOfferAsync(s, request.Id);
        s.ForgetNotifications();

        // The supplier answered with an offer; the customer had a day and let it pass.
        s.Clock.Advance(TimeSpan.FromHours(25));
        var run = await Job(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestCancellationReasons.QuoteNotAnswered, (await s.ReadAsync(request.Id)).CancellationReason);
        Assert.Empty(await s.ReliabilityEventsAsync());
        Assert.Empty(alternatives.Queries);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryOfferExpired, toCustomer.Template);
        Assert.DoesNotContain("Pulizie Rossi", toCustomer.Content.HtmlBody);
    }

    [Fact]
    public async Task ABookingOnAListedServiceThatLapsed_StillWritesNoEvent_ItsHoursTheSuppliersOwnSetting()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.BookedAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(181));

        var run = await Job(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    // ─── The alternatives ───

    [Fact]
    public async Task TheCustomerOfALapsedRequest_IsToldWithTheOtherSuppliers_AsTheShowcaseSaysThem()
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));

        await Job(s).RunAsync();

        var mail = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryExpired, mail.Template);
        var html = mail.Content.HtmlBody;
        Assert.Contains("Pulizie Rossi", html);
        Assert.Contains("Casa Pronta", html);
        Assert.Contains("/fornitori/pulizie-rossi", html);
        Assert.Contains("/fornitori/casa-pronta", html);
        Assert.Contains("Verificato", html);
        Assert.Contains("risponde in circa 30 min", html);
        Assert.Contains("Pulizia casa: da", html);
        Assert.Contains("a intervento", html);
        Assert.DoesNotContain("Per ora non abbiamo", html);

        // Looked for with the comune and the category of the request, never the supplier that did not answer.
        var saved = await s.ReadAsync(request.Id);
        var query = Assert.Single(alternatives.Queries);
        Assert.Equal((s.SupplierOrgId, saved.Category, saved.LocationComuneIstat, ShowcaseScenario.City), (query.ExcludedSupplierOrgId, query.Category, query.ComuneIstat, query.City));
    }

    [Fact]
    public async Task TheCustomerOfALapsedBooking_IsToldWithTheOtherSuppliers_Too()
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(181));
        s.ForgetNotifications();

        await Job(s).RunAsync();

        var mail = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingExpired, mail.Template);
        Assert.Contains("Pulizie Rossi", mail.Content.HtmlBody);
        Assert.Equal(request.Category, Assert.Single(alternatives.Queries).Category);
    }

    [Fact]
    public async Task WhenThereIsNoOtherSupplierToSuggest_TheMailSaysSoPlainly_AndPromisesNothing()
    {
        var alternatives = new FakeShowcaseAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));

        await Job(s).RunAsync();

        var html = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail).Content.HtmlBody;
        Assert.Contains("Per ora non abbiamo altri fornitori da proporti", html);
        Assert.DoesNotContain("Questi fornitori", html);
        Assert.DoesNotContain("richiamat", html);
    }

    [Fact]
    public async Task WhenTheAlternativesCannotBeLookedFor_TheRequestStillClosesAndTheCustomerIsToldWithoutThem()
    {
        var alternatives = TwoAlternatives();
        alternatives.Failure = new InvalidOperationException("the ranking is down");
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));

        var run = await Job(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
        var html = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail).Content.HtmlBody;
        // Nothing is said about alternatives that were not looked for (decision D24: no sentence that may not be true).
        Assert.DoesNotContain("Questi fornitori", html);
        Assert.DoesNotContain("Per ora non abbiamo", html);
    }

    [Fact]
    public async Task TheLookup_OfALapsedRequest_HasTheAlternatives_AndSoDoesTheirOwnCall()
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));
        await Job(s).RunAsync();

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        var own = await s.Kit.Manager.ListAlternativesAsync(CredentialsOf(request));

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(new[] { "Pulizie Rossi", "Casa Pronta" }, view.Alternatives!.Select(a => a.Name).ToArray());
        Assert.Equal(new[] { "Pulizie Rossi", "Casa Pronta" }, own.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task TheLookup_OfALapsedRequestWithNoOneToSuggest_HasAnEmptyList_NotNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: new FakeShowcaseAlternatives());
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));
        await Job(s).RunAsync();

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));

        Assert.NotNull(view.Alternatives);
        Assert.Empty(view.Alternatives);
    }

    [Fact]
    public async Task TheAlternatives_AreNotOfferedForARequestInAnyOtherState_TheyAreNotAListOfSuppliers()
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var open = await s.InquiredAsync(supplier);
        var cancelled = await s.InquiredAsync(supplier, email: "altro@example.com");
        await s.Kit.Manager.CancelAsync(ShowcaseManageScenario.CredentialsOf(cancelled.PublicCode!, "altro@example.com"), "Ho trovato un altro fornitore");

        var openView = await s.Kit.Manager.LookupAsync(CredentialsOf(open));
        var cancelledView = await s.Kit.Manager.LookupAsync(ShowcaseManageScenario.CredentialsOf(cancelled.PublicCode!, "altro@example.com"));
        var openList = await s.Kit.Manager.ListAlternativesAsync(CredentialsOf(open));
        var cancelledList = await s.Kit.Manager.ListAlternativesAsync(ShowcaseManageScenario.CredentialsOf(cancelled.PublicCode!, "altro@example.com"));

        Assert.Null(openView.Alternatives);
        Assert.Null(cancelledView.Alternatives);
        Assert.Empty(openList);
        Assert.Empty(cancelledList);
        Assert.Empty(alternatives.Queries);
    }

    [Theory]
    [InlineData("vetrina-test", "AAAAA-BBBBB", ShowcaseScenario.CustomerEmail)] // a code that is nobody's
    [InlineData("altro-fornitore", null, ShowcaseScenario.CustomerEmail)] // another supplier's slug
    [InlineData("vetrina-test", null, "chi.sono@example.com")] // not the address of the request
    [InlineData(null, null, null)]
    public async Task TheirOwnCall_AnythingThatDoesNotIdentifyARequest_IsTheSameNotFound(string? slug, string? code, string? email)
    {
        var alternatives = TwoAlternatives();
        using var s = await ServiceRequestScenario.CreateAsync(alternatives: alternatives);
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(Hour + TimeSpan.FromMinutes(1));
        await Job(s).RunAsync();
        // The job itself looked for the alternatives once, to tell the customer when it closed the request: only what the call adds counts.
        var queriesOfTheJob = alternatives.Queries.Count;

        var credentials = new ShowcaseBookingCredentials(slug, code ?? ShowcaseManageScenario.CredentialsOf(request.PublicCode!).Code, email);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.ListAlternativesAsync(credentials));
        Assert.Equal(ShowcaseBookingManagementErrors.NotFound, error.Code);
        Assert.Equal(queriesOfTheJob, alternatives.Queries.Count);
    }

    // ─── The mail ───

    [Fact]
    public void TheMailOfALapsedRequest_InEnglish_ListsTheSuppliersWithTheirLinks_AndEncodesTheirNames()
    {
        var supplier = FakeShowcaseAlternatives.Supplier("Rossi & <Figli>", "rossi-figli");

        var content = EmailTemplates.SupplierInquiryExpired(
            new CultureInfo("en"),
            "Mario",
            "Idraulica Verdi",
            "https://site.test/fornitori/verdi",
            [new EmailTemplates.EmailAlternative(supplier, "https://site.test/fornitori/rossi-figli")]);

        var html = content.HtmlBody;
        Assert.Contains("https://site.test/fornitori/rossi-figli", html);
        Assert.Contains("Rossi &amp; &lt;Figli&gt;", html);
        Assert.DoesNotContain("<Figli>", html);
        Assert.Contains("Verified", html);
        Assert.Contains("per job", html);
        Assert.Contains("These suppliers in your area", html);
    }

    [Theory]
    [InlineData(30, "risponde in circa 30 min")]
    [InlineData(119, "risponde in circa 119 min")]
    [InlineData(120, "risponde in circa 2 h")]
    [InlineData(180, "risponde in circa 3 h")]
    public void WhatIsSaidAfterTheName_TheTypicalTimeToAnswer_IsInMinutesThenInHours(int minutes, string expected)
    {
        var supplier = FakeShowcaseAlternatives.Supplier("Pulizie Rossi", "pulizie-rossi") with { MedianResponseMinutes = minutes };

        Assert.Contains(expected, EmailTemplates.AlternativeDetail(new CultureInfo("it"), supplier));
    }

    [Fact]
    public void WhatIsSaidAfterTheName_OnlyWhatIsKnown_NothingForAnUnverifiedSupplierWithNoMeasureAndNoService()
    {
        var supplier = new ShowcaseAlternative("Pulizie Rossi", "pulizie-rossi", null, null, Verified: false, MedianResponseMinutes: null, null);

        Assert.Equal(string.Empty, EmailTemplates.AlternativeDetail(new CultureInfo("it"), supplier));
    }

    [Fact]
    public void WhatIsSaidAfterTheName_AServiceOnQuoteHasNoPrice_AndTheUnitIsTold()
    {
        var onQuote = FakeShowcaseAlternatives.Supplier("Pulizie Rossi", "pulizie-rossi") with
        {
            Service = new ShowcaseAlternativeService("su-misura", "Pulizie su misura", null, SupplierServicePriceUnit.PerJob, RequiresQuote: true),
        };
        var perHour = FakeShowcaseAlternatives.Supplier("Casa Pronta", "casa-pronta") with
        {
            Service = new ShowcaseAlternativeService("a-ore", "Pulizie a ore", 3500, SupplierServicePriceUnit.PerHour, RequiresQuote: false),
        };

        Assert.Contains("Pulizie su misura: su preventivo", EmailTemplates.AlternativeDetail(new CultureInfo("it"), onQuote));
        Assert.Contains("all'ora", EmailTemplates.AlternativeDetail(new CultureInfo("it"), perHour));
    }
}
