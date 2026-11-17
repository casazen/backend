using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// «Fatto» verified or declared, the checklist of the staff view and «Non funziona» (E20/F03/US01/T02, NF-15, NF-107). The verification runs through the
/// real <see cref="OrgScopedPanelVerification"/> and the real verifiers of the panel steps; only the channel manager is a fake (the police account it
/// answers). The world is the one of <see cref="HostScopeScenario"/>; staff works on a context under the tenant of ANOTHER org.
/// </summary>
public class OnboardingAssistVerificationTests : IAsyncLifetime
{
    private const string Staff = "auth0|staff-support";
    private const string Note = "  Il pannello dice ancora che manca la Questura.  ";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly AppDbContext _db;
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly RecordingPush _push = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly RecordingTenantScope _tenantScope = new();
    private readonly Mock<IOctorateApiClient> _octorate = new();
    private OctoratePoliceAccountStatus _policeAccount = OctoratePoliceAccountStatus.NotReadable;
    private readonly List<AppDbContext> _contexts = [];
    private Exception? _octorateFailure;
    private ServiceProvider _verifiers = null!;
    private HostScopeWorld _world = null!;

    public OnboardingAssistVerificationTests() => _db = new AppDbContext(_options);

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);

        // The channel manager: every property is confirmed on an accommodation of its own, and the police account answers what the test sets.
        var mappings = new Mock<IPropertyMappingReader>();
        mappings.SetupGet(m => m.IsProvided).Returns(true);
        mappings
            .Setup(m => m.GetConfirmedAccommodationIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                (IReadOnlyDictionary<Guid, string>)ids.ToDictionary(id => id, id => $"ACC-TEST-{id:N}"));
        _octorate
            .Setup(c => c.GetPoliceAccountStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _octorateFailure is { } failure ? throw failure : _policeAccount);
        var orgScope = new Mock<IOctorateOrgScope>();
        orgScope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());

        var services = new ServiceCollection();
        services.AddScoped<IBackgroundTenantScope>(_ => _tenantScope);
        services.AddScoped<IPanelStepVerifier>(_ => new PoliceConnectionPanelVerifier(mappings.Object, _octorate.Object, orgScope.Object));
        services.AddScoped<IPanelStepVerifier, IstatConnectionPanelVerifier>();
        services.AddScoped<IPanelStepVerifier, ChannelAccessDefaultPanelVerifier>();
        _verifiers = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _verifiers.DisposeAsync();
        foreach (var context in _contexts)
            await context.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): «Fatto» on the police connection is «verified» only when the panel reads <c>policeAccount.valid</c>. Not readable (a structure
    /// created through the API has no police account) is declared by the staff user and the step is confirmed, never verified; readable and not valid is
    /// refused and changes nothing; a read that failed reaches the caller and changes nothing; only <c>valid</c> gives a verified step. The verifier runs
    /// for the org of the request, not the staff user's.
    /// </summary>
    [Fact]
    public async Task AssistDone_NeverVerifiedWithoutPoliceAccountValid_Test()
    {
        var notReadable = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);

        var declared = await StaffService().UpdateAsync(Staff, notReadable.Id, OnboardingAssistStatus.Done, null, Ct);

        Assert.Equal(OnboardingAssistOutcomes.Declared, declared.Outcome);
        var confirmed = await StepAsync(_world.Granted.Id, PanelStepKind.PoliceConnection);
        Assert.Equal((PanelStepStatus.Confirmed, Staff, (DateTime?)null), (confirmed.Status, confirmed.CompletedByUserId, confirmed.VerifiedAt));
        Assert.Equal(PanelVerificationOutcomes.Unverifiable, confirmed.LastVerificationOutcome);
        Assert.Equal([_world.OrgId], _tenantScope.Entered);

        // Readable and not valid: «Fatto» is refused, the request stays with support and nothing is recorded or told.
        var notValid = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Hidden.Id);
        _policeAccount = new OctoratePoliceAccountStatus(true, false, true, null);
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() =>
            StaffService().UpdateAsync(Staff, notValid.Id, OnboardingAssistStatus.Done, null, Ct));
        Assert.Equal(OnboardingAssistCodes.DoneNotValid, refused.Code);
        await AssertStillWithSupportAsync(notValid.Id, _world.Hidden.Id);

        // The panel cannot be read now: the error reaches the caller with its code, and nothing changes.
        _octorateFailure = new OctorateServerException(503);
        await Assert.ThrowsAsync<OctorateServerException>(() => StaffService().UpdateAsync(Staff, notValid.Id, OnboardingAssistStatus.Done, null, Ct));
        await AssertStillWithSupportAsync(notValid.Id, _world.Hidden.Id);

        // Only a valid police account makes «verified».
        _octorateFailure = null;
        _policeAccount = new OctoratePoliceAccountStatus(true, true, true, null);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var verified = await StaffService().UpdateAsync(Staff, notValid.Id, OnboardingAssistStatus.Done, null, Ct);

        Assert.Equal(OnboardingAssistOutcomes.Verified, verified.Outcome);
        var step = await StepAsync(_world.Hidden.Id, PanelStepKind.PoliceConnection);
        Assert.Equal((PanelStepStatus.Verified, (DateTime?)_clock.GetUtcNow().UtcDateTime), (step.Status, step.VerifiedAt));
        Assert.Equal(PanelVerificationOutcomes.Verified, step.LastVerificationOutcome);
        Assert.Equal(
            [OnboardingAssistOutcomes.Declared, OnboardingAssistOutcomes.Verified],
            (await _db.OnboardingAssistRequests.AsNoTracking().OrderBy(r => r.CompletedAt).ToListAsync()).Select(r => r.Outcome));
    }

    /// <summary>
    /// Proof (permission): a request of another org stays hidden. The host of the first org cannot reopen it (the 404 of a request that does not exist) and
    /// never lists it; a collaborator «Solo alcuni» cannot reopen a request of a property it was not given nor one of the whole org; the staff view of a
    /// request of the whole org lists the org and the properties of the org OF THE REQUEST only, and the credentials of another org never count.
    /// </summary>
    [Fact]
    public async Task AssistRequest_OtherOrganizationHidden_Test()
    {
        var foreign = new OnboardingAssistRequest
        {
            OrgId = _world.OtherOrgId,
            PartKey = OnboardingPartKey.Compliance,
            PropertyId = _world.OtherOrgProperty.Id,
            Context = OnboardingAssistContexts.PanelPolice,
            Status = OnboardingAssistStatus.Done,
            Outcome = OnboardingAssistOutcomes.Declared,
            RequestedByUserId = "auth0|altro",
            RequestedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            CompletedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.OnboardingAssistRequests.Add(foreign);
        _db.SupportVaultSecrets.Add(new SupportVaultSecret
        {
            OrgId = _world.OtherOrgId,
            PropertyId = _world.OtherOrgProperty.Id,
            Kind = SupportVaultSecretKind.OctoratePanel,
            Username = "utente-altra-org",
            Password = "PASSWORD-ALTRA-ORG",
            CreatedByUserId = Staff,
            UpdatedByUserId = Staff,
        });
        _db.SupportVaultSecrets.Add(new SupportVaultSecret
        {
            OrgId = _world.OtherOrgId,
            Kind = SupportVaultSecretKind.OctoratePanel,
            Username = "utente-altra-org-intera",
            Password = "PASSWORD-ALTRA-ORG-INTERA",
            CreatedByUserId = Staff,
            UpdatedByUserId = Staff,
        });
        await _db.SaveChangesAsync();

        var hidden = await Assert.ThrowsAsync<NotFoundException>(() =>
            HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, foreign.Id, Note, Ct));
        Assert.Equal(OnboardingAssistCodes.NotFound, hidden.Code);
        Assert.DoesNotContain(await HostService().ListAsync(_world.OrgWide, Ct), request => request.Id == foreign.Id);
        var untouched = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == foreign.Id);
        Assert.Equal((OnboardingAssistStatus.Done, (string?)null), (untouched.Status, untouched.HostNote));

        // A collaborator «Solo alcuni» reaches neither the property it was not given nor the whole org.
        var ofHidden = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Hidden.Id);
        var ofOrg = await HostService().RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "142"), Ct);
        await StaffService().UpdateAsync(Staff, ofHidden.Id, OnboardingAssistStatus.Done, null, Ct);
        await StaffService().UpdateAsync(Staff, ofOrg.Id, OnboardingAssistStatus.Done, null, Ct);
        foreach (var requestId in new[] { ofHidden.Id, ofOrg.Id })
        {
            var notReached = await Assert.ThrowsAsync<NotFoundException>(() =>
                HostService().ReopenAsync(_world.Restricted, _world.CollaboratorId, requestId, Note, Ct));
            Assert.Equal(OnboardingAssistCodes.NotFound, notReached.Code);
        }

        // The staff view of the request of the whole org: the org itself and its two properties, never the other org's, whose credentials do not count.
        var detail = await StaffService().GetAsync(ofOrg.Id, Ct);
        Assert.Equal(_world.OrgId, detail.Request.OrgId);
        Assert.Equal(
            new Guid?[] { null, _world.Granted.Id, _world.Hidden.Id }.Order(),
            detail.Checklist.Select(block => block.PropertyId).Order());
        Assert.All(detail.Checklist, block => Assert.False(block.PanelUser.Present));
        var json = JsonSerializer.Serialize(detail.ToDto());
        foreach (var never in new[] { _world.OtherOrgProperty.Id.ToString(), _world.OtherOrgProperty.Name, "PASSWORD-ALTRA-ORG", "utente-altra-org" })
            Assert.DoesNotContain(never, json, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => StaffService().GetAsync(Guid.NewGuid(), Ct));
        Assert.Equal(OnboardingAssistCodes.NotFound, missing.Code);
    }

    // ─── The checklist ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Checklist_InheritsOnlyTheRequestOrganizationsSavedChoice_WithoutDeclaringActivation()
    {
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        Assert.Null(Assert.Single((await StaffService().GetAsync(request.Id, Ct)).Checklist).Sender);
        var org = await _db.Orgs.SingleAsync(o => o.Id == _world.OrgId);
        org.DefaultComplianceSender = ComplianceSenderType.Manual;
        org.DefaultComplianceSenderChangedAt = _clock.GetUtcNow().UtcDateTime;
        var staffOrg = await _db.Orgs.SingleAsync(o => o.Id == _world.OtherOrgId);
        staffOrg.DefaultComplianceSender = ComplianceSenderType.ChannelManager;
        staffOrg.DefaultComplianceSenderChangedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();

        var inherited = Assert.Single((await StaffService().GetAsync(request.Id, Ct)).Checklist);
        Assert.Equal(new AssistSenderLine(ComplianceSenderType.Manual, org.DefaultComplianceSenderChangedAt.Value, false), inherited.Sender);
        Assert.False(inherited.PoliceCredentials!.Present);
        _db.PropertyComplianceSenders.Add(new PropertyComplianceSender
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            AlloggiatiSender = ComplianceSenderType.ChannelManager,
            IstatSender = ComplianceSenderType.ChannelManager,
            LastChangedAt = _clock.GetUtcNow().UtcDateTime,
            LastChangedByUserId = _world.OwnerUserId,
        });
        await _db.SaveChangesAsync();
        Assert.Equal(ComplianceSenderType.ChannelManager, Assert.Single((await StaffService().GetAsync(request.Id, Ct)).Checklist).Sender!.Sender);

        // A historic support request may still exist after the property country changes: no Italian sender is then shown.
        (await _db.Properties.SingleAsync(p => p.Id == _world.Granted.Id, Ct)).Country = "CH";
        await _db.SaveChangesAsync();
        Assert.Null(Assert.Single((await StaffService().GetAsync(request.Id, Ct)).Checklist).Sender);
    }

    /// <summary>
    /// Support sees what is missing per property before working: the user of the panel in the vault, the police and the ISTAT credentials (and whether the
    /// region needs the ISTAT ones), the stored sender, and each panel step with the key of its guide and its state. Presence and dates only.
    /// </summary>
    [Fact]
    public async Task Get_TheChecklist_SaysWhatIsMissingPerProperty_AndCarriesNoValue()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.SupportVaultSecrets.Add(new SupportVaultSecret
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            Kind = SupportVaultSecretKind.OctoratePanel,
            Username = "utente-pannello-81",
            Password = "PASSWORD-PANNELLO-81",
            CreatedByUserId = Staff,
            UpdatedByUserId = Staff,
            UpdatedAt = now,
        });
        _db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            Username = "utente-questura-81",
            Password = "PASSWORD-QUESTURA-81",
            WsKey = "WSKEY-81",
            UpdatedAt = now,
        });
        _db.PropertyComplianceSenders.Add(new PropertyComplianceSender
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            AlloggiatiSender = ComplianceSenderType.ChannelManager,
            IstatSender = ComplianceSenderType.ChannelManager,
            LastChangedAt = now,
            LastChangedByUserId = _world.OwnerUserId,
        });
        await _db.SaveChangesAsync();
        var police = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        var istat = await RequestAsync(OnboardingAssistContexts.PanelIstat, _world.Hidden.Id);

        var granted = Assert.Single((await StaffService().GetAsync(police.Id, Ct)).Checklist);
        Assert.Equal((_world.Granted.Id, _world.Granted.Name), (granted.PropertyId, granted.PropertyName));
        Assert.Equal(new AssistCredentialPresence(true, now), granted.PanelUser);
        Assert.Equal(new AssistCredentialPresence(true, now), granted.PoliceCredentials);
        Assert.Equal(new AssistIstatPresence(false, null, AssistIstatRequirements.ToVerify), granted.IstatCredentials);
        Assert.Equal(new AssistSenderLine(ComplianceSenderType.ChannelManager, now, false), granted.Sender);
        var policeStep = Assert.Single(granted.Steps);
        Assert.Equal(
            (PanelStepKind.PoliceConnection, OnboardingAssistContexts.PanelPolice, "assistRequested"),
            (policeStep.Kind, policeStep.InstructionsKey, policeStep.State));

        var hidden = Assert.Single((await StaffService().GetAsync(istat.Id, Ct)).Checklist);
        Assert.False(hidden.PanelUser.Present);
        Assert.False(hidden.PoliceCredentials!.Present);
        Assert.Null(hidden.Sender);
        Assert.Equal(AssistIstatRequirements.Required, hidden.IstatCredentials!.Requirement);
        Assert.Equal((OnboardingAssistContexts.PanelIstat, "assistRequested"), (Assert.Single(hidden.Steps).InstructionsKey, hidden.Steps[0].State));

        // The host's ISTAT form counts: the credentials are there from then on.
        _db.PropertyIstatCredentials.Add(new PropertyIstatCredentials
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Hidden.Id,
            Username = "utente-istat-81",
            Password = "PASSWORD-ISTAT-81",
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();
        Assert.Equal(
            new AssistIstatPresence(true, now, AssistIstatRequirements.Required),
            Assert.Single((await StaffService().GetAsync(istat.Id, Ct)).Checklist).IstatCredentials);

        // A step that does not exist yet is listed as not started, with its guide.
        var row = await _db.OnboardingPanelSteps.SingleAsync(s => s.PropertyId == _world.Granted.Id);
        _db.OnboardingPanelSteps.Remove(row);
        await _db.SaveChangesAsync();
        var notStarted = Assert.Single(Assert.Single((await StaffService().GetAsync(police.Id, Ct)).Checklist).Steps);
        Assert.Equal((AssistChecklistStates.NotStarted, OnboardingAssistContexts.PanelPolice), (notStarted.State, notStarted.InstructionsKey));

        var json = JsonSerializer.Serialize((await StaffService().GetAsync(police.Id, Ct)).ToDto())
                   + JsonSerializer.Serialize((await StaffService().GetAsync(istat.Id, Ct)).ToDto());
        foreach (var value in new[] { "utente-pannello-81", "PASSWORD-PANNELLO-81", "PASSWORD-QUESTURA-81", "WSKEY-81", "PASSWORD-ISTAT-81", "utente-istat-81" })
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignProperty_ChecklistOmitsItalianConnectionsEvenForHistoricalRequests()
    {
        var request = await RequestAsync(OnboardingAssistContexts.PanelIstat, _world.Granted.Id);
        (await _db.Properties.SingleAsync(p => p.Id == _world.Granted.Id)).Country = "CH";
        await _db.SaveChangesAsync();

        var block = Assert.Single((await StaffService().GetAsync(request.Id, Ct)).Checklist);
        Assert.Equal("CH", block.Country);
        Assert.Null(block.PoliceCredentials);
        Assert.Null(block.IstatCredentials);
        Assert.Null(block.Sender);
        Assert.Empty(block.Steps);
        Assert.Equal("CH", block.ToDto().Country);
    }

    [Theory]
    [InlineData(OnboardingAssistContexts.PanelPolice)]
    [InlineData(OnboardingAssistContexts.PanelIstat)]
    public async Task ForeignProperty_DoesNotCreateItalianConnectionRequestOrReminder(string context)
    {
        (await _db.Properties.SingleAsync(p => p.Id == _world.Granted.Id)).Country = "CH";
        await _db.SaveChangesAsync();
        var count = await _db.InternalActivities.CountAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => RequestAsync(context, _world.Granted.Id));

        Assert.Equal("sender_not_applicable", error.Code);
        Assert.Empty(await _db.OnboardingAssistRequests.ToListAsync());
        Assert.Equal(count, await _db.InternalActivities.CountAsync());
    }

    // ─── «Non funziona» ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The host reopens a request support marked done: open again with the host's note, its step back with support (what «Fatto» recorded no longer stands),
    /// a "To do" again, the line of the log by the host and the staff told live. Only a request done can be reopened, with a note; a second «Fatto» is
    /// another event for the host.
    /// </summary>
    [Fact]
    public async Task Reopen_GivesTheRequestAndItsStepBackToSupport_WithTheNote_AndTellsTheStaff()
    {
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);
        _clock.Advance(TimeSpan.FromHours(2));

        var blank = await Assert.ThrowsAsync<DomainRuleException>(() => HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, request.Id, "   ", Ct));
        var email = await Assert.ThrowsAsync<DomainRuleException>(() =>
            HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, request.Id, "scrivete a titolare@example.com", Ct));
        Assert.Equal((OnboardingAssistCodes.ReopenNoteRequired, OnboardingAssistCodes.ReopenNoteRequired), (blank.Code, email.Code));

        var reopened = await HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, request.Id, Note, Ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        Assert.Equal(
            (OnboardingAssistStatus.Requested, Note.Trim(), (DateTime?)now, (string?)null, (DateTime?)null),
            (reopened.Status, reopened.HostNote, reopened.ReopenedAt, reopened.Outcome, reopened.CompletedAt));
        var stored = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Null(stored.AssignedToUserId);
        var step = await StepAsync(_world.Granted.Id, PanelStepKind.PoliceConnection);
        Assert.Equal(
            (PanelStepStatus.AssistRequested, (string?)null, (DateTime?)null, (string?)null),
            (step.Status, step.CompletedByUserId, step.CompletedAt, step.LastVerificationOutcome));

        // A "To do" again (the one of the «Fatto» stays closed), the line of the log by the host, and the staff told with the instant of the reopening.
        Assert.NotEqual(request.ActivityId, reopened.ActivityId);
        Assert.NotNull((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == request.ActivityId)).CompletedAt);
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == reopened.ActivityId);
        Assert.Null(todo.CompletedAt);
        Assert.Equal(OnboardingAssistCodes.TodoEntityType, todo.EntityType);
        var line = (await _db.OrgActivityEntries.AsNoTracking().Where(l => l.Type == OrgActivityType.OnboardingAssistRequested).ToListAsync())
            .OrderBy(l => l.When)
            .Last();
        Assert.Equal(now, line.When);
        Assert.Equal((_world.OrgId, _world.OwnerUserId), (line.OrgId, line.ActorUserId));
        Assert.DoesNotContain("Questura", line.DetailsJson, StringComparison.Ordinal);
        var (key, audience, payload) = _push.Queued.Last();
        Assert.Equal(
            (PushDeliveryKeys.AssistReopened(request.Id, now), PushAudience.PlatformStaff(request.Id), PushTypes.AssistReopened),
            (key, audience, payload.Type));
        Assert.DoesNotContain("Questura", JsonSerializer.Serialize(payload), StringComparison.Ordinal);

        // Open again: it cannot be reopened, and staff sees the note.
        var open = await Assert.ThrowsAsync<DomainConflictException>(() => HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, request.Id, Note, Ct));
        Assert.Equal(OnboardingAssistCodes.NotDone, open.Code);
        Assert.Equal(Note.Trim(), (await StaffService().GetAsync(request.Id, Ct)).Request.HostNote);

        // Done again: another event for the host, with a key of its own.
        await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);
        Assert.Equal(2, await _db.InAppNotifications.CountAsync(n => n.UserId == _world.OwnerUserId && n.EntityId == request.Id));
    }

    /// <summary>A request done cannot be reopened while another request of the same part, property and channel is open: the new one stands for it.</summary>
    [Fact]
    public async Task Reopen_WhileAnotherRequestOfTheSamePartIsOpen_Is409_AndChangesNothing()
    {
        var input = new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, _world.Granted.Id);
        var first = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, input, Ct);
        await StaffService().UpdateAsync(Staff, first.Id, OnboardingAssistStatus.Done, null, Ct);
        await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, input, Ct);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            HostService().ReopenAsync(_world.OrgWide, _world.OwnerUserId, first.Id, Note, Ct));

        Assert.Equal(OnboardingAssistCodes.AlreadyOpen, conflict.Code);
        var stored = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == first.Id);
        Assert.Equal((OnboardingAssistStatus.Done, OnboardingAssistOutcomes.Declared, (string?)null), (stored.Status, stored.Outcome, stored.HostNote));
    }

    /// <summary>
    /// The reopening is stored before its "To do" is raised again, as the first request is. A call that fails between the two saves leaves the request
    /// open and naming NO "To do" (never the closed one of the «Fatto»), and the next ask of the host finds that out: it is told the request exists
    /// (409) and the "To do" is raised and linked on that very answer.
    /// </summary>
    [Fact]
    public async Task Reopen_WhenTheTodoCannotBeRaised_LeavesTheRequestWithNoTodo_AndTheNextAskRaisesIt()
    {
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);
        var activities = new Mock<IInternalActivityService>();
        activities
            .Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The To do cannot be raised."));
        var db = Fresh(tenant: null);
        var broken = new OnboardingAssistService(db, new ActivityLog(db, _clock), activities.Object, _push, _clock, NullLogger<OnboardingAssistService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.ReopenAsync(_world.OrgWide, _world.OwnerUserId, request.Id, Note, Ct));

        // Stored as reopened, and it names no "To do": the one of the «Fatto» is closed and is not the request's any more.
        var reopened = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.Equal((OnboardingAssistStatus.Requested, (Guid?)null), (reopened.Status, reopened.ActivityId));
        Assert.NotNull((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == request.ActivityId)).CompletedAt);

        // The host asks again: the request exists, and its "To do" is raised and linked on that answer.
        var again = await Assert.ThrowsAsync<DomainConflictException>(() => HostService().RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct));
        Assert.Equal(OnboardingAssistCodes.AlreadyOpen, again.Code);
        var healed = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id);
        Assert.NotNull(healed.ActivityId);
        Assert.NotEqual(request.ActivityId, healed.ActivityId);
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == healed.ActivityId);
        Assert.Null(todo.CompletedAt);
    }

    // ─── The host told live ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// «Fatto» tells the administrators of the org of the request, live: a push to the phone of each, an entry of the bell (the request as its entity) and
    /// the live update, of the type of the outcome. Nobody who is not an administrator, nothing of a supplier or a person in the text. ISTAT is never
    /// readable: always declared. A failure of the push never fails the update.
    /// </summary>
    [Fact]
    public async Task Done_TellsTheAdministratorsLive_AndAFailureNeverFailsTheUpdate()
    {
        var istat = await RequestAsync(OnboardingAssistContexts.PanelIstat, _world.Granted.Id);

        var done = await StaffService().UpdateAsync(Staff, istat.Id, OnboardingAssistStatus.Done, null, Ct);

        Assert.Equal(OnboardingAssistOutcomes.Declared, done.Outcome);
        var (key, audience, payload) = Assert.Single(_push.Queued, q => q.Audience.Kind == PushAudienceKind.User);
        Assert.Equal(PushAudience.User(_world.OrgId, _world.OwnerUserId), audience);
        Assert.Equal((PushTypes.AssistDeclared, PushRoutes.Properties), (payload.Type, payload.Route));
        Assert.False(string.IsNullOrWhiteSpace(payload.Title) || string.IsNullOrWhiteSpace(payload.Body));
        foreach (var never in new[] { "Octorate", _world.Granted.Name, _world.OwnerUserId })
            Assert.DoesNotContain(never, payload.Title + payload.Body, StringComparison.OrdinalIgnoreCase);

        var bell = Assert.Single(await _db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(
            (_world.OrgId, _world.OwnerUserId, PushTypes.AssistDeclared, (Guid?)istat.Id, key),
            (bell.OrgId, bell.UserId, bell.Type, bell.EntityId, bell.DeliveryKey));
        Assert.Equal((_world.OwnerUserId, (string?)PushTypes.AssistDeclared, (Guid?)istat.Id), Assert.Single(_publisher.Published));
        Assert.DoesNotContain(_push.Queued, q => q.Audience.UserId == _world.CollaboratorId);

        // The queue of the pushes is down: the update is stored and answered all the same.
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Hidden.Id);
        _push.Failure = new InvalidOperationException("The queue is down.");
        var stored = await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);
        Assert.Equal((OnboardingAssistStatus.Done, OnboardingAssistOutcomes.Declared), (stored.Status, stored.Outcome));
    }

    /// <summary>
    /// Each administrator is told on its own. The bell entry of the first one to be told cannot be written (the save refuses its row): the update is
    /// stored and answered all the same, and the other administrator is told in full (push, bell entry and live update) instead of the whole round
    /// stopping at the first failure.
    /// </summary>
    [Fact]
    public async Task Done_WhenTheBellOfOneAdministratorCannotBeWritten_TheOtherIsToldAll_AndTheUpdateIsStored()
    {
        // A second administrator of the org, whose id sorts before the owner's: it is the first one told.
        var admin = HostScopeScenario.NewUser($"auth0|admin-{Guid.NewGuid():N}", _world.OrgId, UserRole.None);
        _db.Users.Add(admin);
        _db.OrgMembers.Add(new OrgMember { OrgId = _world.OrgId, UserId = admin.Id, Role = OrgRole.Admin });
        await _db.SaveChangesAsync();
        Assert.True(string.CompareOrdinal(admin.Id, _world.OwnerUserId) < 0);
        var request = await RequestAsync(OnboardingAssistContexts.PanelIstat, _world.Granted.Id);
        var staffDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(new RefuseTheBellOf(admin.Id)).Options,
            new FixedTenant(_world.OtherOrgId));
        _contexts.Add(staffDb);

        var done = await ServiceOn(staffDb).UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);

        Assert.Equal((OnboardingAssistStatus.Done, OnboardingAssistOutcomes.Declared), (done.Status, done.Outcome));
        var bell = Assert.Single(await _db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.Equal((_world.OwnerUserId, PushTypes.AssistDeclared, (Guid?)request.Id), (bell.UserId, bell.Type, bell.EntityId));
        Assert.Equal(_world.OwnerUserId, Assert.Single(_publisher.Published).UserId);
        Assert.Contains(_push.Queued, q => q.Audience.UserId == _world.OwnerUserId && q.Key == bell.DeliveryKey);
    }

    /// <summary>The verification of «Fatto» is registered with the activation, scoped, and the push types and keys are the stable ones.</summary>
    [Fact]
    public void Registration_AndTheStableCodes()
    {
        var services = new ServiceCollection();
        Casazen.Web.Extensions.OnboardingServiceCollectionExtensions.AddCasazenOnboarding(
            services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var verification = Assert.Single(services, d => d.ServiceType == typeof(IAssistPanelVerification));
        Assert.Equal((typeof(OrgScopedPanelVerification), ServiceLifetime.Scoped), (verification.ImplementationType, verification.Lifetime));
        Assert.Equal(("assist-verified", "assist-declared", "assist-reopened"), (PushTypes.AssistVerified, PushTypes.AssistDeclared, PushTypes.AssistReopened));
        Assert.Equal(("verified", "declared"), (OnboardingAssistOutcomes.Verified, OnboardingAssistOutcomes.Declared));

        var requestId = Guid.Parse("6f0c2b1e-0d1a-4c55-9a3f-2b7d8e9f1a20");
        var at = new DateTime(2026, 11, 14, 9, 5, 7, DateTimeKind.Utc);
        Assert.Equal("assist-request:6f0c2b1e0d1a4c559a3f2b7d8e9f1a20:reopened:20261114090507", PushDeliveryKeys.AssistReopened(requestId, at));
        Assert.Equal("assist-request:6f0c2b1e0d1a4c559a3f2b7d8e9f1a20:done:20261114090507:auth0|x", PushDeliveryKeys.AssistOutcome(requestId, at, "auth0|x"));

        // A user id too long for a key is replaced by its hash: the key stays stable and within the 200 characters of the column.
        var longKey = PushDeliveryKeys.AssistOutcome(requestId, at, new string('u', 300));
        Assert.True(longKey.Length <= InAppNotification.DeliveryKeyMaxLength);
        Assert.Equal(longKey, PushDeliveryKeys.AssistOutcome(requestId, at, new string('u', 300)));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The service as one request of the host reaches it: a context of its own (a request never sees what another one tracks), no tenant filter.</summary>
    private OnboardingAssistService HostService() => ServiceOn(Fresh(tenant: null));

    /// <summary>The service as one request of a platform administrator reaches it: a context of its own, under the tenant of ANOTHER org.</summary>
    private OnboardingAssistService StaffService() => ServiceOn(Fresh(new FixedTenant(_world.OtherOrgId)));

    private AppDbContext Fresh(ITenantContext? tenant)
    {
        var context = tenant is null ? new AppDbContext(_options) : new AppDbContext(_options, tenant);
        _contexts.Add(context);
        return context;
    }

    private OnboardingAssistService ServiceOn(AppDbContext db) =>
        new(
            db,
            new ActivityLog(db, _clock),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock),
            _push,
            _clock,
            NullLogger<OnboardingAssistService>.Instance,
            new OrgScopedPanelVerification(_verifiers.GetRequiredService<IServiceScopeFactory>()),
            _publisher);

    /// <summary>A «Do it for me» request of the compliance part, asked by the owner as the host does (it marks the panel step as asked for).</summary>
    private Task<AssistRequestView> RequestAsync(string context, Guid propertyId) =>
        HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, context, propertyId), Ct);

    private Task<OnboardingPanelStep> StepAsync(Guid propertyId, PanelStepKind kind) =>
        _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.PropertyId == propertyId && s.Kind == kind);

    private async Task AssertStillWithSupportAsync(Guid requestId, Guid propertyId)
    {
        var request = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal((OnboardingAssistStatus.Requested, (string?)null, (DateTime?)null), (request.Status, request.Outcome, request.CompletedAt));
        Assert.Equal(PanelStepStatus.AssistRequested, (await StepAsync(propertyId, PanelStepKind.PoliceConnection)).Status);
        Assert.DoesNotContain(await _db.InAppNotifications.AsNoTracking().ToListAsync(), n => n.EntityId == requestId);
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>The scope of the verification as the web layer puts it under an org: the orgs it was put under are kept.</summary>
    private sealed class RecordingTenantScope : IBackgroundTenantScope
    {
        public List<Guid> Entered { get; } = [];

        public void EnterOrg(Guid orgId) => Entered.Add(orgId);
    }

    /// <summary>The push queue as the request sees it: what was asked is kept; <see cref="Failure"/>, when set, is thrown instead.</summary>
    private sealed class RecordingPush : IPushNotificationService
    {
        public List<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Queued { get; } = [];

        public Exception? Failure { get; set; }

        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            if (Failure is not null)
                throw Failure;

            Queued.Add((deliveryKey, audience, payload));
            return true;
        }
    }

    /// <summary>The live channel of the bell: what was published is kept.</summary>
    private sealed class RecordingPublisher : IInAppNotificationPublisher
    {
        public List<(string UserId, string? Type, Guid? EntityId)> Published { get; } = [];

        public Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default)
        {
            Published.Add((userId, type, entityId));
            return Task.CompletedTask;
        }
    }

    /// <summary>A save that refuses the bell row of one person, as a database that fails for that row would.</summary>
    private sealed class RefuseTheBellOf(string userId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<InAppNotification>().Any(e => e.State == EntityState.Added && e.Entity.UserId == userId))
            {
                throw new DbUpdateException("The bell entry of this person cannot be saved.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
