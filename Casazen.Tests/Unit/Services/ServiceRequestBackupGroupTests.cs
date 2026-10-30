using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-BKP, decision of the product owner of 2026-10-29/30: a host's request goes to the supplier it chose (the primary) and, at the same
/// moment, to up to three backups, declared as such. The primary has one hour; the first backup that accepts has the job if it does not.
/// One test for each rule that decides who has the job, who sees what and who is told. The locks and the xmin checks need PostgreSQL
/// (<c>ServiceRequestGroupPostgresTests</c>); here the calls are one after the other, which is what the rules are about.
/// </summary>
public class ServiceRequestBackupGroupTests
{
    private static readonly TimeSpan PrimaryWindow = TimeSpan.FromMinutes(60);

    /// <summary>A backup of a group: its request and the supplier org it was sent to.</summary>
    private sealed record BackupSide(ServiceRequest Request, Guid SupplierOrgId)
    {
        public Guid Id => Request.Id;

        public string Member => $"auth0|member-of-{SupplierOrgId:N}";
    }

    // ─── Creation ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithTheFlagOn_SendsTheRequestToTheBackupsToo_DeclaredAsSuch_AndTheyHoldNoSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (alfa, beta) = await AddTwoBackupsAsync(s);
        s.ForgetNotifications();

        var primary = await s.RequestAsync(withService: false);

        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(s.HostOrgId, group.OrgId);
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, group.Phase);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime + PrimaryWindow, group.PrimaryDueAt);
        Assert.Null(group.BackupDueAt);

        var savedPrimary = await s.ReadAsync(primary.Id);
        Assert.Equal(group.Id, savedPrimary.GroupId);
        Assert.Equal(ServiceRequestGroupRole.Primary, savedPrimary.GroupRole);
        // The primary has the hour of the rule, not the 120 minutes of a request sent to one supplier.
        Assert.Equal(group.PrimaryDueAt, savedPrimary.ResponseDueAt);

        var backups = await BackupsOfAsync(s, group.Id);
        Assert.Equal(new[] { alfa, beta }.Order(), backups.Select(r => r.SupplierOrgId).Order());
        Assert.All(backups, backup =>
        {
            Assert.Equal(ServiceRequestStatus.Richiesto, backup.Status);
            Assert.Equal(group.Id, backup.GroupId);
            // The same job: host, property, stay, notes, time.
            Assert.Equal(s.HostOrgId, backup.OrgId);
            Assert.Equal(s.PropertyId, backup.PropertyId);
            Assert.Equal(s.BookingId, backup.BookingId);
            Assert.Equal(ServiceRequestScenario.HostNotes, backup.Notes);
            Assert.Equal(savedPrimary.ScheduledStartUtc, backup.ScheduledStartUtc);
            Assert.Equal(savedPrimary.ScheduledEndUtc, backup.ScheduledEndUtc);
            Assert.Null(backup.BackupAcceptedAt);
            Assert.Equal(group.PrimaryDueAt, backup.ResponseDueAt);
        });

        // A backup holds nothing of its agenda until it has the job; the primary's supplier does hold it (a request waiting counts).
        var day = RomeCalendar.DateInRome(savedPrimary.ScheduledStartUtc!.Value);
        Assert.Empty(await s.Reader.ListForAgendaAsync(alfa, day, day));
        Assert.Empty(await s.Reader.ListForAgendaAsync(beta, day, day));
        Assert.Single(await s.Reader.ListForAgendaAsync(s.SupplierOrgId, day, day));
    }

    [Fact]
    public async Task Create_TheBackupsAreTold_TheyAreBackups_WithTheComuneAndNeverThePropertyOrTheHostNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var alfa = await AddBackupAsync(s, "Alfa Srl");
        s.ForgetNotifications();

        var primary = await s.RequestAsync(withService: false);

        var invitation = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestBackupInvited);
        Assert.Equal("alfa-srl@test.com", invitation.To);
        var text = invitation.Content.Subject + invitation.Content.HtmlBody;
        Assert.Contains("fornitore di riserva", text);
        Assert.Contains(ServiceRequestScenario.Comune, text);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyName, text);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, text);
        Assert.DoesNotContain(ServiceRequestScenario.HostNotes, text);

        var backup = Assert.Single(await BackupsOfAsync(s, (await s.ReadAsync(primary.Id)).GroupId!.Value));
        var push = Assert.Single(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestBackupInvited);
        Assert.Equal(PushAudience.SupplierOrg(alfa), push.Audience);
        Assert.Equal(PushDeliveryKeys.ServiceRequestBackup(backup.Id, "invited"), push.DeliveryKey);
        // The chosen supplier still gets the email of a new request, and is not told about the backups.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCreated);
    }

    [Fact]
    public async Task Create_WithTheFlagOffTheOrgOffOrTheRequestOff_OrNobodyElseCoversTheComune_GoesToTheChosenSupplierAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var window = TimeSpan.FromMinutes(new Casazen.Core.Options.ServiceRequestOptions().HostResponseMinutes);

        // Nobody else in the comune: with the flag on, a single request, with the deadline of today.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        await AssertSentAloneAsync(s, (await s.RequestAsync(withService: false)).Id, window);

        await AddBackupAsync(s, "Alfa Srl");

        // The flag is off.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, false);
        await AssertSentAloneAsync(s, (await s.RequestAsync(withService: false)).Id, window);

        // The flag is on, the org turned the rule off.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var org = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        org.SupplierBackupsEnabled = false;
        await s.Db.SaveChangesAsync();
        await AssertSentAloneAsync(s, (await CreateAsync(s, useBackups: null)).Id, window);

        // The org turned it on again and this request asks to be sent alone.
        org.SupplierBackupsEnabled = true;
        await s.Db.SaveChangesAsync();
        await AssertSentAloneAsync(s, (await CreateAsync(s, useBackups: false)).Id, window);

        // Control: the same, asking for the backups, forms a group.
        var grouped = await CreateAsync(s, useBackups: null);
        Assert.NotNull((await s.ReadAsync(grouped.Id)).GroupId);
    }

    [Fact]
    public async Task Create_WhenTheHostPickedAPublishedSlot_OnlyTheBackupsThatHaveThatSlotFreeAreChosen()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var withoutHours = await AddBackupAsync(s, "Alfa Srl");
        var withHours = await AddBackupAsync(s, "Beta Srl");
        await s.Agenda.ReplaceRulesAsync(withHours, new SupplierRulesInput(30, 3, 0, 35, 60));
        await s.Agenda.ReplaceHoursAsync(withHours, ServiceRequestScenario.WorkingWeek());

        // The slot is one of the primary's published slots (a catalog service, no typed duration): each backup must have it too.
        var primary = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var backups = await BackupsOfAsync(s, (await s.ReadAsync(primary.Id)).GroupId!.Value);
        Assert.Equal(withHours, Assert.Single(backups).SupplierOrgId);
        Assert.DoesNotContain(backups, backup => backup.SupplierOrgId == withoutHours);
        Assert.True((await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).SlotPlanned);
    }

    // ─── What the host sees ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Host_SeesOnlyTheRequestItFollows_TheGroupWithEveryBackup_AndCannotActOnABackup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        var scope = new HostScope(s.HostOrgId);

        var (items, total) = await s.Service.ListForHostAsync(scope, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);

        Assert.Equal(1, total);
        Assert.Equal(primary.Id, Assert.Single(items).Id);
        Assert.Null(await s.Service.GetByIdForHostAsync(alfa.Id, scope, ServiceRequestRentalContext.ShortRent));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.CancelAsHostAsync(alfa.Id, s.HostOrgId, "no"));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RemindAsync(alfa.Id, s.HostOrgId));

        var group = Assert.Single(await s.Service.GetGroupViewsAsync(s.HostOrgId, [primary.GroupId!.Value])).Value;
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, group.Phase);
        Assert.Equal(ServiceRequestGroupRole.Primary, group.Members[0].Role);
        Assert.Equal("Supplier Org", group.Members[0].SupplierName);
        Assert.Equal(2, group.Members.Count(m => m.Role == ServiceRequestGroupRole.Backup));
        Assert.Equal(["Alfa Srl", "Beta Srl"], group.Members.Where(m => m.Role == ServiceRequestGroupRole.Backup).Select(m => m.SupplierName).Order());
        Assert.All(group.Members, member => Assert.Equal(ServiceRequestStatus.Richiesto, member.Status));
        // Another org does not read the group.
        Assert.Empty(await s.Service.GetGroupViewsAsync(Guid.NewGuid(), [primary.GroupId.Value]));
    }

    // ─── The primary takes the job ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimaryTakesTheJob_TheBackupsAreClosed_AndToldSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.ForgetNotifications();

        var taken = await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(ServiceRequestGroupPhase.Closed, group.Phase);
        Assert.NotNull(group.ClosedAt);
        foreach (var backup in new[] { alfa, beta })
        {
            var saved = await s.ReadAsync(backup.Id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
            Assert.Equal(ServiceRequestCancellationReasons.PrimaryTookIt, saved.CancellationReason);
            Assert.Equal(ServiceRequestGroupRole.Backup, saved.GroupRole);
        }

        Assert.Equal(2, s.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestBackupClosed));
        // The host is told of the take like for any request, not of backups.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com" && e.Template == EmailTemplates.Names.ServiceRequestStatusChanged);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestTakenByBackup);
    }

    [Fact]
    public async Task ThePrimaryThatTakesTheJobAfterItsHour_IsStillInTheRace_WhenNoBackupAccepted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);

        var late = await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, late.Status);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(alfa.Id)).Status);
    }

    [Fact]
    public async Task ThePrimaryThatTakesTheJobAfterItsHour_IsRefused_WhenABackupHoldsAPreApproval()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        s.Clock.Advance(PrimaryWindow);

        // The pass of the job has not run yet: the rule is the same, the primary cannot take the job from the backup that accepted in time.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));

        Assert.Equal(ServiceRequestGroupErrorCodes.PrimaryWindowPassed, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);
    }

    // ─── The primary is silent for an hour ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimarySilentForAnHour_TheJobGoesToTheBackupThatAcceptedFirst_AndTheHostSeesItInPlaceOfThePrimary()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);

        // Beta accepts first, Alfa three minutes later: the order of the acceptances decides, not the order of the list.
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.Service.AcceptAsBackupAsync(beta.Id, beta.SupplierOrgId, beta.Member);
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        // Before the end of the hour nothing happens.
        s.Clock.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());

        s.Clock.Advance(TimeSpan.FromMinutes(1));
        s.ForgetNotifications();
        var run = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 1, 0, 0, 0), run);
        var winner = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, winner.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, winner.GroupRole);
        Assert.Equal(beta.Member, winner.TakenByUserId);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, winner.TakenAt);
        Assert.Null(winner.ResponseDueAt);
        var other = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, other.Status);
        Assert.Equal(ServiceRequestCancellationReasons.TakenByBackup, other.CancellationReason);
        var lost = await s.ReadAsync(primary.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, lost.Status);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, lost.CancellationReason);
        Assert.Equal(ServiceRequestActorParty.System, lost.CancelledBy);
        Assert.Equal(ServiceRequestGroupRole.ReplacedPrimary, lost.GroupRole);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);

        // The host follows the backup that took the job, in the place of the primary, and is told who it is.
        var (items, _) = await s.Service.ListForHostAsync(new HostScope(s.HostOrgId), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);
        Assert.Equal(beta.Id, Assert.Single(items).Id);
        var toHost = Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestTakenByBackup, toHost.Template);
        Assert.Contains("Beta Srl", toHost.Content.HtmlBody);

        // The winner is told the job is its; the primary that did not answer and the other backup are told theirs.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "beta-srl@test.com" && e.Template == EmailTemplates.Names.ServiceRequestBackupWon);
        Assert.Single(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com" && e.Template == EmailTemplates.Names.ServiceRequestBackupClosed);
        Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier);
    }

    [Fact]
    public async Task PrimarySilent_TheBackupWhoseTimeIsNoLongerFree_IsPassedOver_AndTheNextOneHasTheJob()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Service.AcceptAsBackupAsync(beta.Id, beta.SupplierOrgId, beta.Member);

        // Alfa takes another job at that time before the hour is over: a backup held nothing, so nothing stopped it.
        var savedPrimary = await s.ReadAsync(primary.Id);
        await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.ScheduledStartUtc = savedPrimary.ScheduledStartUtc;
                r.ScheduledEndUtc = savedPrimary.ScheduledEndUtc;
                r.TakenAt = ServiceRequestScenario.Instant.UtcDateTime;
            },
            supplierOrgId: alfa.SupplierOrgId);
        s.Clock.Advance(PrimaryWindow);
        s.ForgetNotifications();

        var run = await Job(s).RunAsync();

        Assert.Equal(1, run.Closed);
        var dropped = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, dropped.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupSlotUnavailable, dropped.CancellationReason);
        var winner = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, winner.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, winner.GroupRole);
        var toAlfa = Assert.Single(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestBackupClosed, toAlfa.Template);
        Assert.Contains("non risultava più libero", toAlfa.Content.HtmlBody);
    }

    [Fact]
    public async Task PrimaryRefuses_TheBackupThatAcceptedHasTheJobAtOnce_WithoutWaitingForTheHour()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        await s.Service.RejectAsync(primary.Id, s.SupplierOrgId, "Sono fuori sede");

        var winner = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, winner.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, winner.GroupRole);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(beta.Id)).Status);
        // It refused: the primary stays refused, and the host follows the backup that took the job.
        Assert.Equal(ServiceRequestStatus.Rifiutato, (await s.ReadAsync(primary.Id)).Status);
        var (items, _) = await s.Service.ListForHostAsync(new HostScope(s.HostOrgId), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);
        Assert.Equal(alfa.Id, Assert.Single(items).Id);
    }

    // ─── Nobody accepted ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBackupAccepted_TheSecondWindowOpens_TheFirstToTakeHasTheJob_AndTheOtherIsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        s.ForgetNotifications();

        var opened = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 0, 1, 0, 0), opened);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(ServiceRequestGroupPhase.BackupWindow, group.Phase);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(60), group.BackupDueAt);
        // The primary is still in the race, now without the priority; nobody is closed yet.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);
        Assert.Equal(group.BackupDueAt, (await s.ReadAsync(primary.Id)).ResponseDueAt);
        Assert.Equal(2, s.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestBackupActive));

        // Two backups try to take it: one has it, the other is refused and closed.
        await s.Service.TakeAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        var refused = await Assert.ThrowsAnyAsync<DomainException>(() => s.Service.TakeAsync(beta.Id, beta.SupplierOrgId, beta.Member));
        Assert.Contains(refused.Code, new[] { ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.InvalidTransition });

        var all = await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.GroupId == group.Id).ToListAsync();
        Assert.Equal(alfa.Id, Assert.Single(all, r => r.TakenAt != null).Id);
        Assert.Equal(ServiceRequestCancellationReasons.TakenByBackup, (await s.ReadAsync(beta.Id)).CancellationReason);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, (await s.ReadAsync(primary.Id)).CancellationReason);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
    }

    [Fact]
    public async Task NobodyTakesTheJobInTheSecondWindow_EveryRequestLapses_AsASingleRequestDoes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var run = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 1, 0, 0, 0), run);
        foreach (var id in new[] { primary.Id, alfa.Id, beta.Id })
        {
            var saved = await s.ReadAsync(id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
            Assert.Equal(ServiceRequestCancellationReasons.NoResponse, saved.CancellationReason);
        }

        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        var toHost = Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com");
        Assert.Contains("nessuna risposta", toHost.Content.Subject);
        // A run repeated finds nothing left, and tells nobody twice.
        var sent = s.Emails.Snapshot().Count;
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
        Assert.Equal(sent, s.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task TheLegacyAutoCancel_NeverClosesTheRequestsOfAGroup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await CreateGroupAsync(s);
        s.Clock.Advance(TimeSpan.FromHours(5));

        var due = await ServiceRequestAutoCancelService.DueIdsOf(s.Db, s.Clock.GetUtcNow().UtcDateTime).ToListAsync();

        Assert.Empty(due);
    }

    // ─── The rules for the suppliers ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABackup_CannotTakeTheJobWhileThePrimaryHasItsHour_ButCanAcceptAsABackup_Once()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupWaitsForPrimary, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);
        var proposal = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ProposeTimeAsync(
            alfa.Id,
            alfa.SupplierOrgId,
            alfa.Member,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(2))));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupCannotPropose, proposal.Code);

        s.Clock.Advance(TimeSpan.FromMinutes(7));
        var accepted = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        var firstInstant = accepted.BackupAcceptedAt;
        s.Clock.Advance(TimeSpan.FromMinutes(7));
        var again = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(-7), firstInstant);
        Assert.Equal(firstInstant, again.BackupAcceptedAt);
        Assert.Equal(alfa.Member, (await s.ReadAsync(alfa.Id)).BackupAcceptedByUserId);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);

        // Accepting as a backup is for a backup that waits: the primary's request is not one, nor is another supplier's.
        var notABackup = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AcceptAsBackupAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));
        Assert.Equal(ServiceRequestGroupErrorCodes.NotAWaitingBackup, notABackup.Code);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.AcceptAsBackupAsync(alfa.Id, s.SupplierOrgId, "auth0|x"));
    }

    [Fact]
    public async Task ABackup_SeesWhatASupplierSeesBeforeTheTake_NeverThePropertyNorTheHostNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        var savedPrimary = await s.ReadAsync(primary.Id);

        var view = await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId);
        var (listed, _) = await s.Reader.ListAsync(alfa.SupplierOrgId, new SupplierInboxQuery([ServiceRequestStatus.Richiesto], null, null, 1, 20));

        Assert.NotNull(view);
        Assert.False(view.ContactDisclosed);
        Assert.Null(view.Notes);
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Location.Address);
        Assert.Null(view.HostContact);
        Assert.Equal(ServiceRequestScenario.Comune, view.Location.City);
        Assert.Equal(savedPrimary.ScheduledStartUtc, view.Schedule.StartUtc);
        Assert.Equal(savedPrimary.ResponseDueAt, view.Schedule.RespondBy);
        var backup = Assert.IsType<SupplierJobBackup>(view.Backup);
        Assert.Equal(SupplierBackupRoles.Backup, backup.Role);
        Assert.Equal(SupplierBackupStates.Waiting, backup.State);
        Assert.Equal(savedPrimary.ResponseDueAt, backup.PrimaryDueAt);
        var inInbox = Assert.Single(listed);
        Assert.Null(inInbox.Notes);
        Assert.Null(inInbox.Location.PropertyName);

        // The chosen supplier sees its own request with the priority of its hour.
        var primaryView = await s.Reader.GetAsync(primary.Id, s.SupplierOrgId);
        Assert.Equal(SupplierBackupRoles.Primary, primaryView!.Backup!.Role);
        Assert.Equal(SupplierBackupStates.Priority, primaryView.Backup.State);

        // Once the backup has the job it is a request like any other: the property and the notes come with the take.
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        Assert.Equal(SupplierBackupStates.Accepted, (await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId))!.Backup!.State);
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        var won = await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId);
        Assert.NotNull(won);
        Assert.True(won.ContactDisclosed);
        Assert.Equal(ServiceRequestScenario.PropertyName, won.Location.PropertyName);
        Assert.Equal(ServiceRequestScenario.HostNotes, won.Notes);
        Assert.Equal(SupplierBackupStates.Won, won.Backup!.State);

        // The primary that did not answer has a cancelled request and no more to do with the group.
        var lost = await s.Reader.GetAsync(primary.Id, s.SupplierOrgId);
        Assert.Equal(ServiceRequestStatus.Annullato, lost!.Status);
        Assert.Null(lost.Backup);
    }

    [Fact]
    public async Task TheHostCancels_TheBackupsAreClosed_WithoutTheHostsWords()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.ForgetNotifications();

        await s.Service.CancelAsHostAsync(primary.Id, s.HostOrgId, "Il cliente ha disdetto, grazie");

        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        foreach (var backup in new[] { alfa, beta })
        {
            var saved = await s.ReadAsync(backup.Id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestCancellationReasons.RequestCancelledByHost, saved.CancellationReason);
        }

        var toBackups = s.Emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServiceRequestBackupClosed).ToList();
        Assert.Equal(2, toBackups.Count);
        Assert.All(toBackups, email => Assert.DoesNotContain("disdetto", email.Content.Subject + email.Content.HtmlBody));
    }

    [Fact]
    public async Task APrimaryWithAProposedTimeIsTheHostsToAnswer_TheGroupDoesNotMoveUntilTheHostDecides()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        await s.Service.ProposeTimeAsync(
            primary.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(2)));
        s.Clock.Advance(PrimaryWindow + TimeSpan.FromMinutes(30));

        var waiting = await Job(s).RunAsync();

        // The window passed, but the supplier did answer: nobody is given the job over the host's head.
        Assert.Equal(ServiceRequestGroupRun.Empty with { Examined = 1 }, waiting);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);

        // The host turns the time down: the hour starts again for the primary and the backups together.
        await s.Service.RejectProposalAsync(primary.Id, s.HostOrgId);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime + PrimaryWindow, group.PrimaryDueAt);
        Assert.Equal(group.PrimaryDueAt, (await s.ReadAsync(primary.Id)).ResponseDueAt);
        Assert.Equal(group.PrimaryDueAt, (await s.ReadAsync(alfa.Id)).ResponseDueAt);
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
    }

    [Fact]
    public async Task Job_RunsTheServiceOnce_AndWarnsOnlyWhenAGroupFailed()
    {
        var service = new Mock<IServiceRequestGroupRunService>();
        service.SetupSequence(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceRequestGroupRun(false, 3, 2, 1, 0, 0))
            .ReturnsAsync(new ServiceRequestGroupRun(false, 3, 1, 0, 1, 2))
            .ReturnsAsync(ServiceRequestGroupRun.LockTaken);
        var quiet = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestGroupJob>();
        var noisy = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestGroupJob>();

        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, quiet).ExecuteAsync(CancellationToken.None);
        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, noisy).ExecuteAsync(CancellationToken.None);
        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, quiet).ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain(quiet.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
        var warning = Assert.Single(noisy.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("2 failed", warning.Message);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ServiceRequestGroupRunService Job(ServiceRequestScenario s) =>
        new(s.Db, s.Service, NullLogger<ServiceRequestGroupRunService>.Instance, s.Clock);

    /// <summary>An active supplier of the scenario's comune and category: a possible backup. Its e-mail is its legal name, dashed.</summary>
    private static async Task<Guid> AddBackupAsync(ServiceRequestScenario s, string legalName)
    {
        var email = $"{legalName.Replace(' ', '-').ToLowerInvariant()}@test.com";
        var org = new Casazen.Core.Entities.Org
        {
            Name = legalName,
            Slug = $"bk-{Guid.NewGuid():N}"[..20],
            DisplayName = legalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        s.Db.Orgs.Add(org);
        s.Db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = legalName,
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{ServiceRequestScenario.Comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return org.Id;
    }

    /// <summary>The backups switched on and two backups in the comune, "Alfa Srl" and "Beta Srl".</summary>
    private static async Task<(Guid Alfa, Guid Beta)> AddTwoBackupsAsync(ServiceRequestScenario s)
    {
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        return (await AddBackupAsync(s, "Alfa Srl"), await AddBackupAsync(s, "Beta Srl"));
    }

    /// <summary>A request of the host sent to the scenario's supplier and to two backups, "Alfa Srl" and "Beta Srl".</summary>
    private static async Task<(ServiceRequest Primary, BackupSide Alfa, BackupSide Beta)> CreateGroupAsync(ServiceRequestScenario s)
    {
        var (alfa, beta) = await AddTwoBackupsAsync(s);
        var primary = await CreateAsync(s, useBackups: null);

        var saved = await s.ReadAsync(primary.Id);
        var backups = await BackupsOfAsync(s, saved.GroupId!.Value);
        return (saved, new BackupSide(backups.Single(b => b.SupplierOrgId == alfa), alfa), new BackupSide(backups.Single(b => b.SupplierOrgId == beta), beta));
    }

    private static Task<ServiceRequest> CreateAsync(ServiceRequestScenario s, bool? useBackups) =>
        s.Service.CreateAsync(new CreateServiceRequestCommand(
            s.HostOrgId,
            ServiceRequestScenario.HostUserId,
            s.PropertyId,
            s.BookingId,
            s.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            ServiceRequestScenario.HostNotes,
            ChargeToGuest: false,
            ServiceRequestRentalContext.ShortRent,
            ServiceListingId: null,
            ScheduledStartUtc: null,
            DurationMinutes: ServiceRequestScenario.ServiceMinutes,
            UseBackupSuppliers: useBackups));

    private static async Task<List<ServiceRequest>> BackupsOfAsync(ServiceRequestScenario s, Guid groupId) =>
        await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.GroupId == groupId && r.GroupRole == ServiceRequestGroupRole.Backup)
            .ToListAsync();

    private static async Task AssertSentAloneAsync(ServiceRequestScenario s, Guid requestId, TimeSpan window)
    {
        var saved = await s.ReadAsync(requestId);
        Assert.Null(saved.GroupId);
        Assert.Equal(ServiceRequestGroupRole.None, saved.GroupRole);
        Assert.Equal(saved.CreatedAt + window, saved.ResponseDueAt);
    }
}
