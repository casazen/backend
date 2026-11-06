using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Extensions;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// «Do it for me» (E10/F01/US03/T01, NF-15, NF-107): the proofs of its permission and privacy rules and the rules of the request, the staff queue and
/// the organization log, on EF InMemory and the world of <see cref="HostScopeScenario"/> (an org with a property given to the collaborator «Solo
/// alcuni», one not given, and another org). The host works on a context with no tenant; staff works on a context under the tenant of ANOTHER org,
/// the way a platform administrator does, so every read of the queue has to lift the tenant filter by name.
/// </summary>
public class OnboardingAssistServiceTests : IAsyncLifetime
{
    private const string Staff = "auth0|staff-support";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly AppDbContext _db;
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private AppDbContext _staffDb = null!;
    private HostScopeWorld _world = null!;

    public OnboardingAssistServiceTests() => _db = new AppDbContext(_options);

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        _staffDb = new AppDbContext(_options, new FixedTenant(_world.OtherOrgId));
    }

    public async Task DisposeAsync()
    {
        await _staffDb.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission): the staff queue and its update are for the platform administrators only. Both routes sit under the policy
    /// <see cref="CasazenPolicies.AdminOnly"/> and nothing else opens them (no <c>[AllowAnonymous]</c>, no host policy, and the host controller of
    /// the requests has no verb that lists the queue or changes a request); the real registered policy refuses the owner of the org (who passes the
    /// host policy of the requests) and every caller that is not an administrator, while the administrator passes (the positive control).
    /// </summary>
    [Fact]
    public async Task OnboardingAssist_StaffEndpoints_RequireAdmin_Test()
    {
        var controller = typeof(AdminOnboardingAssistController);
        Assert.Equal([CasazenPolicies.AdminOnly], controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var routed = RoutedActions(controller);
        Assert.Equal(["List", "Update"], routed.Select(a => a.Method.Name).Order());
        Assert.Equal(["GET", "PUT"], routed.Select(a => a.Verb.HttpMethods.Single()).Order());
        Assert.All(routed, action =>
        {
            Assert.Empty(action.Method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.DoesNotContain(action.Method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != CasazenPolicies.AdminOnly);
        });

        // The host controller of the requests can create and list them: it has no verb to update one, and its policy is the org's.
        var hostController = typeof(OnboardingAssistController);
        Assert.Equal(["GET", "POST"], RoutedActions(hostController).Select(a => a.Verb.HttpMethods.Single()).Order());
        Assert.Equal([CasazenPolicies.OrgBillingAdmin], hostController.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(["Admin"], requirement.AllowedRoles);

        foreach (var role in new[] { "PropertyOwner", "PropertyManager", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, Principal(role), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the staff queue.");
        }

        var nobody = new AuthorizationHandlerContext(policy.Requirements, Principal(), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(nobody);
        Assert.False(nobody.HasSucceeded);

        var admin = new AuthorizationHandlerContext(policy.Requirements, Principal("Admin"), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(admin);
        Assert.True(admin.HasSucceeded, "The platform administrator does not pass the policy of the staff queue.");
    }

    /// <summary>
    /// Proof (privacy): what staff reads holds no draft content, no credential and no personal data. The host has a draft of the activation wizard
    /// with a Questura password, a web service key and an e-mail address in it (the encrypted half) and free text in the plain half; the request
    /// stores only the reference to it. The queue (and the answer of an update), as the API serializes them, carry none of it, nor the person who
    /// asked, nor the address of the property; the shape of the staff view is closed, so a new member is a decision.
    /// </summary>
    [Fact]
    public async Task OnboardingAssist_StaffView_ExcludesDraftContentAndSecrets_Test()
    {
        const string Password = "PASSWORD-QUESTURA-DI-PROVA-77";
        const string WsKey = "WSKEY-DI-PROVA-77";
        const string HostEmail = "titolare.segreto@example.com";
        const string FreeText = "TESTO-LIBERO-DELLA-BOZZA-77";
        var now = _clock.GetUtcNow().UtcDateTime;
        var draft = Draft(_world.OwnerUserId, _world.Granted.Id, now.AddDays(WizardDraftLimits.RetentionDays));
        draft.DraftJson = $$"""{"step":"compliance.police","note":"{{FreeText}}"}""";
        draft.EncryptedFieldsJson = $$"""{"password":"{{Password}}","wsKey":"{{WsKey}}","email":"{{HostEmail}}"}""";
        _db.WizardDrafts.Add(draft);
        await _db.SaveChangesAsync();

        var requested = await HostService().RequestAsync(
            _world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct);

        // The request holds the reference (the wizard and what it edits) and no copy: no text column of it holds anything of the draft.
        var stored = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync(r => r.Id == requested.Id);
        Assert.Equal((WizardType.OnboardingActivation, (Guid?)_world.Granted.Id), (stored.DraftWizardType, stored.DraftEntityId));
        var storedJson = JsonSerializer.Serialize(new { stored.Context, stored.StaffNote, stored.RequestedByUserId, stored.DraftEntityId });
        foreach (var secret in new[] { Password, WsKey, HostEmail, FreeText })
            Assert.DoesNotContain(secret, storedJson, StringComparison.Ordinal);

        var staff = StaffService();
        var taken = await staff.UpdateAsync(Staff, requested.Id, OnboardingAssistStatus.InProgress, "Ci pensiamo noi", Ct);
        var page = await staff.ListQueueAsync(null, 1, 25, Ct);

        var item = Assert.Single(page.Items);
        var json = JsonSerializer.Serialize(page.ToDto()) + JsonSerializer.Serialize(taken.ToDto());
        foreach (var forbidden in new[]
                 {
                     Password, WsKey, HostEmail, FreeText, _world.OwnerUserId, _world.Granted.Address, "compliance.police", "EncryptedFields", "DraftJson",
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }

        // What staff does see: ids, context codes, the property and its comune, the status.
        Assert.Equal(("compliance", OnboardingAssistContexts.PanelPolice, _world.Granted.Name, _world.Granted.City), (item.PartCode, item.Context, item.PropertyName, item.Comune));
        Assert.Equal((_world.OrgId, (Guid?)_world.Granted.Id), (item.OrgId, item.PropertyId));

        // The shape is closed: the view of staff, as a record and as the DTO of the API, has exactly these members.
        string[] members =
        [
            "Id", "OrgId", "PartCode", "PropertyId", "PropertyName", "Comune", "Context", "Status", "RequestedAt", "UpdatedAt", "CompletedAt",
            "AssignedToUserId", "StaffNote", "DraftWizardType", "DraftEntityId",
        ];
        Assert.Equal(members.Order(), typeof(AssistQueueItem).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(
            members.Select(m => m == "PartCode" ? "Part" : m).Order(),
            typeof(AdminAssistRequestDto).GetProperties().Select(p => p.Name).Order());
        Assert.All(
            typeof(AdminAssistRequestDto).GetProperties(),
            property => Assert.DoesNotMatch("(?i)json|password|secret|credential|token|email|phone|iban|address|encrypted|nonce|hash|content|body|requestedby", property.Name));
    }

    // ─── The request ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): a request stores the REFERENCE to the draft of the host's wizard (only when it has one), marks the panel step as asked for,
    /// raises the host's "To do" (entity <c>OnboardingAssistRequest</c>, codes and ids only) and writes <c>OnboardingAssistRequested</c> in the org
    /// of the host with the host as the actor and three codes as details. A second open request for the same part and property is 409.
    /// </summary>
    [Fact]
    public async Task Request_StoresTheReference_MarksTheStep_RaisesTheTodo_AndWritesTheLine()
    {
        var service = HostService();
        var step = await StartStepAsync(PanelStepKind.PoliceConnection, _world.Granted.Id);

        // The host has no draft yet: no reference is stored.
        var first = await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct);
        var stored = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync();
        Assert.Equal(first.Id, stored.Id);
        Assert.Null(stored.DraftWizardType);
        Assert.Null(stored.DraftEntityId);
        Assert.Equal(OnboardingAssistStatus.Requested, stored.Status);
        Assert.Equal(_world.OwnerUserId, stored.RequestedByUserId);

        // The step is marked, and its schedule of checks stops (support is handling it).
        var marked = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal(PanelStepStatus.AssistRequested, marked.Status);
        Assert.Null(marked.NextVerificationAt);

        // The host's "To do": the request, by type and id, with a reason code and nothing of the host.
        var todo = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == first.ActivityId);
        Assert.Equal(nameof(ActivityType.WizardResume), todo.ActivityType);
        Assert.Equal(ActivityAction.ResumeWizard, todo.Action);
        Assert.Equal(OnboardingAssistCodes.TodoEntityType, todo.EntityType);
        Assert.Equal(first.Id, todo.EntityId);
        Assert.Equal(OnboardingAssistCodes.ReasonRequested, todo.Reason);
        Assert.Equal((_world.OrgId, (Guid?)_world.Granted.Id), (todo.OrgId, todo.PropertyId));
        Assert.Null(todo.Notes);
        Assert.Null(todo.StaffUserId);
        Assert.Null(todo.CreatedByUserId);

        // The line of the log of the org of the host.
        var line = Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.Equal((_world.OrgId, OrgActivityType.OnboardingAssistRequested, _world.OwnerUserId), (line.OrgId, line.Type, line.ActorUserId));
        Assert.Equal((OrgActivityArea.Account, OrgActivitySubjectType.Org, _world.OrgId.ToString()), (line.Area, line.SubjectType, line.SubjectId));
        Assert.Equal(
            new Dictionary<string, string> { ["part"] = "compliance", ["context"] = "panelPolice", ["status"] = "requested" },
            OrgActivityDetails.Parse(line.DetailsJson));

        // A second open request of the same part and property is refused.
        var again = await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Granted.Id), Ct));
        Assert.Equal(OnboardingAssistCodes.AlreadyOpen, again.Code);
        Assert.Single(await _db.OnboardingAssistRequests.ToListAsync());
        Assert.Single(await _db.OrgActivityEntries.ToListAsync());

        // Another part of the same property, and the same part of another property, are free.
        await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);
        await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct);
        Assert.Equal(3, await _db.OnboardingAssistRequests.CountAsync());
    }

    /// <summary>The draft of the host is referenced when it exists, by the wizard and the entity it edits, and never when it is another person's, expired or about another entity.</summary>
    [Fact]
    public async Task Request_ReferencesOnlyTheDraftOfTheHostThatExists()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.WizardDrafts.AddRange(
            Draft(_world.OwnerUserId, _world.Granted.Id, now.AddDays(5)),
            Draft(_world.CollaboratorId, _world.Hidden.Id, now.AddDays(5)),
            Draft(_world.OwnerUserId, _world.Hidden.Id, now.AddDays(-1)));
        await _db.SaveChangesAsync();
        var service = HostService();

        var withDraft = await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);
        var expired = await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct);
        var other = await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, null), Ct);

        var rows = await _db.OnboardingAssistRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal((WizardType.OnboardingActivation, (Guid?)_world.Granted.Id), (rows[withDraft.Id].DraftWizardType, rows[withDraft.Id].DraftEntityId));
        Assert.Null(rows[expired.Id].DraftWizardType);
        Assert.Null(rows[other.Id].DraftWizardType);
    }

    /// <summary>Proof (permission): a collaborator «Solo alcuni» asks and reads only for the properties it was given, and never for the whole org.</summary>
    [Fact]
    public async Task Request_AndList_ReachOnlyWhatTheScopeReaches()
    {
        var service = HostService();
        await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct);
        await service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, null), Ct);

        var hidden = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.RequestAsync(_world.Restricted, _world.CollaboratorId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct));
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.OtherOrgProperty.Id), Ct));
        var wholeOrg = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
            service.RequestAsync(_world.Restricted, _world.CollaboratorId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, null), Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, hidden.Code);
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, foreign.Code);
        Assert.Equal(OnboardingPanelStepCodes.NeedsWholeOrg, wholeOrg.Code);

        var granted = await service.RequestAsync(_world.Restricted, _world.CollaboratorId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);

        // The owner reads the three; the collaborator only the one of the property it was given; an account that reaches what it created, the two
        // houses it created but no request about the whole org.
        Assert.Equal(3, (await service.ListAsync(_world.OrgWide, Ct)).Count);
        Assert.Equal(granted.Id, Assert.Single(await service.ListAsync(_world.Restricted, Ct)).Id);
        var owned = await service.ListAsync(_world.OwnedByOwner, Ct);
        Assert.Equal(2, owned.Count);
        Assert.All(owned, request => Assert.NotNull(request.PropertyId));
        Assert.Empty(await service.ListAsync(new HostScope(_world.OtherOrgId), Ct));
    }

    /// <summary>The contexts fit the part (the police and ISTAT connections belong to the compliance, the channel access to the channels).</summary>
    [Theory]
    [InlineData(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelPolice, null)]
    [InlineData(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelChannelAccess, "booking_xml")]
    [InlineData(OnboardingPartKey.Compliance, "panelSomethingElse", null)]
    [InlineData(OnboardingPartKey.Compliance, "", null)]
    public async Task Request_AContextThatDoesNotFitThePart_Is422(OnboardingPartKey part, string context, string? channel)
    {
        var rule = await Assert.ThrowsAsync<DomainRuleException>(() =>
            HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, new AssistRequestInput(part, context, _world.Granted.Id, channel), Ct));

        Assert.Equal(OnboardingAssistCodes.ContextInvalid, rule.Code);
        Assert.Empty(await _db.OnboardingAssistRequests.ToListAsync());
    }

    [Fact]
    public async Task Request_TheChannelOfAChannelAccess_IsRequired_AndRefusedForTheOtherContexts()
    {
        var service = HostService();
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.RequestAsync(_world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, null), Ct));
        var surplus = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.RequestAsync(_world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, null, "booking_xml"), Ct));
        Assert.Equal(OnboardingPanelStepCodes.ChannelInvalid, missing.Code);
        Assert.Equal(OnboardingPanelStepCodes.ChannelInvalid, surplus.Code);

        var ok = await service.RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, " booking_xml "), Ct);
        var step = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal(("booking_xml", PanelStepKind.ChannelAccess, PanelStepStatus.AssistRequested), (step.ChannelCode, step.Kind, step.Status));
        Assert.Equal(OnboardingAssistContexts.PanelChannelAccess, ok.Context);
    }

    [Fact]
    public async Task Request_ForAPanelStepAlreadyDone_Is409_AndLeavesNothing()
    {
        var done = await StartStepAsync(PanelStepKind.IstatConnection, _world.Granted.Id);
        var row = await _db.OnboardingPanelSteps.SingleAsync(s => s.Id == done.Id);
        row.Status = PanelStepStatus.Confirmed;
        await _db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() =>
            HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelIstat, _world.Granted.Id), Ct));

        Assert.Equal(OnboardingAssistCodes.StepAlreadyDone, conflict.Code);
        Assert.Empty(await _db.OnboardingAssistRequests.ToListAsync());
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync());
    }

    /// <summary>
    /// Proof (permission, trust): the step support has cannot be confirmed by the host through the other door on the same row either, the handoff of
    /// the import (E02). Asking for help clears the stored «could not tell», which is what that door lets the confirmation through on; the step
    /// stays with support and the answer is the one of a step that was never checked.
    /// </summary>
    [Fact]
    public async Task Request_ForAChannelAccessThatCouldNotBeChecked_RefusesTheConfirmationOfTheImportHandoff()
    {
        var step = await StartStepAsync(PanelStepKind.ChannelAccess, null);
        var row = await _db.OnboardingPanelSteps.SingleAsync(s => s.Id == step.Id);
        row.LastVerificationOutcome = PanelVerificationOutcomes.Unverifiable;
        row.VerificationAttempts = 3;
        await _db.SaveChangesAsync();

        await HostService().RequestAsync(
            _world.OrgWide,
            _world.OwnerUserId,
            new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "booking_xml"),
            Ct);

        var marked = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal((PanelStepStatus.AssistRequested, (string?)null, 0), (marked.Status, marked.LastVerificationOutcome, marked.VerificationAttempts));

        // The handoff of the import reads and confirms the very same row: with support on it, it answers as before any check.
        var handoff = new OnboardingChannelAccessHandoff(
            _db,
            new Mock<IOctorateApiClient>(MockBehavior.Strict).Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Options.Create(new OctorateOptions { FrontendBaseUrl = "https://panel.octorate.test/" }),
            _clock,
            NullLogger<OnboardingChannelAccessHandoff>.Instance);
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => handoff.ConfirmAsync(_world.OrgId, _world.OwnerUserId, "booking_xml", Ct));

        Assert.Equal(OnboardingPanelStepCodes.VerificationRequired, refused.Code);
        var stillWithSupport = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal((PanelStepStatus.AssistRequested, (string?)null), (stillWithSupport.Status, stillWithSupport.CompletedByUserId));
    }

    // ─── The staff queue ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission, data): <c>done</c> on a panel context confirms the matching step with the staff user as who completed it, closes the host's
    /// "To do" of the request and the one of the step (both of the org of the request, as the staff user), and writes <c>OnboardingAssistUpdated</c> in
    /// the log of the org OF THE REQUEST with the staff user as the actor. The staff user works under the tenant of another org: its own org gets no
    /// line, and the tenant filter hides nothing of the host's rows from the update.
    /// </summary>
    [Fact]
    public async Task Update_Done_OnAPanelContext_ConfirmsTheStep_ClosesTheTodos_AndLogsInTheOrgOfTheRequest()
    {
        var step = await StartStepAsync(PanelStepKind.PoliceConnection, _world.Granted.Id);
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct);
        Assert.NotNull(step.ActivityId);

        // The tenant filter of the staff user's own context hides the host's request: the update must lift it by name.
        Assert.Empty(await _staffDb.OnboardingAssistRequests.ToListAsync());

        _clock.Advance(TimeSpan.FromMinutes(20));
        var done = await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, "  Collegamento fatto dall'assistenza.  ", Ct);

        Assert.Equal(OnboardingAssistStatus.Done, done.Status);
        Assert.Equal(Staff, done.AssignedToUserId);
        Assert.Equal("Collegamento fatto dall'assistenza.", done.StaffNote);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, done.CompletedAt);
        Assert.Equal(_world.OrgId, done.OrgId);

        var stored = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal(PanelStepStatus.Confirmed, stored.Status);
        Assert.Equal(Staff, stored.CompletedByUserId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, stored.CompletedAt);
        Assert.Null(stored.NextVerificationAt);

        // Both "To do" are closed, by the staff user, and are the host's (the org of the request).
        var todos = await _db.InternalActivities.AsNoTracking().Where(a => a.OrgId == _world.OrgId).ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.All(todos, todo => Assert.Equal((InternalActivity.StatusCompleted, Staff), (todo.Status, todo.ClosedBy)));
        Assert.All(todos, todo => Assert.NotNull(todo.CompletedAt));

        // The log: the request by the host, the update by the staff user, both in the org of the request.
        var lines = (await _db.OrgActivityEntries.AsNoTracking().ToListAsync()).OrderBy(l => l.Type).ToList();
        Assert.Equal([OrgActivityType.OnboardingAssistRequested, OrgActivityType.OnboardingAssistUpdated], lines.Select(l => l.Type));
        Assert.All(lines, l => Assert.Equal(_world.OrgId, l.OrgId));
        var update = lines[1];
        Assert.Equal((Staff, _world.OrgId.ToString()), (update.ActorUserId, update.SubjectId));
        Assert.Equal(
            new Dictionary<string, string> { ["part"] = "compliance", ["context"] = "panelPolice", ["status"] = "done" },
            OrgActivityDetails.Parse(update.DetailsJson));
        Assert.DoesNotContain("assistenza", update.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().Where(l => l.OrgId == _world.OtherOrgId).ToListAsync());

        // A closed request is not updated again.
        var closed = await Assert.ThrowsAsync<DomainConflictException>(() => StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, null, Ct));
        Assert.Equal(OnboardingAssistCodes.Closed, closed.Code);
        Assert.Equal(2, await _db.OrgActivityEntries.CountAsync());
    }

    [Fact]
    public async Task Update_InProgress_TakesTheRequest_AndLeavesTheStepAndTheTodosOpen()
    {
        var step = await StartStepAsync(PanelStepKind.IstatConnection, null);
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelIstat, null), Ct);

        var taken = await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, null, Ct);

        Assert.Equal(OnboardingAssistStatus.InProgress, taken.Status);
        Assert.Equal(Staff, taken.AssignedToUserId);
        Assert.Null(taken.CompletedAt);
        Assert.Equal(PanelStepStatus.AssistRequested, (await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id)).Status);
        Assert.Equal(2, await _db.InternalActivities.CountAsync(a => a.CompletedAt == null));

        // The host reads the status behind its "To do", and the request is still the one open request of the part (a second one is refused).
        var read = Assert.Single(await HostService().ListAsync(_world.OrgWide, Ct));
        Assert.Equal(OnboardingAssistStatus.InProgress, read.Status);
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, null), Ct));
    }

    [Fact]
    public async Task Update_Cancelled_GivesTheStepBackToTheHost_WithAFreshWindowOfChecks_AndClosesOnlyTheRequestTodo()
    {
        var step = await StartStepAsync(PanelStepKind.PoliceConnection, _world.Granted.Id);
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var cancelled = await StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Cancelled, "Non serve piu", Ct);

        Assert.Equal(OnboardingAssistStatus.Cancelled, cancelled.Status);
        var stored = await _db.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal(PanelStepStatus.Started, stored.Status);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + PanelStepLimits.BackoffAfter(1), stored.NextVerificationAt);
        Assert.Null(stored.CompletedByUserId);

        // The request's "To do" is closed; the "To do" of the step, still to do, is not.
        Assert.NotNull((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == request.ActivityId)).CompletedAt);
        Assert.Null((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == step.ActivityId)).CompletedAt);

        // A new request is possible now.
        var again = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id), Ct);
        Assert.NotEqual(request.Id, again.Id);
    }

    /// <summary>Proof (privacy): the note holds no e-mail address; a refused note leaves the request, the step, the "To do" and the log untouched.</summary>
    [Theory]
    [InlineData("scrivere a mario.rossi@example.com per le credenziali")]
    [InlineData("a@b")]
    [InlineData("contatto: titolare@casa.it")]
    public async Task Update_ANoteWithAnEmailAddress_IsRefused_AndNothingChanges(string note)
    {
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, null), Ct);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => StaffService().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, note, Ct));

        Assert.Equal(OnboardingAssistCodes.NoteInvalid, refused.Code);
        var stored = await _db.OnboardingAssistRequests.AsNoTracking().SingleAsync();
        Assert.Equal(OnboardingAssistStatus.Requested, stored.Status);
        Assert.Null(stored.StaffNote);
        Assert.Null(stored.AssignedToUserId);
        Assert.Single(await _db.OrgActivityEntries.ToListAsync());
        Assert.Null((await _db.InternalActivities.AsNoTracking().SingleAsync()).CompletedAt);
    }

    [Fact]
    public async Task Update_TheNote_IsBoundedAt500Characters_BlankClearsIt_AndLeftOutKeepsIt()
    {
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, null), Ct);
        var staff = StaffService();

        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(() =>
            staff.UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, new string('x', OnboardingAssistRequest.StaffNoteMaxLength + 1), Ct));
        Assert.Equal(OnboardingAssistCodes.NoteInvalid, tooLong.Code);

        var longest = new string('x', OnboardingAssistRequest.StaffNoteMaxLength);
        Assert.Equal(longest, (await staff.UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, longest, Ct)).StaffNote);
        Assert.Equal(longest, (await staff.UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, null, Ct)).StaffNote);
        Assert.Null((await staff.UpdateAsync(Staff, request.Id, OnboardingAssistStatus.InProgress, "   ", Ct)).StaffNote);

        // Every accepted update wrote its line, and never the note.
        var lines = await _db.OrgActivityEntries.AsNoTracking().Where(l => l.Type == OrgActivityType.OnboardingAssistUpdated).ToListAsync();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.DoesNotContain("xxx", l.DetailsJson, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OnboardingAssistStatus.Requested)]
    [InlineData((OnboardingAssistStatus)0)]
    [InlineData((OnboardingAssistStatus)99)]
    public async Task Update_AStatusStaffCannotSet_Is422_AndAnUnknownRequestIs404(OnboardingAssistStatus status)
    {
        var request = await HostService().RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, null), Ct);
        var staff = StaffService();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => staff.UpdateAsync(Staff, request.Id, status, null, Ct));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => staff.UpdateAsync(Staff, Guid.NewGuid(), OnboardingAssistStatus.Done, null, Ct));

        Assert.Equal(OnboardingAssistCodes.StatusInvalid, refused.Code);
        Assert.Equal(OnboardingAssistCodes.NotFound, missing.Code);
    }

    /// <summary>The queue is paged in SQL and capped at 100 a page, shows the requests of every org, open ones first and the oldest first, and narrows by status.</summary>
    [Fact]
    public async Task ListQueue_IsPaged_OrdersOpenFirst_CrossesOrgs_AndFiltersByStatus()
    {
        var host = HostService();
        var first = await host.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await host.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var third = await host.RequestAsync(_world.OrgWide, _world.OwnerUserId, Input(OnboardingPartKey.Listings, OnboardingAssistContexts.Part, null), Ct);
        var staff = StaffService();
        await staff.UpdateAsync(Staff, first.Id, OnboardingAssistStatus.Done, null, Ct);

        // A request of another org, written straight in the table.
        var foreign = new OnboardingAssistRequest
        {
            OrgId = _world.OtherOrgId,
            PartKey = OnboardingPartKey.Channels,
            PropertyId = _world.OtherOrgProperty.Id,
            Context = OnboardingAssistContexts.Part,
            RequestedByUserId = "auth0|altro",
            RequestedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-30),
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.OnboardingAssistRequests.Add(foreign);
        await _db.SaveChangesAsync();

        var all = await staff.ListQueueAsync(null, 1, 25, Ct);
        Assert.Equal(4, all.TotalCount);
        Assert.Equal([foreign.Id, second.Id, third.Id, first.Id], all.Items.Select(i => i.Id));
        Assert.Equal(_world.OtherOrgProperty.Name, all.Items[0].PropertyName);

        var firstPage = await staff.ListQueueAsync(null, 1, 2, Ct);
        var secondPage = await staff.ListQueueAsync(null, 2, 2, Ct);
        Assert.Equal([foreign.Id, second.Id], firstPage.Items.Select(i => i.Id));
        Assert.Equal([third.Id, first.Id], secondPage.Items.Select(i => i.Id));
        Assert.Equal((4, 2, 2), (secondPage.TotalCount, secondPage.Page, secondPage.PageSize));

        Assert.Equal([first.Id], (await staff.ListQueueAsync(OnboardingAssistStatus.Done, 1, 25, Ct)).Items.Select(i => i.Id));
        Assert.Equal(3, (await staff.ListQueueAsync(OnboardingAssistStatus.Requested, 1, 25, Ct)).TotalCount);

        // The bounds: 100 at most, 25 without a size, page 1 for nonsense.
        Assert.Equal(OnboardingAssistLimits.MaxPageSize, (await staff.ListQueueAsync(null, 1, 5000, Ct)).PageSize);
        Assert.Equal(OnboardingAssistLimits.DefaultPageSize, (await staff.ListQueueAsync(null, 1, 0, Ct)).PageSize);
        Assert.Equal(1, (await staff.ListQueueAsync(null, -3, 10, Ct)).Page);
    }

    [Fact]
    public void TheContextAndTheNoteRules_AreClosedLists()
    {
        Assert.Equal(
            ["panelPolice", "panelIstat", "panelChannelAccess", "part"],
            OnboardingAssistRules.Contexts);
        Assert.True(OnboardingAssistRules.ContextFits(OnboardingPartKey.Compliance, "panelPolice"));
        Assert.True(OnboardingAssistRules.ContextFits(OnboardingPartKey.Compliance, "panelIstat"));
        Assert.True(OnboardingAssistRules.ContextFits(OnboardingPartKey.Channels, "panelChannelAccess"));
        Assert.True(OnboardingAssistRules.ContextFits(OnboardingPartKey.Partners, "part"));
        Assert.False(OnboardingAssistRules.ContextFits(OnboardingPartKey.Channels, "panelPolice"));
        Assert.False(OnboardingAssistRules.ContextFits(OnboardingPartKey.Compliance, "PANELPOLICE"));
        Assert.False(OnboardingAssistRules.ContextFits(OnboardingPartKey.Compliance, null));

        Assert.True(OnboardingAssistRules.TryNormalizeNote(null, out var none));
        Assert.Null(none);
        Assert.True(OnboardingAssistRules.TryNormalizeNote("  ok  ", out var trimmed));
        Assert.Equal("ok", trimmed);
        Assert.False(OnboardingAssistRules.TryNormalizeNote("write to x@y.z", out _));

        Assert.Equal(
            ["requested", "inProgress", "done", "cancelled"],
            Enum.GetValues<OnboardingAssistStatus>().Select(OnboardingAssistRules.StatusCode));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The service as the host's request reaches it: a context with no tenant filter, like the other onboarding tests.</summary>
    private OnboardingAssistService HostService() => ServiceOn(_db);

    /// <summary>The service as a platform administrator reaches it: the context of the request is under the tenant of ANOTHER org.</summary>
    private OnboardingAssistService StaffService() => ServiceOn(_staffDb);

    private OnboardingAssistService ServiceOn(AppDbContext db) =>
        new(
            db,
            new ActivityLog(db, _clock),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock),
            _clock,
            NullLogger<OnboardingAssistService>.Instance);

    /// <summary>Starts a panel step as the owner does, with the default verifiers (nothing is called in these tests).</summary>
    private async Task<PanelStepView> StartStepAsync(PanelStepKind kind, Guid? propertyId)
    {
        var steps = new OnboardingPanelStepService(
            _db,
            [new IstatConnectionPanelVerifier()],
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock),
            Options.Create(new OctorateOptions { FrontendBaseUrl = "https://panel.octorate.test/" }),
            _clock,
            NullLogger<OnboardingPanelStepService>.Instance);
        var started = await steps.StartAsync(
            _world.OrgWide,
            _world.OwnerUserId,
            new PanelStepStartRequest(kind, propertyId, kind == PanelStepKind.ChannelAccess ? "booking_xml" : null),
            Ct);
        return started.Step;
    }

    private static AssistRequestInput Input(OnboardingPartKey part, string context, Guid? propertyId) => new(part, context, propertyId);

    private WizardDraft Draft(string userId, Guid? entityId, DateTime expiresAt) => new()
    {
        OrgId = _world.OrgId,
        UserId = userId,
        WizardType = WizardType.OnboardingActivation,
        EntityId = entityId,
        Version = 1,
        DraftJson = "{}",
        LastSavedAt = expiresAt.AddDays(-WizardDraftLimits.RetentionDays),
        ExpiresAt = expiresAt,
    };

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), "test"));

    private static List<(MethodInfo Method, HttpMethodAttribute Verb)> RoutedActions(Type controller) =>
        [
            .. controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => (Method: method, Verb: method.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault()))
                .Where(entry => entry.Verb is not null)
                .Select(entry => (entry.Method, entry.Verb!)),
        ];

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
