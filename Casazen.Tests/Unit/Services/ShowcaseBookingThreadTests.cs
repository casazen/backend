using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.DTOs.Supplier;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10b: the conversation of a request on measure, as its customer has it in its own area (code and e-mail address, no account). The rules
/// that cost privacy, trust or a request when they are wrong are the ones tested: the view of any other booking is exactly what it was, the
/// customer reads the supplier's messages and marks them read, writes only in a request on measure of its own and while the thread is open,
/// the supplier is told with the comune and "Nome C." and never with the words, closing the request is free and tells the supplier without a
/// day and a time, and the retention deletes the thread of a request that is over.
/// </summary>
public class ShowcaseBookingThreadTests
{
    private const string Reply = "Certo, posso passare giovedì mattina per un sopralluogo gratuito.";
    private const string Question = "Perfetto, va bene anche venerdì alle nove? Porto io la scala.";

    private static readonly RetentionPeriodOptions TwoYears = new() { Years = 2, Source = "Art. 2220 c.c. (test)" };

    private static ShowcaseBookingCredentials Credentials(ServiceRequest request, string email = ShowcaseScenario.CustomerEmail) =>
        new(ShowcaseScenario.Slug, BookingCodes.Format(request.PublicCode!), email);

    // ─── Reading ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_OfARequestOnMeasure_ShowsTheThread_MarksTheSuppliersMessagesRead_AndSaysWhereTheNegotiationIs()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);

        var waiting = await s.Kit.Manager.LookupAsync(Credentials(request));

        Assert.Equal(ServiceRequestKind.Custom, waiting.Kind);
        Assert.Equal(ServiceRequestNegotiation.AwaitingSupplier, waiting.Negotiation);
        Assert.Equal(request.ResponseDueAt, waiting.RespondBy);
        Assert.True(waiting.Actions.CanCancel);
        Assert.False(waiting.Actions.CanReschedule);
        Assert.False(waiting.Actions.CanRespondToProposal);
        Assert.True(waiting.CancellationTerms.IsFree);
        var first = Assert.Single(Assert.IsType<ServiceRequestThread>(waiting.Thread).Messages);
        Assert.Equal(ServiceRequestMessageAuthor.Customer, first.Author);
        Assert.Equal(InquiryScenario.Words, first.Body);

        // The supplier answers: the customer sees it as new, then as read, and the hour is no longer its concern. The clock of the test is
        // frozen: the answer comes a minute after the customer's words, otherwise both messages have the same instant and the thread lists
        // them in the order of their random ids.
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, Reply);
        s.Clock.Advance(TimeSpan.FromMinutes(2));

        var talking = await s.Kit.Manager.LookupAsync(Credentials(request));

        Assert.Equal(ServiceRequestNegotiation.InConversation, talking.Negotiation);
        Assert.Null(talking.RespondBy);
        var thread = Assert.IsType<ServiceRequestThread>(talking.Thread);
        Assert.Equal([ServiceRequestMessageAuthor.Customer, ServiceRequestMessageAuthor.Supplier], thread.Messages.Select(m => m.Author).ToArray());
        Assert.Equal(Reply, thread.Messages[1].Body);
        Assert.Null(thread.Messages[1].ReadAt);
        Assert.True(thread.CanWrite);

        var again = await s.Kit.Manager.LookupAsync(Credentials(request));
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, Assert.IsType<ServiceRequestThread>(again.Thread).Messages[1].ReadAt);
    }

    [Fact]
    public async Task Lookup_OfAnyOtherBooking_IsWhatItAlwaysWas_NoKindNoThreadNoNegotiation_NotEvenInTheJson()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (booking, _) = await s.BookedAsync();

        var view = await s.Kit.Manager.LookupAsync(Credentials(booking));

        Assert.Equal(ServiceRequestKind.Standard, view.Kind);
        Assert.Null(view.Thread);
        Assert.Null(view.Negotiation);
        // At the root of the answer (the lines of the price have a kind of their own): the three members are not there at all.
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(PublicSupplierBookingViewMapper.ToDto(view), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        foreach (var member in new[] { "kind", "thread", "negotiation" })
            Assert.False(plain.RootElement.TryGetProperty(member, out _), member);

        // And the one on measure has them.
        var onMeasure = await s.InquiredAsync(email: "altra.persona@example.com");
        var customJson = JsonSerializer.Serialize(
            PublicSupplierBookingViewMapper.ToDto(await s.Kit.Manager.LookupAsync(Credentials(onMeasure, "altra.persona@example.com"))),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"kind\":\"Custom\"", customJson);
        Assert.Contains("\"negotiation\":\"AwaitingSupplier\"", customJson);
        Assert.Contains("\"author\":\"customer\"", customJson);
    }

    // ─── Writing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostMessage_AddsTheCustomersMessage_TellsTheSupplierWithTheComuneAndAShortName_NeverTheWords()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        // The supplier was told of the first message when the address was checked; a follow-up inside ten minutes tells nobody again.
        s.Clock.Advance(TimeSpan.FromMinutes(11));

        var view = await s.Kit.Manager.PostMessageAsync(Credentials(request), "  " + Question + "  ");

        var thread = Assert.IsType<ServiceRequestThread>(view.Thread);
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal(ServiceRequestMessageAuthor.Customer, thread.Messages[1].Author);
        Assert.Equal(Question, thread.Messages[1].Body);
        var stored = await s.ThreadAsync(request.Id);
        Assert.Equal(s.SupplierOrgId, stored[1].OrgId);

        // The clock of the supplier is its own: the customer's message does not touch it.
        var after = await s.ReadAsync(request.Id);
        Assert.Equal(request.ResponseDueAt, after.ResponseDueAt);
        Assert.Equal(ServiceRequestStatus.Richiesto, after.Status);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryMessage, email.Template);
        Assert.Contains(ShowcaseScenario.City, email.Content.Subject);
        Assert.Contains("Mario R.", email.Content.HtmlBody);
        var everything = email.Content.Subject + email.Content.HtmlBody;
        foreach (var secret in new[] { "Rossi", "venerdì", "scala", "ringhiera", ShowcaseScenario.CustomerEmail, "3331234567" })
            Assert.DoesNotContain(secret, everything);
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task PostMessage_ALineAfterAnother_IsOneEmailToTheSupplier_TheNextAfterTheQuietPeriodIsAnother()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(11));

        await s.Kit.Manager.PostMessageAsync(Credentials(request), "Buongiorno, ho una domanda sulla scala.");
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.Kit.Manager.PostMessageAsync(Credentials(request), "Si può fare anche con la mia?");
        Assert.Single(s.Emails.Snapshot());

        s.Clock.Advance(TimeSpan.FromMinutes(11));
        await s.Kit.Manager.PostMessageAsync(Credentials(request), "Resto in attesa di una risposta.");

        Assert.Equal(2, s.Emails.Snapshot().Count);
        Assert.Equal(4, (await s.ThreadAsync(request.Id)).Count);
    }

    [Fact]
    public async Task PostMessage_RefusesWhatItShould_AndAddsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        var (booking, _) = await s.BookedAsync();
        s.ForgetNotifications();

        // Empty, too long, with a character a message cannot have: one answer.
        foreach (var body in new[] { null, "   ", new string('x', ShowcaseInquiryLimits.MessageMaxLength + 1), "con un carattere" + (char)0 + " nascosto" })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.PostMessageAsync(Credentials(request), body));
            Assert.Equal("service_request_message_invalid", invalid.Code);
        }

        // Who is not the customer, and a booking that is not a request on measure, are the same 404.
        var wrongAddress = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Manager.PostMessageAsync(Credentials(request, "qualcun.altro@example.com"), Question));
        var aBooking = await Assert.ThrowsAsync<NotFoundException>(
            async () => await s.Kit.Manager.PostMessageAsync(Credentials(booking), Question));
        Assert.Equal("supplier_booking_not_found", wrongAddress.Code);
        Assert.Equal(wrongAddress.Code, aBooking.Code);
        Assert.Equal(wrongAddress.Message, aBooking.Message);

        // A supplier that is not active answers nobody.
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.PostMessageAsync(Credentials(request), Question));
        Assert.Equal("supplier_booking_supplier_unavailable", suspended.Code);
        Assert.False(Assert.IsType<ServiceRequestThread>((await s.Kit.Manager.LookupAsync(Credentials(request))).Thread).CanWrite);

        Assert.Single(await s.ThreadAsync(request.Id));
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task PostMessage_AClosedThread_Is409_AFullOne_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var refused = await s.InquiredAsync(supplier);
        var crowded = await s.InquiredAsync(supplier, "altra.persona@example.com");

        await s.Service.RejectAsync(refused.Id, s.SupplierOrgId, "Non copro questo tipo di lavoro");
        var closed = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Manager.PostMessageAsync(Credentials(refused), Question));
        Assert.Equal("service_request_thread_closed", closed.Code);

        var now = s.Clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < ShowcaseInquiryLimits.MaxMessagesPerRequest - 1; i++)
        {
            s.Db.ServiceRequestMessages.Add(new ServiceRequestMessage
            {
                ServiceRequestId = crowded.Id,
                OrgId = s.SupplierOrgId,
                Author = i % 2 == 0 ? ServiceRequestMessageAuthor.Supplier : ServiceRequestMessageAuthor.Customer,
                Body = $"messaggio numero {i}",
                CreatedAt = now.AddSeconds(-i - 1),
            });
        }

        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var full = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Manager.PostMessageAsync(Credentials(crowded, "altra.persona@example.com"), Question));
        Assert.Equal("service_request_thread_full", full.Code);
        Assert.Equal(ShowcaseInquiryLimits.MaxMessagesPerRequest, (await s.ThreadAsync(crowded.Id)).Count);
    }

    // ─── Closing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_OfARequestOnMeasure_ClosesItForFree_TellsTheSupplierWithoutADay_AndTheCustomerGetsNoReceipt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, Reply);
        s.ForgetNotifications();

        var view = await s.Kit.Manager.CancelAsync(Credentials(request), "Ho trovato un altro fornitore");

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(ServiceRequestNegotiation.Closed, view.Negotiation);
        Assert.Equal(ServiceRequestActorParty.Customer, view.Cancellation!.By);
        Assert.False(Assert.IsType<ServiceRequestThread>(view.Thread).CanWrite);

        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.SupplierInquiryClosedByCustomer, toSupplier.Template);
        Assert.Contains("Ho trovato un altro fornitore", toSupplier.Content.HtmlBody);
        Assert.Contains("Mario R.", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.Subject + toSupplier.Content.HtmlBody);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer);
        // The customer closed it itself: no receipt that speaks of a day and a time the request never had.
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);

        // A closed request takes no more messages, from either party.
        await Assert.ThrowsAsync<DomainConflictException>(async () => await s.Kit.Manager.PostMessageAsync(Credentials(request), Question));
        await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, Reply));
    }

    // ─── Retention ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRetention_TheThreadOfARequestOnMeasureThatIsOver_IsDeletedAfterThePeriod_TheOpenOneAndTheYoungOneAreKept()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var closed = await s.InquiredAsync(supplier);
        var open = await s.InquiredAsync(supplier, "altra.persona@example.com");
        await s.Service.RejectAsync(closed.Id, s.SupplierOrgId, "Non copro questo tipo di lavoro");

        // Not yet: the period has not ended.
        s.Clock.Advance(TimeSpan.FromDays((365 * 2) - 30));
        await Service(s).ApplyRetentionAsync();
        Assert.Single(await s.ThreadAsync(closed.Id));

        s.Clock.Advance(TimeSpan.FromDays(60));
        await Service(s).ApplyRetentionAsync();

        // The words are gone; the request stays for the supplier's accounts. The request still open keeps its thread.
        Assert.Empty(await s.ThreadAsync(closed.Id));
        var kept = await s.ReadAsync(closed.Id);
        Assert.Equal(ServiceRequestStatus.Rifiutato, kept.Status);
        Assert.Equal(ShowcaseScenario.City, kept.LocationCity);
        Assert.Single(await s.ThreadAsync(open.Id));

        // A second run finds nothing more.
        await Service(s).ApplyRetentionAsync();
        Assert.Single(await s.ThreadAsync(open.Id));
        Assert.Empty(await s.ThreadAsync(closed.Id));
    }

    private static ServiceCustomerPrivacyService Service(ServiceRequestScenario s) =>
        new(
            s.Db,
            Options.Create(new GdprOptions { Retention = new GdprRetentionOptions { SupplierCustomers = TwoYears } }),
            new CapturingLogger<ServiceCustomerPrivacyService>(),
            s.Clock);
}
