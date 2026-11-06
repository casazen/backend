using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The guided steps of the Octorate panel (E10/F01/US03/T01, NF-107): the handoff stored before the address leaves, the verification on a schedule
/// that never burns the quota, the confirmation only as the fallback, the callback with a state used once, and the reach of the caller. EF InMemory and
/// the world of <see cref="HostScopeScenario"/>; the verifiers are scripted, so what is proved is the service, not Octorate.
/// </summary>
public class OnboardingPanelStepServiceTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly OctorateOptions _octorate = new()
    {
        FrontendBaseUrl = "https://panel.octorate.test/",
        RedirectUri = "https://app.casazen.test/octorate/callback",
    };

    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ─── Start ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data, privacy): the step, the single hand-over (the hash of a nonce, its expiry, the mode) and the point to resume are stored before the
    /// address is returned, then ONE "To do" is raised (codes and ids only, due tomorrow at 09:00 in Rome); the address is the configured panel as it
    /// is, with no org, property or person in it, and no nonce leaves for a panel that sends nobody back. A second start is the same step, same "To do".
    /// </summary>
    [Theory]
    [InlineData(PanelStepKind.PoliceConnection, null)]
    [InlineData(PanelStepKind.IstatConnection, null)]
    [InlineData(PanelStepKind.ChannelAccess, "booking_xml")]
    public async Task Start_StoresTheHandoffBeforeTheAddressLeaves_AndRaisesTheTodo(PanelStepKind kind, string? channel)
    {
        var service = Service();
        var propertyId = _world.Granted.Id;
        var request = new PanelStepStartRequest(kind, propertyId, channel, WizardType.OnboardingActivation, propertyId, "compliance.police");

        var started = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, request, Ct);

        Assert.Equal("https://panel.octorate.test/", started.PanelUrl);
        Assert.Null(started.CallbackUrl);
        foreach (var personal in new[]
                 {
                     _world.OrgId.ToString(), _world.OrgId.ToString("N"), propertyId.ToString(), propertyId.ToString("N"), _world.OwnerUserId, _world.Granted.Name,
                 })
        {
            Assert.DoesNotContain(personal, started.PanelUrl, StringComparison.OrdinalIgnoreCase);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal(started.Step.Id, row.Id);
        Assert.Equal((_world.OrgId, (Guid?)propertyId, kind), (row.OrgId, row.PropertyId, row.Kind));
        Assert.Equal(channel, row.ChannelCode);
        Assert.Equal((PanelStepStatus.Started, PanelHandoffMode.ManagedWindow), (row.Status, row.HandoffMode));
        Assert.Equal(now, row.StartedAt);
        Assert.Equal(now + PanelStepLimits.BackoffAfter(1), row.NextVerificationAt);
        Assert.Equal(0, row.VerificationAttempts);
        Assert.Null(row.LastVerificationOutcome);
        Assert.Matches("^[0-9a-f]{64}$", row.HandoffNonceHash);
        Assert.Equal(now.AddMinutes(PanelStepLimits.StateLifetimeMinutes), row.HandoffExpiresAt);
        Assert.Null(row.CallbackConsumedAt);

        // The point to resume: the wizard, what it edits and the step, a reference and no copy.
        Assert.Equal((WizardType.OnboardingActivation, (Guid?)propertyId, "compliance.police"), (row.ResumeWizardType, row.ResumeDraftEntityId, row.ResumeStepCode));
        Assert.Equal(("compliance.police", (Guid?)propertyId), (started.Step.Resume.StepCode, started.Step.Resume.DraftEntityId));

        // The "To do": created after the step, found on it, with codes and ids only.
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync();
        Assert.Equal(todo.Id, row.ActivityId);
        Assert.Equal(nameof(ActivityType.WizardResume), todo.ActivityType);
        Assert.Equal(ActivityAction.ResumeWizard, todo.Action);
        Assert.Equal(OnboardingPanelStepCodes.TodoEntityType, todo.EntityType);
        Assert.Equal(row.Id, todo.EntityId);
        Assert.Equal(OnboardingPanelStepCodes.ReasonPending, todo.Reason);
        Assert.Equal((_world.OrgId, (Guid?)propertyId), (todo.OrgId, todo.PropertyId));
        Assert.Equal(new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), todo.Deadline);
        Assert.Null(todo.Notes);
        Assert.Null(todo.StaffUserId);
        Assert.Null(todo.CreatedByUserId);

        // Idempotent: the same step and the same "To do"; a new hand-over replaces the old nonce.
        var again = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, request, Ct);
        Assert.Equal(started.Step.Id, again.Step.Id);
        Assert.Single(await _db.OnboardingPanelSteps.ToListAsync());
        Assert.Single(await _db.InternalActivities.ToListAsync());
        Assert.NotEqual(row.HandoffNonceHash, (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).HandoffNonceHash);
    }

    [Fact]
    public async Task Start_RefusesWhatIsNotValid()
    {
        var service = Service();
        var owner = _world.OwnerUserId;

        var kind = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest((PanelStepKind)0), Ct));
        var noChannel = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.ChannelAccess), Ct));
        var surplusChannel = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.PoliceConnection, null, "booking_xml"), Ct));
        var longChannel = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.ChannelAccess, null, new string('c', 41)), Ct));
        var longStep = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.IstatConnection, null, null, null, null, new string('s', 51)), Ct));
        var emptyDraft = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.IstatConnection, null, null, null, Guid.Empty, null), Ct));

        Assert.Equal(OnboardingPanelStepCodes.KindInvalid, kind.Code);
        Assert.Equal(OnboardingPanelStepCodes.ChannelInvalid, noChannel.Code);
        Assert.Equal(OnboardingPanelStepCodes.ChannelInvalid, surplusChannel.Code);
        Assert.Equal(OnboardingPanelStepCodes.ChannelInvalid, longChannel.Code);
        Assert.Equal(OnboardingPanelStepCodes.ResumeInvalid, longStep.Code);
        Assert.Equal(OnboardingPanelStepCodes.ResumeInvalid, emptyDraft.Code);
        Assert.Empty(await _db.OnboardingPanelSteps.ToListAsync());
        Assert.Empty(await _db.InternalActivities.ToListAsync());
    }

    /// <summary>The address of the panel must be configured, https, for a named host and without a query: otherwise nothing is stored and no address leaves.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://panel.octorate.test/")]
    [InlineData("https://panel.octorate.test/?org=1")]
    [InlineData("https://user:secret@panel.octorate.test/")]
    public async Task Start_WithoutAValidPanelAddress_Is409_AndStoresNothing(string? address)
    {
        _octorate.FrontendBaseUrl = address;

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            Service().StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct));

        Assert.Equal("octorate_panel_url_missing", conflict.Code);
        Assert.Empty(await _db.OnboardingPanelSteps.ToListAsync());
    }

    /// <summary>
    /// Proof (permission): a collaborator «Solo alcuni» starts, lists, reads, verifies and confirms only the steps of the properties it was given, never
    /// a step of the whole org, and the step of a property it does not reach is the 404 of a step that does not exist.
    /// </summary>
    [Fact]
    public async Task Steps_ReachOnlyWhatTheScopeReaches()
    {
        var service = Service();
        var owner = _world.OwnerUserId;
        var granted = (await service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct)).Step;
        var hidden = (await service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Hidden.Id), Ct)).Step;
        var wholeOrg = (await service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct)).Step;
        var restricted = _world.Restricted;
        var collaborator = _world.CollaboratorId;

        // Start: the property it was given only, never the whole org.
        var mine = (await service.StartAsync(restricted, collaborator, new PanelStepStartRequest(PanelStepKind.IstatConnection, _world.Granted.Id), Ct)).Step;
        var notMine = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.StartAsync(restricted, collaborator, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Hidden.Id), Ct));
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.OtherOrgProperty.Id), Ct));
        var org = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
            service.StartAsync(restricted, collaborator, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, notMine.Code);
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, foreign.Code);
        Assert.Equal(OnboardingPanelStepCodes.NeedsWholeOrg, org.Code);

        // List: the owner reads all four; the collaborator its own two; the account that reaches what it created, the two of its houses.
        Assert.Equal(4, (await service.ListAsync(_world.OrgWide, Ct)).Count);
        Assert.Equal(
            new[] { granted.Id, mine.Id }.Order(),
            (await service.ListAsync(restricted, Ct)).Select(s => s.Id).Order());
        Assert.Equal(
            new[] { granted.Id, hidden.Id, mine.Id }.Order(),
            (await service.ListAsync(_world.OwnedByOwner, Ct)).Select(s => s.Id).Order());
        Assert.Empty(await service.ListAsync(new HostScope(_world.OtherOrgId), Ct));

        // By id: a step it does not reach is a step that does not exist, for every operation.
        foreach (var hiddenId in new[] { hidden.Id, wholeOrg.Id, Guid.NewGuid() })
        {
            var status = await Assert.ThrowsAsync<NotFoundException>(() => service.GetStatusAsync(restricted, hiddenId, Ct));
            var verify = await Assert.ThrowsAsync<NotFoundException>(() => service.VerifyAsync(restricted, hiddenId, Ct));
            var confirm = await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmAsync(restricted, collaborator, hiddenId, Ct));
            var callback = await Assert.ThrowsAsync<NotFoundException>(() => service.CompleteCallbackAsync(restricted, hiddenId, "x", Ct));
            Assert.All(new[] { status.Code, verify.Code, confirm.Code, callback.Code }, code => Assert.Equal(OnboardingPanelStepCodes.NotFound, code));
        }

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetStatusAsync(new HostScope(_world.OtherOrgId), granted.Id, Ct));
        Assert.Equal(granted.Id, (await service.GetStatusAsync(restricted, granted.Id, Ct)).Id);
    }

    // ─── Verification ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (money-like quota rule): the verifier runs only when the stored schedule says so. Any number of polls between two checks calls it
    /// once; the wait after each check doubles from 30 seconds up to 600 and stays there.
    /// </summary>
    [Fact]
    public async Task Status_RunsTheVerifierOnlyWhenDue_AndTheWaitDoublesUpToTheMaximum()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection).Always(PanelVerificationOutcome.NotValid);
        var service = Service(police);
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct)).Step;

        // A page that polls every few seconds before the first check calls nothing.
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            var polled = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);
            Assert.Equal(step.NextCheckAt, polled.NextCheckAt);
        }

        Assert.Equal(0, police.Calls);

        double[] waits = [30, 60, 120, 240, 480, 600, 600, 600];
        var expectedCalls = 0;
        foreach (var wait in waits)
        {
            var due = (await service.GetStatusAsync(_world.OrgWide, step.Id, Ct)).NextCheckAt!.Value;
            _clock.Advance(due - _clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1));
            for (var i = 0; i < 25; i++)
                await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);

            Assert.Equal(expectedCalls, police.Calls);

            _clock.Advance(TimeSpan.FromSeconds(1));
            var checkedNow = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);
            expectedCalls++;
            Assert.Equal(expectedCalls, police.Calls);
            Assert.Equal(TimeSpan.FromSeconds(wait), checkedNow.NextCheckAt - _clock.GetUtcNow().UtcDateTime);
            Assert.Equal(PanelStepStatus.NotValid, checkedNow.Status);
        }

        Assert.Equal(waits.Length, (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).VerificationAttempts);
        Assert.All(police.Contexts, context => Assert.Equal((_world.OrgId, (Guid?)_world.Granted.Id, PanelStepKind.PoliceConnection), (context.OrgId, context.PropertyId, context.Kind)));
    }

    [Fact]
    public async Task Status_Verified_CompletesTheStep_ClosesTheTodo_AndStopsTheChecks()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection).Then(PanelVerificationOutcome.Verified);
        var service = Service(police);
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct)).Step;

        _clock.Advance(TimeSpan.FromSeconds(31));
        var done = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);

        Assert.Equal((PanelStepStatus.Verified, PanelVerificationOutcomes.Verified, false), (done.Status, done.LastVerificationOutcome, done.CanConfirm));
        Assert.Null(done.NextCheckAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, done.CompletedAt);
        var row = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, row.VerifiedAt);
        Assert.Null(row.CompletedByUserId);
        Assert.Null(row.NextVerificationAt);
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync();
        Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (todo.Status, todo.ClosedBy));

        // Nothing is asked again, a start of a step already done restarts nothing, and the host cannot be made to confirm it twice.
        _clock.Advance(TimeSpan.FromHours(2));
        await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);
        await service.VerifyAsync(_world.OrgWide, step.Id, Ct);
        var restarted = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct);
        Assert.Equal(1, police.Calls);
        Assert.Equal(PanelStepStatus.Verified, restarted.Step.Status);
        Assert.Equal(PanelStepStatus.Verified, (await service.ConfirmAsync(_world.OrgWide, _world.OwnerUserId, step.Id, Ct)).Status);
    }

    /// <summary>
    /// Proof (trust): the confirmation is the technical fallback. It is 409 before any real check, 409 after a check found the step not valid, 409 while
    /// support has the step, and allowed only after a check stored «could not tell»; it then completes the step with the host as who confirmed it.
    /// </summary>
    [Fact]
    public async Task Confirm_IsAllowedOnlyAfterACheckCouldNotTell()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection)
            .Then(PanelVerificationOutcome.NotValid)
            .Then(PanelVerificationOutcome.Unverifiable);
        var service = Service(police);
        var owner = _world.OwnerUserId;
        var step = (await service.StartAsync(_world.OrgWide, owner, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct)).Step;

        // Before any check, also after the explicit asking of a page that has not been checked yet: refused.
        var before = await Assert.ThrowsAsync<DomainConflictException>(() => service.ConfirmAsync(_world.OrgWide, owner, step.Id, Ct));
        Assert.Equal(OnboardingPanelStepCodes.VerificationRequired, before.Code);

        // A check found it not valid: refused, with the other code.
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(PanelStepStatus.NotValid, (await service.GetStatusAsync(_world.OrgWide, step.Id, Ct)).Status);
        var invalid = await Assert.ThrowsAsync<DomainConflictException>(() => service.ConfirmAsync(_world.OrgWide, owner, step.Id, Ct));
        Assert.Equal(OnboardingPanelStepCodes.NotValid, invalid.Code);

        // The next check could not tell: the step is open again and may be confirmed.
        _clock.Advance(TimeSpan.FromSeconds(61));
        var open = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);
        Assert.Equal((PanelStepStatus.Started, PanelVerificationOutcomes.Unverifiable, true), (open.Status, open.LastVerificationOutcome, open.CanConfirm));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var confirmed = await service.ConfirmAsync(_world.OrgWide, owner, step.Id, Ct);
        Assert.Equal((PanelStepStatus.Confirmed, false), (confirmed.Status, confirmed.CanConfirm));
        Assert.Null(confirmed.NextCheckAt);
        var row = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal((owner, (DateTime?)_clock.GetUtcNow().UtcDateTime), (row.CompletedByUserId, row.CompletedAt));
        Assert.Null(row.VerifiedAt);
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync();
        Assert.Equal((InternalActivity.StatusCompleted, owner), (todo.Status, todo.ClosedBy));

        // Again: the same answer, nothing changes.
        Assert.Equal(PanelStepStatus.Confirmed, (await service.ConfirmAsync(_world.OrgWide, owner, step.Id, Ct)).Status);
    }

    [Fact]
    public async Task Confirm_WhileSupportHasTheStep_Is409()
    {
        var service = Service();
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct)).Step;
        var row = await _db.OnboardingPanelSteps.SingleAsync(s => s.Id == step.Id);
        row.Status = PanelStepStatus.AssistRequested;
        row.LastVerificationOutcome = PanelVerificationOutcomes.Unverifiable;
        await _db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => service.ConfirmAsync(_world.OrgWide, _world.OwnerUserId, step.Id, Ct));

        Assert.Equal(OnboardingPanelStepCodes.AssistOpen, conflict.Code);
        // A step support has is answered as it is by a new start, and is not checked.
        var again = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct);
        Assert.Equal(PanelStepStatus.AssistRequested, again.Step.Status);
        Assert.Null(again.Step.NextCheckAt);
    }

    /// <summary>
    /// A check that fails (Octorate down, the quota used) decides nothing: it is logged by code, counts as an attempt, moves the schedule on, stores no
    /// outcome and so never unlocks the confirmation. A cancelled call is not swallowed.
    /// </summary>
    [Fact]
    public async Task Status_AVerifierThatFails_DecidesNothing_AndNeverUnlocksTheConfirmation()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection)
            .ThenThrow(new OctorateServerException(503))
            .ThenThrow(new InvalidOperationException("boom"))
            .ThenThrow(new OperationCanceledException());
        var service = Service(police);
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct)).Step;

        _clock.Advance(TimeSpan.FromSeconds(31));
        var failed = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);

        Assert.Equal(PanelStepStatus.Started, failed.Status);
        Assert.Null(failed.LastVerificationOutcome);
        Assert.False(failed.CanConfirm);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + PanelStepLimits.BackoffAfter(1), (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).NextVerificationAt);
        Assert.Equal(1, (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).VerificationAttempts);
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => service.ConfirmAsync(_world.OrgWide, _world.OwnerUserId, step.Id, Ct));
        Assert.Equal(OnboardingPanelStepCodes.VerificationRequired, refused.Code);

        // The next one, due later, fails with an unexpected error: same, and the page still answers.
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, police.Calls);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);
        Assert.Equal(2, police.Calls);
        Assert.Null(second.LastVerificationOutcome);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetStatusAsync(_world.OrgWide, step.Id, Ct));
    }

    /// <summary>The explicit trigger (the panel window closed, the tab focused again) runs a check at once, but not twice within ten seconds.</summary>
    [Fact]
    public async Task Verify_TheExplicitTrigger_IsHeldToAMinimumGapFromThePreviousCheck()
    {
        var istat = new ScriptedVerifier(PanelStepKind.IstatConnection).Always(PanelVerificationOutcome.NotValid);
        var service = Service(istat);
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.IstatConnection), Ct)).Step;

        // A first check before the schedule: nothing was checked yet.
        await service.VerifyAsync(_world.OrgWide, step.Id, Ct);
        Assert.Equal(1, istat.Calls);

        // A click-happy page: the next ones within ten seconds are the stored state.
        _clock.Advance(TimeSpan.FromSeconds(3));
        await service.VerifyAsync(_world.OrgWide, step.Id, Ct);
        _clock.Advance(TimeSpan.FromSeconds(6));
        await service.VerifyAsync(_world.OrgWide, step.Id, Ct);
        Assert.Equal(1, istat.Calls);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var again = await service.VerifyAsync(_world.OrgWide, step.Id, Ct);
        Assert.Equal(2, istat.Calls);
        Assert.Equal(PanelStepStatus.NotValid, again.Status);
        Assert.Equal(TimeSpan.FromSeconds(60), again.NextCheckAt - _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The verifier with the highest order of the kind is the only one that runs; the default of order 0 stays out of it.</summary>
    [Fact]
    public async Task TheVerifierWithTheHighestOrderOfTheKind_IsTheOneThatRuns()
    {
        var defaultVerifier = new ScriptedVerifier(PanelStepKind.ChannelAccess, order: 0).Always(PanelVerificationOutcome.Unverifiable);
        var marketplace = new ScriptedVerifier(PanelStepKind.ChannelAccess, order: 10).Always(PanelVerificationOutcome.Verified);
        var other = new ScriptedVerifier(PanelStepKind.PoliceConnection, order: 99).Always(PanelVerificationOutcome.NotValid);
        var service = Service(defaultVerifier, marketplace, other);
        var step = (await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.ChannelAccess, null, "booking_xml"), Ct)).Step;

        _clock.Advance(TimeSpan.FromSeconds(31));
        var checkedStep = await service.GetStatusAsync(_world.OrgWide, step.Id, Ct);

        Assert.Equal(PanelStepStatus.Verified, checkedStep.Status);
        Assert.Equal((0, 1, 0), (defaultVerifier.Calls, marketplace.Calls, other.Calls));
        Assert.Equal("booking_xml", Assert.Single(marketplace.Contexts).ChannelCode);
    }

    // ─── The callback ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (privacy, trust): a panel that sends the person back gets a callback address with the step and a state; only the hash of the state is
    /// stored. The state is valid once and before its expiry, the same refusal tells nothing about why, and a step with no callback refuses it.
    /// </summary>
    [Fact]
    public async Task Callback_TheStateIsUsedOnce_ExpiresAndIsNeverStoredInClear()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection, order: 10) { SupportsCallback = true }.Then(PanelVerificationOutcome.Verified);
        var service = Service(police);
        var started = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct);

        Assert.Equal(PanelHandoffMode.RedirectCallback, started.Step.HandoffMode);
        var callback = new Uri(Assert.IsType<string>(started.CallbackUrl));
        Assert.Equal("https://app.casazen.test/octorate/callback", callback.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal(started.Step.Id.ToString("D"), query["step"].ToString());
        var state = query["state"].ToString();
        Assert.True(state.Length >= 40);

        // The row holds the hash and not the state; nothing else of it holds the state or the org or the person.
        var row = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state))), row.HandoffNonceHash);
        Assert.DoesNotContain(state, string.Join('|', row.HandoffNonceHash, row.ResumeStepCode, row.ChannelCode, row.LastVerificationOutcome), StringComparison.Ordinal);
        foreach (var personal in new[] { _world.OrgId.ToString("N"), _world.OwnerUserId })
            Assert.DoesNotContain(personal, callback.ToString(), StringComparison.OrdinalIgnoreCase);

        // A state that is not the one: the same refusal for unknown, empty and foreign states, and the real one is still good.
        foreach (var wrong in new[] { "", "  ", "x", new string('a', PanelStepLimits.StateMaxLength + 1), state[..^1] + (state[^1] == 'A' ? "B" : "A") })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteCallbackAsync(_world.OrgWide, started.Step.Id, wrong, Ct));
            Assert.Equal(OnboardingPanelStepCodes.StateInvalid, refused.Code);
        }

        Assert.Null((await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).CallbackConsumedAt);
        Assert.Equal(0, police.Calls);

        // The real state: consumed, the step verified at once (the person just came back).
        var done = await service.CompleteCallbackAsync(_world.OrgWide, started.Step.Id, state, Ct);
        Assert.Equal(PanelStepStatus.Verified, done.Status);
        Assert.Equal(1, police.Calls);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).CallbackConsumedAt);

        // Used once: the second presentation of the same state is the same refusal.
        var replay = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteCallbackAsync(_world.OrgWide, started.Step.Id, state, Ct));
        Assert.Equal(OnboardingPanelStepCodes.StateInvalid, replay.Code);
        Assert.Equal(1, police.Calls);
    }

    [Fact]
    public async Task Callback_AnExpiredState_AndAStateOfAnotherStep_AreRefused()
    {
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection, order: 10) { SupportsCallback = true }.Always(PanelVerificationOutcome.Verified);
        var service = Service(police);
        var first = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct);
        var second = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Hidden.Id), Ct);
        var firstState = StateOf(first);
        var secondState = StateOf(second);

        // The state of the other step does not open this one.
        var foreign = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteCallbackAsync(_world.OrgWide, first.Step.Id, secondState, Ct));
        Assert.Equal(OnboardingPanelStepCodes.StateInvalid, foreign.Code);

        // After the expiry (30 minutes) the right state is refused too.
        _clock.Advance(TimeSpan.FromMinutes(PanelStepLimits.StateLifetimeMinutes) + TimeSpan.FromSeconds(1));
        var expired = await Assert.ThrowsAsync<DomainRuleException>(() => service.CompleteCallbackAsync(_world.OrgWide, first.Step.Id, firstState, Ct));
        Assert.Equal(OnboardingPanelStepCodes.StateInvalid, expired.Code);
        Assert.Equal(0, police.Calls);
        Assert.Null((await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == first.Step.Id)).CallbackConsumedAt);
    }

    [Fact]
    public async Task Callback_AStepWithNoCallback_IsRefused_AndAPanelThatCannotSendBackIsAManagedWindow()
    {
        // No callback address configured: even a verifier that supports it starts a managed window and no address leaves.
        _octorate.RedirectUri = null;
        var police = new ScriptedVerifier(PanelStepKind.PoliceConnection, order: 10) { SupportsCallback = true };
        var service = Service(police);
        var started = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Granted.Id), Ct);
        Assert.Equal(PanelHandoffMode.ManagedWindow, started.Step.HandoffMode);
        Assert.Null(started.CallbackUrl);

        var unsupported = await Assert.ThrowsAsync<DomainConflictException>(() => service.CompleteCallbackAsync(_world.OrgWide, started.Step.Id, "anything", Ct));
        Assert.Equal(OnboardingPanelStepCodes.CallbackUnsupported, unsupported.Code);

        // An address that is not https (and not this machine) is no callback either.
        _octorate.RedirectUri = "http://app.casazen.test/octorate/callback";
        var insecure = await service.StartAsync(_world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.PoliceConnection, _world.Hidden.Id), Ct);
        Assert.Equal(PanelHandoffMode.ManagedWindow, insecure.Step.HandoffMode);
        Assert.Equal(0, police.Calls);
    }

    // ─── The default verifiers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): the police verifier reads <c>policeAccount.valid</c> of the accommodation the property is CONFIRMED to, for the org of the step and no
    /// other: valid is verified, not valid is not valid, anything else (absent, refused, no listing, no property, no mapping) is «could not tell», and a
    /// failure worth a retry is not swallowed.
    /// </summary>
    [Fact]
    public async Task PoliceVerifier_ReadsTheAccountOfTheConfirmedAccommodation_AndNothingElseIsAVerdict()
    {
        var propertyId = _world.Granted.Id;
        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var seenOrgs = new List<Guid>();
        client
            .Setup(c => c.GetPoliceAccountStatusAsync("ACC-1", It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, _) => seenOrgs.Add(scope.OrgId))
            .ReturnsAsync(new OctoratePoliceAccountStatus(true, true, true, null));
        client
            .Setup(c => c.GetPoliceAccountStatusAsync("ACC-INVALID", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccountStatus(true, false, true, null));
        client
            .Setup(c => c.GetPoliceAccountStatusAsync("ACC-HIDDEN", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OctoratePoliceAccountStatus.NotReadable);
        client
            .Setup(c => c.GetPoliceAccountStatusAsync("ACC-DOWN", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));

        PanelStepContext Context(Guid? property) => new(_world.OrgId, property, PanelStepKind.PoliceConnection, null);
        PoliceConnectionPanelVerifier Verifier(IPropertyMappingReader mappings) => new(mappings, client.Object, scope);

        var confirmed = new Mapping(true, new Dictionary<Guid, string> { [propertyId] = "ACC-1" });
        Assert.Equal(PanelVerificationOutcome.Verified, await Verifier(confirmed).VerifyAsync(Context(propertyId), Ct));
        Assert.Equal([_world.OrgId], seenOrgs);
        Assert.Equal(_world.OrgId, confirmed.AskedOrgs.Single());
        Assert.Equal([propertyId], confirmed.AskedProperties.Single());

        Assert.Equal(
            PanelVerificationOutcome.NotValid,
            await Verifier(new Mapping(true, new Dictionary<Guid, string> { [propertyId] = "ACC-INVALID" })).VerifyAsync(Context(propertyId), Ct));
        Assert.Equal(
            PanelVerificationOutcome.Unverifiable,
            await Verifier(new Mapping(true, new Dictionary<Guid, string> { [propertyId] = "ACC-HIDDEN" })).VerifyAsync(Context(propertyId), Ct));

        // No confirmed listing, no property, a mapping that is not provided: «could not tell», and Octorate is never asked.
        client.Invocations.Clear();
        Assert.Equal(PanelVerificationOutcome.Unverifiable, await Verifier(new Mapping(true, new Dictionary<Guid, string>())).VerifyAsync(Context(propertyId), Ct));
        Assert.Equal(PanelVerificationOutcome.Unverifiable, await Verifier(confirmed).VerifyAsync(Context(null), Ct));
        Assert.Equal(
            PanelVerificationOutcome.Unverifiable,
            await Verifier(new Mapping(false, new Dictionary<Guid, string> { [propertyId] = "ACC-1" })).VerifyAsync(Context(propertyId), Ct));
        Assert.Equal(
            PanelVerificationOutcome.Unverifiable,
            await Verifier(new Mapping(true, new Dictionary<Guid, string> { [propertyId] = "  " })).VerifyAsync(Context(propertyId), Ct));
        Assert.Empty(client.Invocations);

        // A failure that is worth a retry is thrown, never turned into a verdict.
        await Assert.ThrowsAsync<OctorateRateLimitException>(() =>
            Verifier(new Mapping(true, new Dictionary<Guid, string> { [propertyId] = "ACC-DOWN" })).VerifyAsync(Context(propertyId), Ct));
    }

    [Fact]
    public async Task IstatAndChannelAccess_AreUnverifiable_UntilAnotherEpicRegistersItsOwnVerifier()
    {
        var istat = await new IstatConnectionPanelVerifier().VerifyAsync(new PanelStepContext(_world.OrgId, null, PanelStepKind.IstatConnection, null), Ct);
        var channel = await new ChannelAccessDefaultPanelVerifier().VerifyAsync(new PanelStepContext(_world.OrgId, null, PanelStepKind.ChannelAccess, "booking_xml"), Ct);

        Assert.Equal((PanelVerificationOutcome.Unverifiable, PanelVerificationOutcome.Unverifiable), (istat, channel));
        Assert.Equal((PanelStepKind.IstatConnection, 0), (new IstatConnectionPanelVerifier().Kind, new IstatConnectionPanelVerifier().Order));
        Assert.Equal((PanelStepKind.ChannelAccess, 0), (new ChannelAccessDefaultPanelVerifier().Kind, new ChannelAccessDefaultPanelVerifier().Order));
        Assert.False(((IPanelStepVerifier)new IstatConnectionPanelVerifier()).SupportsCallback);
        Assert.False(((IPanelStepVerifier)new ChannelAccessDefaultPanelVerifier()).SupportsCallback);
    }

    // ─── Registration and names ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The exact list of the default verifiers and of the services of this task: one verifier of order 0 for each of the three kinds.</summary>
    [Fact]
    public void Registration_OneDefaultVerifierForEachKind_AndTheTwoServices()
    {
        var services = new ServiceCollection();
        services.AddCasazenOnboarding(new ConfigurationBuilder().Build());

        Assert.Equal(
            [typeof(PoliceConnectionPanelVerifier), typeof(IstatConnectionPanelVerifier), typeof(ChannelAccessDefaultPanelVerifier)],
            services.Where(d => d.ServiceType == typeof(IPanelStepVerifier)).Select(d => d.ImplementationType));
        Assert.Equal(
            typeof(OnboardingPanelStepService),
            Assert.Single(services, d => d.ServiceType == typeof(IOnboardingPanelStepService)).ImplementationType);
        Assert.Equal(
            typeof(OnboardingAssistService),
            Assert.Single(services, d => d.ServiceType == typeof(IOnboardingAssistService)).ImplementationType);
        Assert.Equal(
            Enum.GetValues<PanelStepKind>().Order(),
            PanelStepCatalog.All.Select(info => info.Kind).Order());
    }

    [Fact]
    public async Task TheApiSpeaksCamelCaseCodes_AndTheBackoffIsTheOneOfTheSchedule()
    {
        var started = await Service().StartAsync(
            _world.OrgWide, _world.OwnerUserId, new PanelStepStartRequest(PanelStepKind.ChannelAccess, null, "booking_xml"), Ct);

        var dto = started.ToDto();
        Assert.Equal(("channelAccess", "started", "managedWindow", "panelChannelAccess"), (dto.Kind, dto.Status, dto.HandoffMode, dto.InstructionsKey));
        Assert.Equal("https://panel.octorate.test/", dto.PanelUrl);
        Assert.Null(dto.CallbackUrl);
        Assert.Equal("booking_xml", dto.ChannelCode);
        Assert.Equal(
            ["policeConnection", "istatConnection", "channelAccess"],
            PanelStepCatalog.All.Select(info => info.Code));
        Assert.Equal(
            ["started", "verified", "confirmed", "notValid", "assistRequested"],
            Enum.GetValues<PanelStepStatus>().Select(status => PanelStepCatalog.CodeOf(status)));
        Assert.Equal(
            [30, 60, 120, 240, 480, 600, 600],
            Enumerable.Range(1, 7).Select(attempt => (int)PanelStepLimits.BackoffAfter(attempt).TotalSeconds));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private OnboardingPanelStepService Service(params IPanelStepVerifier[] verifiers) =>
        new(
            _db,
            verifiers,
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock),
            Options.Create(_octorate),
            _clock,
            NullLogger<OnboardingPanelStepService>.Instance);

    private static string StateOf(PanelStepStart started) =>
        QueryHelpers.ParseQuery(new Uri(Assert.IsType<string>(started.CallbackUrl)).Query)["state"].ToString();

    /// <summary>A verifier that answers what the test scripted: the queue first, then the default (could not tell, or the one set by <see cref="Always"/>).</summary>
    private sealed class ScriptedVerifier(PanelStepKind kind, int order = 0) : IPanelStepVerifier
    {
        private readonly Queue<Func<PanelVerificationOutcome>> _script = new();
        private PanelVerificationOutcome _default = PanelVerificationOutcome.Unverifiable;

        public PanelStepKind Kind { get; } = kind;

        public int Order { get; } = order;

        public bool SupportsCallback { get; init; }

        public int Calls { get; private set; }

        public List<PanelStepContext> Contexts { get; } = [];

        public ScriptedVerifier Then(PanelVerificationOutcome outcome)
        {
            _script.Enqueue(() => outcome);
            return this;
        }

        public ScriptedVerifier ThenThrow(Exception exception)
        {
            _script.Enqueue(() => throw exception);
            return this;
        }

        public ScriptedVerifier Always(PanelVerificationOutcome outcome)
        {
            _default = outcome;
            return this;
        }

        public Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct)
        {
            Calls++;
            Contexts.Add(context);
            return Task.FromResult(_script.Count > 0 ? _script.Dequeue()() : _default);
        }
    }

    /// <summary>The mapping of the properties to the accommodations, as the verifier asks for it; it records who asked.</summary>
    private sealed class Mapping(bool provided, Dictionary<Guid, string> confirmed) : IPropertyMappingReader
    {
        public List<Guid> AskedOrgs { get; } = [];

        public List<IReadOnlyCollection<Guid>> AskedProperties { get; } = [];

        public bool IsProvided => provided;

        public Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            AskedOrgs.Add(orgId);
            AskedProperties.Add(propertyIds);
            IReadOnlyDictionary<Guid, string> answer = confirmed.Where(pair => propertyIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            return Task.FromResult(answer);
        }
    }
}
