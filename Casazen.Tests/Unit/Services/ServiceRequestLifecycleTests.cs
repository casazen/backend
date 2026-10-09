using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: the life of a request after the take on an in-memory database: start, complete with the final price, cancel by the
/// host or the supplier, the host's reminder. Every refusal leaves the request and the queues untouched; every success tells
/// the other party and only that party. The races (two changes at once, <c>xmin</c>) need PostgreSQL and are in
/// <c>ServiceRequestLifecyclePostgresTests</c>.
/// </summary>
public class ServiceRequestLifecycleTests
{
    // ─── Start ───

    [Fact]
    public async Task StartAsync_TakenRequest_GoesInCorsoRecordsTheMomentAndTellsTheHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromHours(20));

        var started = await s.Service.StartAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestStatus.InCorso, started.Status);
        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.InCorso, saved.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.StartedAt);
        Assert.Equal(saved.StartedAt, saved.UpdatedAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("host@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestStatusChanged, email.Template);
        Assert.Contains(ServiceRequestScenario.PropertyName, email.Content.Subject);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.PropertyHosts(s.PropertyId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestStarted, push.Payload.Type);
        Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(taken.Id, ServiceRequestStatus.InCorso), push.DeliveryKey);
        Assert.Equal(taken.Id, push.Payload.ServiceRequestId);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task StartAsync_RequestNotTaken_Throws422WithTheStartMessageAndChangesNothing(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.StartAsync(request.Id, s.SupplierOrgId));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotStartMessageKey, ex.MessageKey);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(status, saved.Status);
        Assert.Null(saved.StartedAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task StartAsync_RequestOfAnotherSupplier_IsForbidden()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var other = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.StartAsync(taken.Id, other));

        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(taken.Id)).Status);
    }

    [Fact]
    public async Task StartAsync_UnknownRequest_Throws404WithCode()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.StartAsync(Guid.NewGuid(), s.SupplierOrgId));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task StartAsync_SuspendedSupplier_Throws422SupplierNotActive()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.StartAsync(taken.Id, s.SupplierOrgId));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
    }

    // ─── Complete: the final price, the notes ───

    [Fact]
    public async Task CompleteAsync_WithoutDetails_KeepsTheAgreedPriceAsTheFinalOneAndLeavesTheHostNotesAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 7500));

        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.Completato, saved.Status);
        Assert.Equal(7500, saved.FinalAmountCents);
        Assert.Equal(
            new[] { new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, ServiceRequestScenario.ServiceName, 7500) },
            ServiceRequestJson.ReadPriceLines(saved.PriceLinesJson));
        Assert.False(saved.FinalAmountNeedsConfirmation);
        Assert.Null(saved.CompletionNotes);
        Assert.Equal(ServiceRequestScenario.HostNotes, saved.Notes);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_WithNotes_StoresThemInTheirOwnFieldAndNeverReplacesTheNotesOfTheHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        await s.Service.CompleteAsync(
            taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(Notes: "  Fatto, chiavi in cassetta.  "));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal("Fatto, chiavi in cassetta.", saved.CompletionNotes);
        Assert.Equal(ServiceRequestScenario.HostNotes, saved.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompleteAsync_BlankNotes_LeaveNoNotes(string? notes)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(Notes: notes));

        Assert.Null((await s.ReadAsync(taken.Id)).CompletionNotes);
    }

    [Fact]
    public async Task CompleteAsync_RequestWithNoPriceAtAll_HasNoFinalAmount()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(withService: false);
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.FinalAmountCents);
        Assert.Equal("[]", saved.PriceLinesJson);
        Assert.False(saved.FinalAmountNeedsConfirmation);
    }

    [Fact]
    public async Task CompleteAsync_ExtrasWithinTheTolerance_AddUpToTheAgreedPriceInLines()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 10000));

        await s.Service.CompleteAsync(
            taken.Id,
            s.SupplierOrgId,
            new CompleteServiceRequestCommand(Extras: [new ServiceRequestExtra("Bagno in piu", 1500), new ServiceRequestExtra("Set lenzuola", 500)]));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(12000, saved.FinalAmountCents);
        Assert.False(saved.FinalAmountNeedsConfirmation); // exactly +20 %
        Assert.Equal(
            new[]
            {
                new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, ServiceRequestScenario.ServiceName, 10000),
                new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Extra, "Bagno in piu", 1500),
                new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Extra, "Set lenzuola", 500),
            },
            ServiceRequestJson.ReadPriceLines(saved.PriceLinesJson));
    }

    [Theory]
    [InlineData(12000, false)] // +20 %: the limit is "more than"
    [InlineData(12001, true)]
    [InlineData(9000, false)]
    public async Task CompleteAsync_FinalAmountAgainstTheQuote_FlagsOnlyWhatIsMoreThanTwentyPercentAbove(int finalAmount, bool flagged)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 10000));

        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: finalAmount));

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.Completato, saved.Status); // only the data and the flag: the confirmation comes with SP-15
        Assert.Equal(finalAmount, saved.FinalAmountCents);
        Assert.Equal(flagged, saved.FinalAmountNeedsConfirmation);
    }

    [Theory]
    [InlineData(7200, false)]
    [InlineData(7201, true)]
    public async Task CompleteAsync_NoQuote_ComparesWithTheEstimateOfTheService(int finalAmount, bool flagged)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(); // estimate 60.00 euro

        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: finalAmount));

        Assert.Equal(flagged, (await s.ReadAsync(taken.Id)).FinalAmountNeedsConfirmation);
    }

    [Theory]
    [InlineData(15000, false)]
    [InlineData(15001, true)]
    public async Task CompleteAsync_ToleranceFromTheOptions_ReplacesTheDefaultTwentyPercent(int finalAmount, bool flagged)
    {
        using var s = await ServiceRequestScenario.CreateAsync(new ServiceRequestOptions { FinalAmountTolerancePercent = 50 });
        var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 10000));

        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: finalAmount));

        Assert.Equal(flagged, (await s.ReadAsync(taken.Id)).FinalAmountNeedsConfirmation);
    }

    [Fact]
    public async Task CompleteAsync_ExtrasThatTakeThePriceOverTheTolerance_SetTheFlagAndTheEmailSaysSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 6000));
        s.ForgetNotifications();

        await s.Service.CompleteAsync(
            taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(Extras: [new ServiceRequestExtra("Pulizia vetri", 1800)]));

        Assert.True((await s.ReadAsync(taken.Id)).FinalAmountNeedsConfirmation);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("Importo finale: <strong>78,00 €</strong>.", email.Content.HtmlBody);
        Assert.Contains("supera il preventivo", email.Content.HtmlBody);
    }

    [Fact]
    public async Task CompleteAsync_FinalAmountBelowTheExtras_Throws422FinalAmountInvalidAndLeavesTheWorkOpen()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.CompleteAsync(
            taken.Id,
            s.SupplierOrgId,
            new CompleteServiceRequestCommand(FinalAmountCents: 500, Extras: [new ServiceRequestExtra("Trasferta", 1000)])));

        Assert.Equal(ServiceRequestErrorCodes.FinalAmountInvalid, ex.Code);
        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Null(saved.FinalAmountCents);
        Assert.Null(saved.CompletedAt);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task CompleteAsync_FromInCorso_Works()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var started = await s.StartedAsync();

        var completed = await s.Service.CompleteAsync(started.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
    }

    [Fact]
    public async Task CompleteAsync_TellsTheHostWithTheAmountAndTheNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();

        await s.Service.CompleteAsync(
            taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(Notes: "Manca il detersivo", FinalAmountCents: 6000));

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("host@test.com", email.To);
        Assert.Contains("Importo finale: <strong>60,00 €</strong>.", email.Content.HtmlBody);
        Assert.Contains("Manca il detersivo", email.Content.HtmlBody);
        Assert.DoesNotContain("supera il preventivo", email.Content.HtmlBody);
        Assert.Equal(PushTypes.ServiceRequestCompleted, Assert.Single(s.Pushes).Payload.Type);
    }

    // ─── Cancel: who may cancel what ───

    public static TheoryData<ServiceRequestStatus, bool, bool> CancelMatrix => new()
    {
        // status, may the host cancel, may the supplier cancel
        { ServiceRequestStatus.Richiesto, true, true },
        { ServiceRequestStatus.PresoInCarico, true, true },
        { ServiceRequestStatus.InCorso, true, false }, // the host up to the work in progress, the supplier only before the start
        { ServiceRequestStatus.Completato, false, false },
        { ServiceRequestStatus.Pagato, false, false },
        { ServiceRequestStatus.Rifiutato, false, false },
        { ServiceRequestStatus.Annullato, false, false },
    };

    [Theory]
    [MemberData(nameof(CancelMatrix))]
    public async Task CancelAsHostAsync_OnlyBeforeTheWorkIsDone(ServiceRequestStatus status, bool hostMay, bool supplierMay)
    {
        _ = supplierMay;
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        if (hostMay)
        {
            var cancelled = await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Ospiti partiti prima");
            Assert.Equal(ServiceRequestStatus.Annullato, cancelled.Status);
            Assert.Equal(ServiceRequestActorParty.Host, (await s.ReadAsync(request.Id)).CancelledBy);
            return;
        }

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Ospiti partiti prima"));
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotCancelMessageKey, ex.MessageKey);
        Assert.Equal(status, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Theory]
    [MemberData(nameof(CancelMatrix))]
    public async Task CancelAsSupplierAsync_OnlyBeforeTheWorkStarts(ServiceRequestStatus status, bool hostMay, bool supplierMay)
    {
        _ = hostMay;
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        if (supplierMay)
        {
            var cancelled = await s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, "Furgone in panne");
            Assert.Equal(ServiceRequestStatus.Annullato, cancelled.Status);
            Assert.Equal(ServiceRequestActorParty.Supplier, (await s.ReadAsync(request.Id)).CancelledBy);
            return;
        }

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, "Furgone in panne"));
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotCancelMessageKey, ex.MessageKey);
        Assert.Equal(status, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CancelAsHostAsync_NewRequest_RecordsWhoWhenAndWhyAndTellsOnlyTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(30));

        await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "  Ospiti partiti prima  ");

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.CancelledAt);
        Assert.Equal(ServiceRequestActorParty.Host, saved.CancelledBy);
        Assert.Equal("Ospiti partiti prima", saved.CancellationReason);
        Assert.Null(saved.ResponseDueAt);
        // The time the request held stays on it, as history; it holds no slot anymore.
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestCancelledToSupplier, email.Template);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestCancelled, push.Payload.Type);
    }

    [Fact]
    public async Task CancelAsHostAsync_BeforeTheTake_NeverGivesTheSupplierThePropertyNorTheNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.ForgetNotifications();

        await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Cambio programma");

        var email = Assert.Single(s.Emails.Snapshot());
        var texts = new[] { email.Content.Subject, email.Content.HtmlBody }
            .Concat(s.Pushes.Select(p => p.Payload.Title))
            .Concat(s.Pushes.Select(p => p.Payload.Body))
            .ToList();
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(ServiceRequestScenario.PropertyName, text);
            Assert.DoesNotContain(ServiceRequestScenario.HostNotes, text);
            Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, text);
        });
        Assert.Contains(ServiceRequestScenario.Comune, email.Content.Subject + email.Content.HtmlBody);
    }

    [Fact]
    public async Task CancelAsHostAsync_TakenRequest_TheSupplierIsToldItHasLostTheJob()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(ServiceRequestScenario.FridayAt10);
        s.ForgetNotifications();

        await s.Service.CancelAsHostAsync(taken.Id, s.HostOrgId, "Cambio programma");

        Assert.Equal("supplier@test.com", Assert.Single(s.Emails.Snapshot()).To);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(taken.Id)).Status);
    }

    [Fact]
    public async Task CancelAsSupplierAsync_TakenRequest_RecordsTheReasonAndTellsOnlyTheHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(ServiceRequestScenario.FridayAt10);
        s.ForgetNotifications();

        await s.Service.CancelAsSupplierAsync(taken.Id, s.SupplierOrgId, "Furgone in panne");

        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestActorParty.Supplier, saved.CancelledBy);
        Assert.Equal("Furgone in panne", saved.CancellationReason);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("host@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestStatusChanged, email.Template);
        Assert.Contains("Furgone in panne", email.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.PropertyHosts(s.PropertyId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestCancelled, push.Payload.Type);
    }

    [Fact]
    public async Task CancelAsync_DropsTheProposalOfTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Non serve piu");

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedEndUtc);
        Assert.Null(saved.ProposedAt);
        Assert.Null(saved.ProposedByUserId);
        Assert.Null(saved.ProposalMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CancelAsync_BlankReason_IsAProgrammingErrorTheApiNeverSends(string reason)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, reason));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, reason));

        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task CancelAsHostAsync_RequestOfAnotherHost_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.CancelAsHostAsync(request.Id, Guid.NewGuid(), "Prova"));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task CancelAsSupplierAsync_RequestOfAnotherSupplier_IsForbidden()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var other = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.CancelAsSupplierAsync(request.Id, other, "Prova"));
    }

    [Fact]
    public async Task CancelAsSupplierAsync_SuspendedSupplier_Throws422SupplierNotActive()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, "Prova"));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
    }

    [Fact]
    public async Task CancelAsHostAsync_SuspendedSupplier_StillLetsTheHostCancel()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var cancelled = await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Il fornitore e sospeso");

        Assert.Equal(ServiceRequestStatus.Annullato, cancelled.Status);
    }

    [Fact]
    public async Task CancelAsHostAsync_NotificationsCannotBeQueued_StillCancels()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        // The queues are test doubles that cannot fail; a failing one must not undo the cancellation (A4-20).
        using var failing = new ServiceRequestTestKit(s.Db, new ThrowingEmailQueue(), new ThrowingPushQueue(), s.Clock);

        var cancelled = await failing.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Prova");

        Assert.Equal(ServiceRequestStatus.Annullato, cancelled.Status);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── Remind ───

    [Fact]
    public async Task RemindAsync_NewRequest_RecordsTheMomentAndTellsTheSupplierWithoutExtendingTheDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var deadline = (await s.ReadAsync(request.Id)).ResponseDueAt;
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(45));

        await s.Service.RemindAsync(request.Id, s.HostOrgId);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.LastRemindedAt);
        Assert.Equal(deadline, saved.ResponseDueAt);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestReminder, email.Template);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyName, email.Content.Subject + email.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestReminder, push.Payload.Type);
    }

    [Fact]
    public async Task RemindAsync_AgainBeforeSixHours_Throws422RemindTooSoonWithTheIntervalAndSendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.RemindAsync(request.Id, s.HostOrgId);
        var firstReminder = (await s.ReadAsync(request.Id)).LastRemindedAt;
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RemindAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.RemindTooSoon, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.RemindTooSoonMessageKey, ex.MessageKey);
        Assert.Equal(6, Assert.IsType<int>(Assert.Single(ex.MessageArgs)));
        Assert.Equal(firstReminder, (await s.ReadAsync(request.Id)).LastRemindedAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task RemindAsync_AfterSixHours_IsAllowedAgainAndEachReminderHasItsOwnPushKey()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.RemindAsync(request.Id, s.HostOrgId);
        s.Clock.Advance(TimeSpan.FromHours(6));

        await s.Service.RemindAsync(request.Id, s.HostOrgId);

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, (await s.ReadAsync(request.Id)).LastRemindedAt);
        var reminders = s.Pushes.Where(p => p.Payload.Type == PushTypes.ServiceRequestReminder).ToList();
        Assert.Equal(2, reminders.Count);
        Assert.NotEqual(reminders[0].DeliveryKey, reminders[1].DeliveryKey);
    }

    [Fact]
    public async Task RemindAsync_IntervalFromTheOptions_ReplacesTheSixHours()
    {
        using var s = await ServiceRequestScenario.CreateAsync(new ServiceRequestOptions { RemindIntervalHours = 1 });
        var request = await s.RequestAsync();
        await s.Service.RemindAsync(request.Id, s.HostOrgId);
        s.Clock.Advance(TimeSpan.FromMinutes(59));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RemindAsync(request.Id, s.HostOrgId));
        Assert.Equal(1, Assert.IsType<int>(Assert.Single(ex.MessageArgs)));

        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Service.RemindAsync(request.Id, s.HostOrgId);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task RemindAsync_RequestNotWaitingForAnAnswer_Throws422WithTheRemindMessage(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RemindAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotRemindMessageKey, ex.MessageKey);
        Assert.Null((await s.ReadAsync(request.Id)).LastRemindedAt);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RemindAsync_RequestWithAProposalWaitingForTheHost_Throws422WithTheRemindMessage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RemindAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServiceRequestErrorCodes.CannotRemindMessageKey, ex.MessageKey);
    }

    [Fact]
    public async Task RemindAsync_RequestOfAnotherHost_IsAnsweredLikeAMissingOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RemindAsync(request.Id, Guid.NewGuid()));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Null((await s.ReadAsync(request.Id)).LastRemindedAt);
    }

    [Fact]
    public async Task RemindAsync_SuspendedSupplier_IsStillRemindedBecauseTheHostIsTheOneAsking()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        await s.Service.RemindAsync(request.Id, s.HostOrgId);

        Assert.NotNull((await s.ReadAsync(request.Id)).LastRemindedAt);
    }

    // ─── Mark paid keeps working ───

    [Fact]
    public async Task MarkPaidAsync_CompletedRequestWithAFinalAmount_StillGoesToPagatoAndKeepsTheAmount()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 6500));

        var paid = await s.Service.MarkPaidAsync(taken.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        var saved = await s.ReadAsync(taken.Id);
        Assert.Equal(6500, saved.FinalAmountCents);
        Assert.NotNull(saved.PaidAt);
    }

    private sealed class ThrowingEmailQueue : Casazen.Infrastructure.Email.IEmailQueue
    {
        public bool Enqueue(string? to, Casazen.Infrastructure.Email.EmailContent content, string template) =>
            throw new InvalidOperationException("The email queue is down.");
    }

    private sealed class ThrowingPushQueue : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload) =>
            throw new InvalidOperationException("The push queue is down.");
    }
}
