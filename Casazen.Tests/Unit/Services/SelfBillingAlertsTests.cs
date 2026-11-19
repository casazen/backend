using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using CasazenPolicies = Casazen.Web.Authorization.CasazenPolicies;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-TD17-ALERTS: the two gaps of the self-billed invoices (TD17) the SDI rejects and the drafts nobody asks for. A TD17 the SDI rejected raises one «Da fare» of its own, not
/// «Correggi la fattura», and the system closes it when the document is corrected, credited or accepted; the monthly run (off by default) makes the drafts of the month that ended once and
/// raises one «Da fare» to check and issue them, closed when no draft is left; and none of it reaches another organization or a caller who may not manage the self-billed invoices.
/// </summary>
public class SelfBillingAlertsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// PROOF TEST: data rule. The rejection of a TD17 raises exactly one dedicated «Da fare» (<c>SelfBillingRejected</c>, leading to the self-billed invoices with the document id, organization-level,
    /// urgent, in the words of the organization, no datum of the document), never the generic «Correggi la fattura», however many times the event is delivered; the system closes it when the
    /// document is corrected, credited or accepted by the SDI (closing again changes nothing), and a rejection delivered after the document left the rejected state raises nothing.
    /// </summary>
    [Fact]
    public async Task RejectedSelfBilling_RaisesOneDedicatedToDo_AndClosesIt_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var handler = HandlerOf(rig.Db, ActivitiesOf(rig));
        var open = await AddTd17Async(rig, 1);
        var corrected = await AddTd17Async(rig, 2);
        var credited = await AddTd17Async(rig, 3);
        var accepted = await AddTd17Async(rig, 4);

        // The event delivered twice raises one item, the dedicated one: organization-level, urgent, about the document, leading to the self-billed invoices.
        foreach (var document in new[] { open, corrected, credited, accepted })
        {
            await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, document.Id), Ct);
            await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, document.Id), Ct);
        }

        var items = await rig.Db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(4, items.Count);
        Assert.DoesNotContain(items, a => a.Action == ActivityAction.CorrectInvoice || a.ActivityType == nameof(ActivityType.FiscalDocument));
        var item = items.Single(a => a.EntityId == open.Id);
        Assert.Equal(
            (nameof(ActivityType.SelfBillingRejected), ActivityAction.ReviewSelfBilling, "FiscalDocument", ActivityUrgency.High, rig.OrgId, (Guid?)null, (DateTime?)null),
            (item.ActivityType, item.Action!.Value, item.EntityType, item.Urgency!.Value, item.OrgId, item.PropertyId, item.CompletedAt));

        // The words are the organization's (Italian by default), a fixed sentence: nothing of the document or of the answer of the SDI.
        Assert.Contains("autofattura", item.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(open.Number!, item.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("00417", item.Reason, StringComparison.Ordinal);

        // The correction discards the document, the credit of a document and the acceptance of the SDI close the item of that document, by the system, and nobody else's.
        await SetDocumentAsync(rig, corrected.Id, d => d.Status = FiscalDocumentStatus.Discarded);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentCorrected, corrected.Id), Ct);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentCreditNoteIssued, Guid.NewGuid(), CreditNotePayloadOf(credited.Id)), Ct);
        await SetDocumentAsync(rig, accepted.Id, d => d.SdiStatus = FiscalDocumentSdiStatuses.Delivered);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiDelivered, accepted.Id), Ct);

        var after = (await rig.Db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync()).ToDictionary(a => a.EntityId!.Value);
        Assert.Null(after[open.Id].CompletedAt);
        foreach (var done in new[] { corrected.Id, credited.Id, accepted.Id })
            Assert.Equal((InternalActivity.ClosedBySystem, true), (after[done].ClosedBy, after[done].CompletedAt is not null));

        // Closing again changes nothing, and a rejection delivered after the document left the rejected state (the events are not delivered in order) raises nothing.
        var closedAt = after[corrected.Id].CompletedAt;
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentCorrected, corrected.Id), Ct);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, corrected.Id), Ct);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, accepted.Id), Ct);
        var again = await rig.Db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(4, again.Count);
        Assert.Equal(closedAt, again.Single(a => a.EntityId == corrected.Id).CompletedAt);
        Assert.Equal(1, again.Count(a => a.CompletedAt is null));

        // The rejection of an invoice to a guest is not affected: it still has its «Correggi la fattura».
        var guestInvoice = await AddTd17Async(rig, 5);
        await SetDocumentAsync(rig, guestInvoice.Id, d => d.Purpose = FiscalDocumentPurpose.GuestStay);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, guestInvoice.Id), Ct);
        var correct = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().Where(a => a.Action == ActivityAction.CorrectInvoice).ToListAsync());
        Assert.Equal(guestInvoice.Id, correct.EntityId);
    }

    /// <summary>
    /// PROOF TEST: data rule. The monthly run is off by default (the shipped settings and the code), and then does nothing, not even open a scope (so no organization is read and no lock is taken);
    /// with the key on it makes the drafts of the month that ended once and raises one «Da fare» to check and issue them; a second run, or a trigger by hand, makes no second draft and no second
    /// item, and never brings back a draft a person discarded; the item closes when no draft is left; and nothing is ever issued by the run.
    /// </summary>
    [Fact]
    public async Task MonthlySelfBillingDrafts_AreIdempotentAndOffByDefault_Test()
    {
        Assert.False(new SelfBillingOptions().MonthlyDrafts.Enabled);
        var shipped = SelfBillingRig.Shipped();
        Assert.False(shipped.MonthlyDrafts.Enabled);

        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        var service = ServiceOf(rig, shipped, ActivitiesOf(rig));

        // Off: a factory that is never asked for a scope (a strict mock throws at the first call): nothing is read, nothing is locked, nothing is written.
        var never = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        await JobOf(never.Object, rig, shipped, issuance: true).ExecuteAsync(Ct);
        never.VerifyNoOtherCalls();
        Assert.Empty(await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.Purpose == FiscalDocumentPurpose.SelfBilling).ToListAsync());
        Assert.Empty(await rig.Db.InternalActivities.AsNoTracking().ToListAsync());

        // On, with the issuance off: the same.
        var enabled = SelfBillingRig.Shipped();
        enabled.MonthlyDrafts.Enabled = true;
        await JobOf(never.Object, rig, enabled, issuance: false).ExecuteAsync(Ct);
        never.VerifyNoOtherCalls();

        // On: the drafts of October (the 12th of November is in the month after) are made once, one item stands, nothing is issued, the organization is entered as the tenant.
        var entered = new List<Guid>();
        var job = JobOf(ProviderOf(service, entered).GetRequiredService<IServiceScopeFactory>(), rig, enabled, issuance: true);
        await job.ExecuteAsync(Ct);
        Assert.Equal([rig.OrgId], entered);
        var drafts = await DocumentsAsync(rig);
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, d => Assert.Equal((FiscalDocumentStatus.Draft, 2026, 10), (d.Status, d.PeriodYear!.Value, d.PeriodMonth!.Value)));
        var todo = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(
            (nameof(ActivityType.SelfBillingDraftsToReview), ActivityAction.ReviewSelfBilling, "SelfBillingPeriod:2026-10", rig.OrgId, ActivityUrgency.Medium, rig.OrgId, (Guid?)null),
            (todo.ActivityType, todo.Action!.Value, todo.EntityType, todo.EntityId!.Value, todo.Urgency!.Value, todo.OrgId, todo.PropertyId));
        Assert.Equal(RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 16)), todo.Deadline);
        Assert.Contains("autofatture", todo.Reason, StringComparison.Ordinal);

        // A second run (a retry, a trigger by hand) makes no second draft and no second item.
        await job.ExecuteAsync(Ct);
        Assert.Equal(drafts.Select(d => d.Id).Order(), (await DocumentsAsync(rig)).Select(d => d.Id).Order());
        Assert.Equal(todo.Id, Assert.Single(await rig.Db.InternalActivities.AsNoTracking().ToListAsync()).Id);

        // A person discards one draft: the next run does not bring it back, and the item stands while the other draft is open.
        await service.DiscardDraftAsync(rig.OrgId, drafts[0].Id);
        await job.ExecuteAsync(Ct);
        var states = (await DocumentsAsync(rig)).ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal((FiscalDocumentStatus.Discarded, FiscalDocumentStatus.Draft), (states[drafts[0].Id], states[drafts[1].Id]));
        Assert.Null(Assert.Single(await rig.Db.InternalActivities.AsNoTracking().ToListAsync()).CompletedAt);

        // The last draft is issued by a person (never by the run): the item closes, and a run after that makes nothing and raises nothing.
        await service.IssueAsync(rig.OrgId, drafts[1].Id, confirm: true, "auth0|agency");
        var closed = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal((todo.Id, InternalActivity.ClosedBySystem), (closed.Id, closed.ClosedBy));
        await job.ExecuteAsync(Ct);
        Assert.Equal(2, (await DocumentsAsync(rig)).Count);
        Assert.Single(await rig.Db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(1, (await DocumentsAsync(rig)).Count(d => d.Status == FiscalDocumentStatus.Issued));
    }

    /// <summary>
    /// The run of every organization is isolated: it enters the tenant of each, an organization that fails does not stop the others (the run fails at the end with them all so that Hangfire repeats it),
    /// and it asks for the month that ended by the calendar of Rome.
    /// </summary>
    [Fact]
    public async Task MonthlyJob_EachOrganizationRunsUnderItsOwnTenant_AndOneThatFailsDoesNotStopTheOthers()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entered = new List<Guid>();
        var service = new Mock<ISelfBillingService>();
        service.Setup(s => s.OrgsWithCommissionsAsync(2026, 10, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        service.Setup(s => s.RunMonthlyDraftsAsync(first, 2026, 10, It.IsAny<CancellationToken>())).ThrowsAsync(new DomainRuleException(SelfBillingErrors.NotConfigured, "SelfBillingNotConfigured"));
        service.Setup(s => s.RunMonthlyDraftsAsync(second, 2026, 10, It.IsAny<CancellationToken>())).ReturnsAsync(new SelfBillingMonthlyRun(2026, 10, false, 2, 0, 2));
        var enabled = SelfBillingRig.Shipped();
        enabled.MonthlyDrafts.Enabled = true;
        var job = JobOf(ProviderOf(service.Object, entered).GetRequiredService<IServiceScopeFactory>(), rig, enabled, issuance: true);

        var failed = await Assert.ThrowsAsync<AggregateException>(() => job.ExecuteAsync(Ct));

        Assert.IsType<DomainRuleException>(Assert.Single(failed.InnerExceptions));
        Assert.Equal([first, second], entered);
        service.Verify(s => s.RunMonthlyDraftsAsync(second, 2026, 10, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.CreateDraftsForPeriodAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.IssueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void MonthlyJob_IsRegisteredWhileTheIssuanceIsOn_OnThe5thAt0730InRome_AndNeverRunsTwiceAtOnce()
    {
        Assert.Equal("self-billing-monthly-drafts", SelfBillingMonthlyDraftsJob.RecurringJobId);

        var on = Schedule(issuance: true);
        var job = on.Registered[SelfBillingMonthlyDraftsJob.RecurringJobId];
        Assert.Equal(("30 7 5 * *", RomeCalendar.TimeZone), (job.Cron, job.Options.TimeZone));
        Assert.Equal(typeof(SelfBillingMonthlyDraftsJob), job.Job.Type);
        var concurrency = job.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, 300);
        on.Manager.Verify(m => m.RemoveIfExists(SelfBillingMonthlyDraftsJob.RecurringJobId), Times.Never);

        var off = Schedule(issuance: false);
        Assert.DoesNotContain(SelfBillingMonthlyDraftsJob.RecurringJobId, off.Registered.Keys);
        off.Manager.Verify(m => m.RemoveIfExists(SelfBillingMonthlyDraftsJob.RecurringJobId), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: tenant rule. Nothing of the alerts reaches another organization: the item of a rejected TD17 is the organization of the event's, an event of one organization neither raises nor
    /// closes the item of another's document, the item of a month closes when the drafts of its own organization are gone (the open draft of another does not keep it open), each organization reads
    /// only its own items, a caller restricted to some properties reads none of them, and the monthly run looks only at the active agencies that have commissions in the ledger of the month.
    /// </summary>
    [Fact]
    public async Task SelfBillingAlerts_AreScopedToTheOrganization_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        var activities = ActivitiesOf(rig);
        var service = ServiceOf(rig, SelfBillingRig.Shipped(), activities);
        var handler = HandlerOf(rig.Db, activities);

        // Another agency (in English) with a rejected TD17, an open draft of the same month and the item of that month.
        var other = Guid.NewGuid();
        await rig.AddAsync(
            new OrgEntity
            {
                Id = other,
                Name = "TEST Altra Agenzia",
                Slug = $"test-{other:N}"[..20],
                DisplayName = "TEST Altra Agenzia",
                ContactEmail = "altra@example.com",
                PlanTier = PlanTier.Agency,
                DefaultLanguage = "en",
            });
        var otherRejected = await AddTd17Async(rig, 1, other);
        var otherDraft = Td17(other, 2);
        otherDraft.Status = FiscalDocumentStatus.Draft;
        otherDraft.SdiStatus = FiscalDocumentSdiStatuses.NotApplicable;
        await rig.AddAsync(otherDraft);
        await activities.CreateAsync(DraftsTodo(other), Ct);

        // An event of one organization names the document of the other: nothing is raised for it.
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, otherRejected.Id), Ct);
        Assert.Empty(await rig.Db.InternalActivities.AsNoTracking().Where(a => a.EntityId == otherRejected.Id).ToListAsync());
        await handler.HandleAsync(Event(other, DomainEventType.FiscalDocumentSdiRejected, otherRejected.Id), Ct);
        var raised = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().Where(a => a.EntityId == otherRejected.Id).ToListAsync());
        Assert.Equal((other, nameof(ActivityType.SelfBillingRejected)), (raised.OrgId, raised.ActivityType));

        // The words are the organization's: English for the other, Italian for the first, and not the same sentence (a TD17 of September: October is still to be made below).
        var mine = await AddTd17Async(rig, 3);
        await SetDocumentAsync(rig, mine.Id, d => d.PeriodMonth = 9);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiRejected, mine.Id), Ct);
        var italian = (await rig.Db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == mine.Id)).Reason;
        Assert.Contains("self-billed invoice", raised.Reason, StringComparison.Ordinal);
        Assert.Contains("autofattura", italian, StringComparison.Ordinal);
        Assert.NotEqual(raised.Reason, italian);

        // The closing events of one organization never close the item of another's document.
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentCorrected, otherRejected.Id), Ct);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentSdiDelivered, otherRejected.Id), Ct);
        await handler.HandleAsync(Event(rig.OrgId, DomainEventType.FiscalDocumentCreditNoteIssued, Guid.NewGuid(), CreditNotePayloadOf(otherRejected.Id)), Ct);
        Assert.Null((await rig.Db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == otherRejected.Id)).CompletedAt);

        // The monthly run of the first organization makes its drafts and its item; the other's draft and item are not touched, and the item of the month of each is its own.
        var run = await service.RunMonthlyDraftsAsync(rig.OrgId, 2026, 10, Ct);
        Assert.Equal((false, 2, 2), (run.AlreadyMade, run.Created, run.OpenDrafts));
        var drafts = (await DocumentsAsync(rig)).Where(d => d.OrgId == rig.OrgId && d.Status == FiscalDocumentStatus.Draft).ToList();
        Assert.Equal(2, drafts.Count);
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == otherDraft.Id)).Status);
        var months = await rig.Db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview)).ToListAsync();
        Assert.Equal(new[] { other, rig.OrgId }.Order(), months.Select(m => m.OrgId).Order());

        // The first organization issues its drafts: its item closes although the other organization still has an open draft of the same month, and the other's item stays open.
        foreach (var draft in drafts)
            await service.IssueAsync(rig.OrgId, draft.Id, confirm: true, "auth0|agency");
        months = await rig.Db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview)).ToListAsync();
        Assert.Equal((true, false), (months.Single(m => m.OrgId == rig.OrgId).CompletedAt is not null, months.Single(m => m.OrgId == other).CompletedAt is not null));

        // Each organization reads only its own items; a caller restricted to some properties reads none of them.
        var ofFirst = await activities.GetPendingAsync(new HostScope(rig.OrgId), Ct);
        var ofOther = await activities.GetPendingAsync(new HostScope(other), Ct);
        Assert.Equal([mine.Id], ofFirst.Select(a => a.EntityId!.Value));
        Assert.Equal(new[] { otherRejected.Id, other }.Order(), ofOther.Select(a => a.EntityId!.Value).Order());
        Assert.Empty(await activities.GetPendingAsync(new HostScope(rig.OrgId, GrantedToUserId: "auth0|collaborator"), Ct));
        Assert.Empty(await activities.GetPendingAsync(new HostScope(other, OwnerId: "auth0|legacy"), Ct));

        // The run looks at the active agencies (not downgraded) that have commissions in the ledger of the month: not a starter plan, not a month without lines, not an inactive org.
        var ledgerOrgs = new List<(Guid Org, PlanTier Tier, bool Active, bool Downgraded)>
        {
            (other, PlanTier.Agency, true, false),
            (Guid.NewGuid(), PlanTier.Starter, true, false),
            (Guid.NewGuid(), PlanTier.Agency, false, false),
            (Guid.NewGuid(), PlanTier.Agency, true, true),
        };
        foreach (var (org, tier, active, downgraded) in ledgerOrgs.Skip(1))
        {
            await rig.AddAsync(
                new OrgEntity
                {
                    Id = org,
                    Name = "TEST Altro",
                    Slug = $"test-{org:N}"[..20],
                    DisplayName = "TEST Altro",
                    ContactEmail = "altro@example.com",
                    PlanTier = tier,
                    IsActive = active,
                    IsAgencyTierDowngraded = downgraded,
                });
        }

        foreach (var (org, _, _, _) in ledgerOrgs)
        {
            var fee = rig.Fee(rig.Company, rig.Lake, Guid.NewGuid(), 10.00m);
            fee.OrgId = org;
            await rig.AddAsync(fee);
        }

        var inSeptember = rig.Fee(rig.Company, rig.Lake, Guid.NewGuid(), 10.00m, year: 2026, month: 9);
        inSeptember.OrgId = Guid.NewGuid();
        await rig.AddAsync(new OrgEntity { Id = inSeptember.OrgId, Name = "TEST Settembre", Slug = $"test-{inSeptember.OrgId:N}"[..20], DisplayName = "TEST Settembre", ContactEmail = "settembre@example.com", PlanTier = PlanTier.Agency }, inSeptember);
        Assert.Equal(new[] { rig.OrgId, other }.Order(), (await service.OrgsWithCommissionsAsync(2026, 10, Ct)).Order());
    }

    /// <summary>
    /// PROOF TEST: permission rule. The items about the self-billed invoices are listed, and counted in the badge, only to a caller who holds <c>agency.invoice.manage</c> (the policy of their endpoints);
    /// anybody else keeps every other item of the list and never sees them; a caller restricted to some properties reaches none of them whatever it may do; and the permission is looked up only when
    /// the organization has such an item open (the badge runs on every page).
    /// </summary>
    [Fact]
    public async Task SelfBillingTodos_AreListedOnlyToWhoCanManageTheInvoices_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var activities = ActivitiesOf(rig);

        // Nothing about the self-billed invoices is open: the permission is not looked up at all, and the list is the usual one.
        var ordinary = await activities.CreateAsync(CheckIn(rig.Lake), Ct);
        var quiet = Controller(rig, activities, new HostScope(rig.OrgId), allowed: false);
        Assert.Equal([ordinary], (await ItemsAsync(quiet.Controller)).Select(i => i.Id));
        quiet.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()), Times.Never);

        var rejected = await activities.CreateAsync(RejectedTodo(rig.OrgId, Guid.NewGuid()), Ct);
        var monthly = await activities.CreateAsync(DraftsTodo(rig.OrgId), Ct);

        // Who holds the permission reads all three and the badge counts the two urgent ones; who does not reads the ordinary one and the badge counts that one only.
        var allowed = Controller(rig, activities, new HostScope(rig.OrgId), allowed: true);
        Assert.Equal(new[] { ordinary, rejected, monthly }.Order(), (await ItemsAsync(allowed.Controller)).Select(i => i.Id).Order());
        Assert.Equal(2, await CountAsync(allowed.Controller));
        allowed.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyInvoiceManage), Times.Exactly(2));

        var denied = Controller(rig, activities, new HostScope(rig.OrgId), allowed: false);
        Assert.Equal([ordinary], (await ItemsAsync(denied.Controller)).Select(i => i.Id));
        Assert.Equal(1, await CountAsync(denied.Controller));

        // A caller restricted to some properties reaches no organization-level item, with the permission or without it.
        var restricted = Controller(rig, activities, new HostScope(rig.OrgId, GrantedToUserId: "auth0|collaborator"), allowed: true);
        Assert.DoesNotContain(await ItemsAsync(restricted.Controller), i => i.Id == rejected || i.Id == monthly);
        Assert.Equal(0, await CountAsync(restricted.Controller));

        // The service leaves out only the types it is told to, and an empty list leaves out nothing, never everything.
        Assert.Equal(new[] { ordinary, rejected, monthly }.Order(), (await activities.GetPendingAsync(new HostScope(rig.OrgId), Ct, excludeTypes: [])).Select(a => a.Id).Order());
        Assert.Equal([ordinary], (await activities.GetPendingAsync(new HostScope(rig.OrgId), Ct, excludeTypes: SelfBillingTodos.ActivityTypes)).Select(a => a.Id));
        Assert.True(await activities.AnyOpenAsync(rig.OrgId, SelfBillingTodos.ActivityTypes, Ct));
        Assert.False(await activities.AnyOpenAsync(rig.OrgId, [], Ct));
        Assert.False(await activities.AnyOpenAsync(Guid.NewGuid(), SelfBillingTodos.ActivityTypes, Ct));
    }

    // ---------------------------------------------------------------- helpers

    private static InternalActivityService ActivitiesOf(SelfBillingRig rig) => new(rig.Db, NullLogger<InternalActivityService>.Instance, rig.Clock);

    private static FiscalDocumentRejectionHandler HandlerOf(Casazen.Infrastructure.Data.AppDbContext db, IInternalActivityService activities) =>
        new(db, activities, new ResxDocumentTexts(), NullLogger<FiscalDocumentRejectionHandler>.Instance);

    private static SelfBillingService ServiceOf(SelfBillingRig rig, SelfBillingOptions options, IInternalActivityService? activities = null) => new(
        rig.Db,
        rig.Documents,
        rig.Issuers,
        rig.Rules,
        new PortalAccountHolderResolver(rig.Db),
        new ResxDocumentTexts(),
        Options.Create(options),
        rig.Clock,
        NullLogger<SelfBillingService>.Instance,
        activities);

    private static SelfBillingMonthlyDraftsJob JobOf(IServiceScopeFactory scopes, SelfBillingRig rig, SelfBillingOptions options, bool issuance) => new(
        scopes,
        issuance ? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance) : new TestFeatureFlags(),
        Options.Create(options),
        NullLogger<SelfBillingMonthlyDraftsJob>.Instance,
        rig.Clock);

    /// <summary>A provider whose scopes hand out <paramref name="service"/> and record the organizations entered as the tenant.</summary>
    private static ServiceProvider ProviderOf(ISelfBillingService service, List<Guid> entered)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => service);
        services.AddScoped<IBackgroundTenantScope>(_ => new RecordingTenantScope(entered));
        return services.BuildServiceProvider();
    }

    private static DomainEvent Event(Guid orgId, DomainEventType type, Guid entityId, string payload = "{}") =>
        new(Guid.NewGuid(), orgId, type, entityId, SelfBillingRig.Now.UtcDateTime, payload);

    private static string CreditNotePayloadOf(Guid creditedDocumentId) =>
        JsonSerializer.Serialize(new FiscalDocumentIssuedPayload(Guid.NewGuid(), null, "CreditNote", creditedDocumentId), DomainEventRecorder.PayloadJsonOptions);

    /// <summary>A self-billed invoice (TD17) of an organization, issued, in the state the SDI rejected it.</summary>
    private static FiscalDocument Td17(Guid orgId, int number) => new()
    {
        OrgId = orgId,
        Source = FiscalDocumentSource.CasaZen,
        Purpose = FiscalDocumentPurpose.SelfBilling,
        Kind = FiscalDocumentKind.Invoice,
        Status = FiscalDocumentStatus.Issued,
        SdiStatus = FiscalDocumentSdiStatuses.Rejected,
        SdiErrorCode = "00417",
        Series = "TD17-2026",
        SequenceNumber = number,
        Number = $"TD17-2026/{number}",
        PeriodYear = 2026,
        PeriodMonth = 10,
    };

    private static async Task<FiscalDocument> AddTd17Async(SelfBillingRig rig, int number, Guid? orgId = null)
    {
        var document = Td17(orgId ?? rig.OrgId, number);
        await rig.AddAsync(document);
        return document;
    }

    private static async Task SetDocumentAsync(SelfBillingRig rig, Guid id, Action<FiscalDocument> change)
    {
        change(await rig.Db.FiscalDocuments.SingleAsync(d => d.Id == id));
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
    }

    private static async Task<List<FiscalDocument>> DocumentsAsync(SelfBillingRig rig) =>
        await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.Purpose == FiscalDocumentPurpose.SelfBilling).OrderBy(d => d.TaxableBase).ThenBy(d => d.Id).ToListAsync();

    private static CreateTodoRequest RejectedTodo(Guid orgId, Guid documentId) => new()
    {
        Type = ActivityType.SelfBillingRejected,
        Action = ActivityAction.ReviewSelfBilling,
        EntityType = SelfBillingTodos.RejectedEntityType,
        EntityId = documentId,
        Urgency = ActivityUrgency.High,
        OrgId = orgId,
        Reason = "rejected",
    };

    private static CreateTodoRequest DraftsTodo(Guid orgId) => new()
    {
        Type = ActivityType.SelfBillingDraftsToReview,
        Action = ActivityAction.ReviewSelfBilling,
        EntityType = SelfBillingTodos.PeriodEntityType(2026, 10),
        EntityId = orgId,
        Urgency = ActivityUrgency.Medium,
        OrgId = orgId,
        Reason = "drafts",
    };

    private static CreateTodoRequest CheckIn(Guid propertyId) => new()
    {
        Type = ActivityType.CheckInPending,
        Action = ActivityAction.StartCheckIn,
        EntityType = InternalActivity.BookingEntityType,
        EntityId = Guid.NewGuid(),
        Urgency = ActivityUrgency.High,
        PropertyId = propertyId,
    };

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Registered) Schedule(bool issuance)
    {
        var registered = new Dictionary<string, (Job, string, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(issuance);

        RecurringJobsRegistration.Configure(manager.Object, flags.Object);

        return (manager, registered);
    }

    /// <summary>The controller of the list for a caller of <paramref name="scope"/> who holds the permission of the self-billed invoices or not, with the authorization it asks.</summary>
    private static (ActivitiesController Controller, Mock<IAuthorizationService> Authorization) Controller(SelfBillingRig rig, IInternalActivityService activities, HostScope scope, bool allowed)
    {
        const string userId = "auth0|caller";
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyInvoiceManage))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rig.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(userId, It.IsAny<IReadOnlySet<string>>(), rig.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var controller = new ActivitiesController(activities, scopes.Object, org.Object, null, null, authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId), new Claim(ClaimTypes.NameIdentifier, userId)], "test")) },
            },
        };
        return (controller, authorization);
    }

    private static async Task<List<TodoActivityDto>> ItemsAsync(ActivitiesController controller) =>
        Assert.IsAssignableFrom<IEnumerable<TodoActivityDto>>(Assert.IsType<OkObjectResult>((await controller.List(Ct)).Result).Value).ToList();

    private static async Task<int> CountAsync(ActivitiesController controller) =>
        Assert.IsType<TodoCountDto>(Assert.IsType<OkObjectResult>((await controller.UrgentCount(Ct)).Result).Value).Count;
}

/// <summary>The tenant scope of a job that records the organizations it was entered for, in order.</summary>
file sealed class RecordingTenantScope(List<Guid> entered) : IBackgroundTenantScope
{
    public void EnterOrg(Guid orgId) => entered.Add(orgId);
}
