using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10b: the conversation of a request on measure, as the supplier has it. The rules that cost privacy, trust or a customer's request when
/// they are wrong are the ones tested: only the supplier of a request on measure reads and writes its thread (any other id is one 404), the
/// customer is told of a reply with a link and never with the words, the first reply stops the clock of the hour (so the conversation is not
/// closed behind the customer's back, and a request nobody answered is), a request on measure is answered or refused and never taken as a
/// booking is, and a closed or full thread takes no more.
/// </summary>
public class ServiceRequestThreadTests
{
    private const string Reply = "Certo, posso passare giovedì mattina per un sopralluogo gratuito.";

    // ─── Reading ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetThread_ShowsTheCustomersWordsInOrder_MarksThemRead_AndSaysTheSupplierCanWrite()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var first = await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId);

        var message = Assert.Single(first.Messages);
        Assert.Equal(ServiceRequestMessageAuthor.Customer, message.Author);
        Assert.Equal(InquiryScenario.Words, message.Body);
        // As it was before this reading: the screen can tell what is new to the supplier.
        Assert.Null(message.ReadAt);
        Assert.Equal(ServiceRequestStatus.Richiesto, first.Status);
        Assert.True(first.CanWrite);
        Assert.Equal(request.ResponseDueAt, first.RespondBy);

        // From now on the customer's message is read; the next reading shows it.
        var second = await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, Assert.Single(second.Messages).ReadAt);
    }

    [Fact]
    public async Task GetThread_ARequestOfAnotherSupplier_ABooking_AndAMissingOne_AreTheSame404_AndNothingIsMarkedRead()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();
        var other = await s.AddBookableSupplierAsync();
        var (booking, _) = await s.BookedAsync();

        var ofAnother = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.GetThreadAsSupplierAsync(request.Id, other.OrgId));
        var aBooking = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.GetThreadAsSupplierAsync(booking.Id, s.SupplierOrgId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.GetThreadAsSupplierAsync(Guid.NewGuid(), s.SupplierOrgId));

        Assert.All(new[] { aBooking, missing }, ex => Assert.Equal(ofAnother.Code, ex.Code));
        Assert.Null(Assert.Single(await s.ThreadAsync(request.Id)).ReadAt);
    }

    // ─── Writing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostMessage_AddsTheSuppliersMessage_StopsTheClock_AndTellsTheCustomerWithALinkAndNoWords()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();
        Assert.NotNull(request.ResponseDueAt);
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var message = await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "  " + Reply + "  ");

        Assert.Equal(ServiceRequestMessageAuthor.Supplier, message.Author);
        Assert.Equal(Reply, message.Body);
        var thread = await s.ThreadAsync(request.Id);
        Assert.Equal([ServiceRequestMessageAuthor.Customer, ServiceRequestMessageAuthor.Supplier], thread.Select(m => m.Author).ToArray());
        Assert.Equal(s.SupplierOrgId, thread[1].OrgId);

        // The supplier answered: nobody closes the conversation behind the customer's back.
        var stored = await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
        Assert.Null(stored.ResponseDueAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryReply, email.Template);
        Assert.Contains("Supplier Srl", email.Content.Subject);
        Assert.Contains("/fornitori/vetrina-test/richiesta?code=", email.Content.HtmlBody);
        foreach (var words in new[] { "giovedì", "sopralluogo", "ringhiera" })
            Assert.DoesNotContain(words, email.Content.Subject + email.Content.HtmlBody);
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task PostMessage_ALineAfterAnother_IsOneEmail_TheNextAfterTheQuietPeriodIsAnother()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();

        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "Buongiorno, ho letto la richiesta.");
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "Le serve una data precisa?");
        Assert.Single(s.Emails.Snapshot());

        s.Clock.Advance(TimeSpan.FromMinutes(11));
        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "Resto in attesa di una risposta.");

        Assert.Equal(2, s.Emails.Snapshot().Count);
        Assert.Equal(3, (await s.ThreadAsync(request.Id)).Count(m => m.Author == ServiceRequestMessageAuthor.Supplier));
    }

    [Fact]
    public async Task PostMessage_RefusesWhatItShould_AndAddsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();
        var (booking, _) = await s.BookedAsync();
        var other = await s.AddBookableSupplierAsync();
        s.ForgetNotifications();

        // Empty, too long, with a character a message cannot have: one answer.
        foreach (var body in new[] { null, "   ", new string('x', ShowcaseInquiryLimits.MessageMaxLength + 1), "con un carattere" + (char)0 + " nascosto" })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, body));
            Assert.Equal("service_request_message_invalid", invalid.Code);
        }

        // A request that is not the supplier's, or is not on measure, is a 404; a host never reaches the thread.
        await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.PostMessageAsSupplierAsync(request.Id, other.OrgId, Reply));
        await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.PostMessageAsSupplierAsync(booking.Id, s.SupplierOrgId, Reply));

        // A supplier that is not active writes nothing (it can still read).
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, Reply));
        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, suspended.Code);
        Assert.False((await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId)).CanWrite);

        Assert.Single(await s.ThreadAsync(request.Id));
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task PostMessage_AClosedThread_Is409_AFullOne_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var refused = await s.InquiredAsync();
        var crowded = await s.InquiredAsync(email: "altra.persona@example.com");

        await s.Service.RejectAsync(refused.Id, s.SupplierOrgId, "Non copro questo tipo di lavoro");
        var closed = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Service.PostMessageAsSupplierAsync(refused.Id, s.SupplierOrgId, Reply));
        Assert.Equal("service_request_thread_closed", closed.Code);
        Assert.False((await s.Service.GetThreadAsSupplierAsync(refused.Id, s.SupplierOrgId)).CanWrite);

        // 199 more messages and the thread has the 200 it may have.
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
            async () => await s.Service.PostMessageAsSupplierAsync(crowded.Id, s.SupplierOrgId, Reply));
        Assert.Equal("service_request_thread_full", full.Code);
        Assert.Equal(ShowcaseInquiryLimits.MaxMessagesPerRequest, (await s.ThreadAsync(crowded.Id)).Count);
    }

    // ─── A request on measure is answered, refused or lapses: it is never taken as a booking is ──────────────────────

    [Fact]
    public async Task ARequestOnMeasure_IsNotTakenNorGivenAnotherTime_UntilThereIsAnOffer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();

        var take = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));
        var propose = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Service.ProposeTimeAsync(
                request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt10)));
        var batch = Assert.Single(await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [request.Id]));

        Assert.Equal("supplier_inquiry_needs_offer", take.Code);
        Assert.Equal("supplier_inquiry_needs_offer", propose.Code);
        Assert.False(batch.Accepted);
        Assert.Equal("supplier_inquiry_needs_offer", batch.Code);

        var stored = await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, stored.Status);
        Assert.Null(stored.TakenAt);
        Assert.Null(stored.ProposedStartUtc);
    }

    [Fact]
    public async Task Reject_OfARequestOnMeasure_TellsTheCustomerWithTheReason_AndNeitherADayNorATime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var request = await s.InquiredAsync();

        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non copro questo tipo di lavoro");

        var email = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryDeclined, email.Template);
        Assert.Contains("Non copro questo tipo di lavoro", email.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test", email.Content.HtmlBody);
        Assert.DoesNotContain(EmailTemplates.Names.SupplierBookingDeclined, s.Emails.Snapshot().Select(e => e.Template));
    }

    [Fact]
    public async Task NoAnswerInTime_TheJobClosesTheRequestAndTellsTheCustomer_ARequestTheSupplierAnsweredIsLeftAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var silent = await s.InquiredAsync(supplier);
        var answered = await s.InquiredAsync(supplier, email: "altra.persona@example.com");
        await s.Service.PostMessageAsSupplierAsync(answered.Id, s.SupplierOrgId, "Ci sentiamo domani.");
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(61));

        var job = new ServiceRequestAutoCancelService(
            s.Db, s.Kit.Notifier, s.Kit.Flags, NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock);
        var run = await job.CancelUnansweredShowcaseAsync();

        Assert.Equal(1, run.Cancelled);
        var closed = await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == silent.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, closed.Status);
        Assert.Equal(ServiceRequestActorParty.System, closed.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, closed.CancellationReason);
        Assert.Equal(
            ServiceRequestStatus.Richiesto,
            (await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == answered.Id)).Status);

        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryExpired, toCustomer.Template);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.To == "altra.persona@example.com");
    }
}
