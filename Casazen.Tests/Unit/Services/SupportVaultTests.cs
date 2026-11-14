using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The support vault (E20/F03/US01/T01, decision O5, PO-19): the proofs of its permission and privacy rules. Staff works on a context under the tenant
/// of ANOTHER org, the way a platform administrator does; the host asks for help on a context with no tenant, like the other onboarding tests. The world
/// is the one of <see cref="HostScopeScenario"/>: an org with two properties and a second org.
/// </summary>
public class SupportVaultTests : IAsyncLifetime
{
    private const string Staff = "auth0|staff-support";
    private const string Reason = "Collego la Polizia nel pannello per conto dell'host.";
    private const string PanelUser = "utente-pannello-di-prova-31";
    private const string PanelPassword = "PASSWORD-PANNELLO-DI-PROVA-31";
    private const string PoliceUser = "utente-questura-di-prova-31";
    private const string PolicePassword = "PASSWORD-QUESTURA-DI-PROVA-31";
    private const string PoliceWsKey = "WSKEY-QUESTURA-DI-PROVA-31";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly AppDbContext _db;
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private AppDbContext _staffDb = null!;
    private HostScopeWorld _world = null!;

    public SupportVaultTests() => _db = new AppDbContext(_options);

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
    /// Proof (permission): a credential opens only for an OPEN «Do it for me» request, of the org and the property of that request, and only the kinds
    /// the request needs. The panel and the police credentials of the property open for its open police request; the ISTAT ones are refused for it;
    /// another property of the same org and another org never get them; a request done or unknown opens nothing; only the openings that passed are
    /// recorded.
    /// </summary>
    [Fact]
    public async Task Vault_RevealOnlyWithOpenAssistRequest_Test()
    {
        await StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, PanelPassword);
        AddPoliceCredentials(_world.OrgId, _world.Granted.Id);
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        var vault = Vault();

        var panel = await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct);
        var police = await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.Police, Reason, Ct);

        Assert.Equal((PanelUser, PanelPassword, (string?)null), (panel.Username, panel.Password, panel.ExtraCode));
        Assert.Equal((PoliceUser, PolicePassword, (string?)PoliceWsKey), (police.Username, police.Password, police.ExtraCode));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddSeconds(SupportVaultLimits.VisibleSeconds), panel.VisibleUntil);

        // A credential the request does not need is refused: the police request never opens the ISTAT credentials.
        var notNeeded = await Assert.ThrowsAsync<DomainRuleException>(() =>
            vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.RegionalIstat, Reason, Ct));
        Assert.Equal(SupportVaultCodes.KindNotForRequest, notNeeded.Code);

        // Another property of the same org has its own credentials (none here): the ones of the first are never its.
        var hidden = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Hidden.Id);
        var otherProperty = await Assert.ThrowsAsync<NotFoundException>(() =>
            vault.RevealAsync(Staff, hidden.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct));
        Assert.Equal(SupportVaultCodes.SecretMissing, otherProperty.Code);

        // A request of another org never reaches the credentials of this org, whatever property it names.
        var foreign = new OnboardingAssistRequest
        {
            OrgId = _world.OtherOrgId,
            PartKey = OnboardingPartKey.Compliance,
            PropertyId = _world.OtherOrgProperty.Id,
            Context = OnboardingAssistContexts.PanelPolice,
            RequestedByUserId = "auth0|altro",
            RequestedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.OnboardingAssistRequests.Add(foreign);
        await _db.SaveChangesAsync();
        foreach (var kind in new[] { SupportVaultCredentialKind.OctoratePanel, SupportVaultCredentialKind.Police })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => vault.RevealAsync(Staff, foreign.Id, kind, Reason, Ct));
            Assert.Equal(SupportVaultCodes.SecretMissing, refused.Code);
        }

        // A request that is done opens nothing any more, and an unknown request is a 404.
        await StaffAssist().UpdateAsync(Staff, request.Id, OnboardingAssistStatus.Done, null, Ct);
        var closed = await Assert.ThrowsAsync<DomainConflictException>(() =>
            vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct));
        Assert.Equal(SupportVaultCodes.RequestNotOpen, closed.Code);
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() =>
            vault.RevealAsync(Staff, Guid.NewGuid(), SupportVaultCredentialKind.OctoratePanel, Reason, Ct));
        Assert.Equal(OnboardingAssistCodes.NotFound, unknown.Code);

        // Only the two openings that passed are recorded.
        Assert.Equal(
            [SupportVaultCredentialKind.OctoratePanel, SupportVaultCredentialKind.Police],
            (await _db.SupportVaultAccesses.AsNoTracking().ToListAsync()).Select(a => a.Kind).Order());
    }

    /// <summary>
    /// Proof (permission): the opening and the vault are for the platform administrators only. The two controllers sit under
    /// <see cref="CasazenPolicies.AdminOnly"/> and nothing else opens them; the only action of the API that answers the values is the opening; the
    /// registered policy refuses every role but the administrator (the positive control).
    /// </summary>
    [Fact]
    public async Task Vault_NonAdminForbidden_Test()
    {
        foreach (var controller in new[] { typeof(AdminOnboardingAssistController), typeof(AdminSupportVaultController) })
        {
            Assert.Equal([CasazenPolicies.AdminOnly], controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
            Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.All(RoutedActions(controller), action =>
            {
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
                Assert.DoesNotContain(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != CasazenPolicies.AdminOnly);
            });
        }

        // The vault itself lists, stores and removes: none of its actions answers a value.
        Assert.Equal(["Delete", "List", "Store"], RoutedActions(typeof(AdminSupportVaultController)).Select(a => a.Name).Order());

        // In the whole API one action answers the values of a credential: the opening, with no-store.
        var answering = typeof(AdminOnboardingAssistController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => RoutedActions(type))
            .Where(action => action.ReturnType.ToString().Contains(nameof(SupportVaultRevealDto), StringComparison.Ordinal))
            .ToList();
        var reveal = Assert.Single(answering);
        Assert.Equal((typeof(AdminOnboardingAssistController), nameof(AdminOnboardingAssistController.Reveal)), (reveal.DeclaringType, reveal.Name));
        Assert.NotNull(reveal.GetCustomAttribute<PrivateAnswerAttribute>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());

        foreach (var role in new[] { "PropertyOwner", "PropertyManager", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, Principal(role), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the support vault.");
        }

        var admin = new AuthorizationHandlerContext(policy.Requirements, Principal("Admin"), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(admin);
        Assert.True(admin.HasSucceeded, "The platform administrator does not pass the policy of the support vault.");
    }

    /// <summary>
    /// Proof (privacy): every opening is audited without the value. The access row holds who, when, the request, the kind and the reason, and nothing
    /// else (the shape is closed); the host reads the line of its request, with the kind of credential, in its own log, with the staff user as the actor
    /// and no value and no reason in it. The sixth opening of the hour is refused and leaves nothing; an hour later the request opens again.
    /// </summary>
    [Fact]
    public async Task Vault_RevealAuditedWithoutValue_Test()
    {
        await StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, PanelPassword);
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        var vault = Vault();

        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, "  " + Reason + "  ", Ct);

        var access = Assert.Single(await _db.SupportVaultAccesses.AsNoTracking().ToListAsync());
        Assert.Equal(
            (_world.OrgId, request.Id, (Guid?)_world.Granted.Id, SupportVaultCredentialKind.OctoratePanel, Staff, _clock.GetUtcNow().UtcDateTime, Reason),
            (access.OrgId, access.RequestId, access.PropertyId, access.Kind, access.AccessedByUserId, access.AccessedAt, access.Reason));
        Assert.Equal(
            ["AccessedAt", "AccessedByUserId", "Id", "Kind", "OrgId", "PropertyId", "Reason", "RequestId"],
            typeof(SupportVaultAccess).GetProperties().Select(p => p.Name).Order());
        var stored = JsonSerializer.Serialize(access);
        Assert.DoesNotContain(PanelPassword, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(PanelUser, stored, StringComparison.Ordinal);

        var line = Assert.Single(await _db.OrgActivityEntries.AsNoTracking().Where(l => l.Type == OrgActivityType.OnboardingAssistUpdated).ToListAsync());
        Assert.Equal((_world.OrgId, Staff, _world.OrgId.ToString()), (line.OrgId, line.ActorUserId, line.SubjectId));
        Assert.Equal(
            new Dictionary<string, string> { ["part"] = "compliance", ["context"] = "panelPolice", ["status"] = "requested", ["credential"] = "panel" },
            OrgActivityDetails.Parse(line.DetailsJson));
        foreach (var never in new[] { PanelPassword, PanelUser, "Polizia nel pannello" })
            Assert.DoesNotContain(never, line.DetailsJson, StringComparison.Ordinal);

        // At most five openings of the request in an hour: the sixth is refused and records nothing.
        for (var opening = 2; opening <= SupportVaultLimits.MaxOpeningsPerRequestPerHour; opening++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct);
        }

        var sixth = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct));
        Assert.Equal(SupportVaultCodes.TooManyOpenings, sixth.Code);
        Assert.Equal(SupportVaultLimits.MaxOpeningsPerRequestPerHour, await _db.SupportVaultAccesses.CountAsync());
        Assert.Equal(SupportVaultLimits.MaxOpeningsPerRequestPerHour, await _db.OrgActivityEntries.CountAsync(l => l.Type == OrgActivityType.OnboardingAssistUpdated));

        // An hour after the first opening, the request opens again.
        _clock.Advance(TimeSpan.FromMinutes(57));
        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct);
        Assert.Equal(SupportVaultLimits.MaxOpeningsPerRequestPerHour + 1, await _db.SupportVaultAccesses.CountAsync());
    }

    /// <summary>
    /// Proof (privacy): the host's ISTAT credentials are write-only. The service and the API answer whether they are configured and since when, never a
    /// value (the shapes are closed and no action of the controller answers anything else); the values are encrypted at rest with their own purpose; the
    /// one reader is the controlled opening of the vault, for an open ISTAT request of the property, recorded.
    /// </summary>
    [Fact]
    public async Task IstatCredentials_WriteOnly_Test()
    {
        const string IstatPassword = "PASSWORD-ISTAT-DI-PROVA-31 ";
        var service = new PropertyIstatCredentialsService(_db, NullLogger<PropertyIstatCredentialsService>.Instance, _clock);
        Assert.Equal(IstatCredentialsStatus.NotConfigured, await service.GetStatusAsync(_world.Granted.Id, Ct));

        var set = await service.SetAsync(_world.Granted.Id, _world.OrgId, new IstatCredentialsInput(" utente-istat-31 ", IstatPassword, " CIR-031 "), Ct);

        Assert.Equal((true, (DateTime?)_clock.GetUtcNow().UtcDateTime), (set.Configured, set.ConfiguredAt));
        var row = await _db.PropertyIstatCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(("utente-istat-31", IstatPassword, (string?)"CIR-031", _world.OrgId), (row.Username, row.Password, row.StructureCode, row.OrgId));

        // What a client reads: configured and since when, in the service and in the API.
        Assert.Equal(["Configured", "ConfiguredAt"], PublicInstanceProperties(typeof(IstatCredentialsStatus)));
        Assert.Equal(["Configured", "ConfiguredAt"], PublicInstanceProperties(typeof(IstatCredentialsStatusDto)));
        var json = JsonSerializer.Serialize(IstatCredentialsStatusDto.From(await service.GetStatusAsync(_world.Granted.Id, Ct)));
        foreach (var value in new[] { "utente-istat-31", "PASSWORD-ISTAT-DI-PROVA-31", "CIR-031" })
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);

        var actions = RoutedActions(typeof(PropertyIstatCredentialsController));
        Assert.Equal(["DELETE", "GET", "PUT"], actions.Select(a => a.GetCustomAttribute<HttpMethodAttribute>()!.HttpMethods.Single()).Order());
        Assert.All(actions, action => Assert.True(
            action.ReturnType == typeof(Task<ActionResult<IstatCredentialsStatusDto>>) || action.ReturnType == typeof(Task<IActionResult>),
            $"{action.Name} answers something else than the status."));

        var encrypted = EncryptedColumns.All.Where(c => c.EntityType == typeof(PropertyIstatCredentials)).ToList();
        Assert.Equal(["Password", "StructureCode", "Username"], encrypted.Select(c => c.Property).Order());
        Assert.All(encrypted, c => Assert.Equal(EncryptedColumns.IstatCredentialsPurpose, c.Purpose));
        Assert.Equal("Casazen.PropertyIstatCredentials", EncryptedColumns.IstatCredentialsPurpose);

        // The one reader: support, for an open ISTAT request of the property, with the opening recorded.
        var request = await RequestAsync(OnboardingAssistContexts.PanelIstat, _world.Granted.Id);
        var opened = await Vault().RevealAsync(Staff, request.Id, SupportVaultCredentialKind.RegionalIstat, Reason, Ct);
        Assert.Equal(("utente-istat-31", IstatPassword, (string?)"CIR-031"), (opened.Username, opened.Password, opened.ExtraCode));
        Assert.Equal(SupportVaultCredentialKind.RegionalIstat, (await _db.SupportVaultAccesses.AsNoTracking().SingleAsync()).Kind);

        // Removed: not configured any more; removing again says there was nothing.
        Assert.True(await service.DeleteAsync(_world.Granted.Id, Ct));
        Assert.Equal(IstatCredentialsStatus.NotConfigured, await service.GetStatusAsync(_world.Granted.Id, Ct));
        Assert.False(await service.DeleteAsync(_world.Granted.Id, Ct));
    }

    // ─── The rules of the vault ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The reason of an opening is 10 to 300 characters once trimmed: anything else is refused and opens nothing.</summary>
    [Fact]
    public async Task Reveal_AReasonOutsideTenTo300Characters_Is422_AndOpensNothing()
    {
        await StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, PanelPassword);
        var request = await RequestAsync(OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        var vault = Vault();

        foreach (var reason in new[] { null, string.Empty, "   breve   ", new string('x', SupportVaultAccess.ReasonMaxLength + 1) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() =>
                vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, reason, Ct));
            Assert.Equal(SupportVaultCodes.ReasonInvalid, refused.Code);
        }

        var unknownKind = await Assert.ThrowsAsync<DomainRuleException>(() =>
            vault.RevealAsync(Staff, request.Id, (SupportVaultCredentialKind)99, Reason, Ct));
        Assert.Equal(SupportVaultCodes.KindInvalid, unknownKind.Code);
        Assert.Empty(await _db.SupportVaultAccesses.ToListAsync());

        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, new string('x', SupportVaultAccess.ReasonMaxLength), Ct);
        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, new string('y', SupportVaultAccess.ReasonMinLength), Ct);
        Assert.Equal(2, await _db.SupportVaultAccesses.CountAsync());
    }

    /// <summary>The kinds a request needs: the panel always, the police and ISTAT credentials only for their own connection (or the whole compliance part) of a property.</summary>
    [Fact]
    public void KindFits_TheLeastTheWorkNeeds()
    {
        var property = Guid.NewGuid();
        Assert.True(SupportVaultRules.KindFits(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, SupportVaultCredentialKind.OctoratePanel));
        Assert.True(SupportVaultRules.KindFits(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, property, SupportVaultCredentialKind.Police));
        Assert.True(SupportVaultRules.KindFits(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelIstat, property, SupportVaultCredentialKind.RegionalIstat));
        Assert.True(SupportVaultRules.KindFits(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, property, SupportVaultCredentialKind.Police));
        Assert.False(SupportVaultRules.KindFits(OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, null, SupportVaultCredentialKind.Police));
        Assert.False(SupportVaultRules.KindFits(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, property, SupportVaultCredentialKind.Police));
        Assert.False(SupportVaultRules.KindFits(OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, property, SupportVaultCredentialKind.RegionalIstat));
        Assert.Equal(["panel", "police", "regionalIstat"], Enum.GetValues<SupportVaultCredentialKind>().Select(SupportVaultRules.CodeOf));
        Assert.All(Enum.GetValues<SupportVaultCredentialKind>(), kind => Assert.True(OrgActivityCatalog.IsSafeDetailValue(SupportVaultRules.CodeOf(kind))));
        Assert.Equal([1, 2, 3], Enum.GetValues<SupportVaultCredentialKind>().Select(k => (int)k));
        Assert.Equal([1, 2], Enum.GetValues<SupportVaultSecretKind>().Select(k => (int)k));
    }

    /// <summary>
    /// Staff stores one credential for each org, property and kind (a second write replaces it), only for a host org and one of its properties; the list
    /// and the answers carry the presence and never a value; a removed credential is gone.
    /// </summary>
    [Fact]
    public async Task Store_ReplacesTheOneCredential_ListsPresenceOnly_AndRemoves()
    {
        var vault = Vault();
        var first = await StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, PanelPassword);
        _clock.Advance(TimeSpan.FromMinutes(3));
        var replaced = await vault.StoreAsync(
            "auth0|staff-altro",
            new SupportVaultSecretInput(_world.OrgId, _world.Granted.Id, SupportVaultSecretKind.OctoratePanel, " nuovo-utente ", "NUOVA-PASSWORD ", "  "),
            Ct);

        Assert.Equal(first.Id, replaced.Id);
        var row = await _db.SupportVaultSecrets.AsNoTracking().SingleAsync();
        Assert.Equal(("nuovo-utente", "NUOVA-PASSWORD ", (string?)null), (row.Username, row.Password, row.ExtraCode));
        Assert.Equal((Staff, "auth0|staff-altro", _clock.GetUtcNow().UtcDateTime), (row.CreatedByUserId, row.UpdatedByUserId, row.UpdatedAt));

        var listed = Assert.Single(await vault.ListAsync(_world.OrgId, Ct));
        Assert.Equal((first.Id, (Guid?)_world.Granted.Id, SupportVaultSecretKind.OctoratePanel), (listed.Id, listed.PropertyId, listed.Kind));
        Assert.Empty(await vault.ListAsync(_world.OtherOrgId, Ct));
        string[] presence = ["CreatedAt", "Id", "Kind", "OrgId", "PropertyId", "UpdatedAt", "UpdatedByUserId"];
        Assert.Equal(presence, PublicInstanceProperties(typeof(SupportVaultSecretPresence)));
        Assert.Equal(presence, PublicInstanceProperties(typeof(SupportVaultSecretDto)));
        Assert.DoesNotContain("NUOVA-PASSWORD", JsonSerializer.Serialize(SupportVaultSecretDto.From(listed)), StringComparison.Ordinal);

        // Only a host org, and only one of its properties.
        var supplierOrg = await Assert.ThrowsAsync<NotFoundException>(() => StoreAsync(_world.SupplierOrgId, null, PanelUser, PanelPassword));
        Assert.Equal(SupportVaultCodes.OrgNotFound, supplierOrg.Code);
        var foreignProperty = await Assert.ThrowsAsync<NotFoundException>(() => StoreAsync(_world.OrgId, _world.OtherOrgProperty.Id, PanelUser, PanelPassword));
        Assert.Equal("property_not_found", foreignProperty.Code);
        var noPassword = await Assert.ThrowsAsync<DomainRuleException>(() => StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, string.Empty));
        Assert.Equal(SupportVaultCodes.ValueInvalid, noPassword.Code);
        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(() =>
            StoreAsync(_world.OrgId, _world.Granted.Id, PanelUser, new string('p', SupportVaultSecret.MaxPasswordLength + 1)));
        Assert.Equal(SupportVaultCodes.ValueInvalid, tooLong.Code);
        var notAKind = await Assert.ThrowsAsync<DomainRuleException>(() => vault.StoreAsync(
            Staff, new SupportVaultSecretInput(_world.OrgId, null, (SupportVaultSecretKind)3, PanelUser, PanelPassword, null), Ct));
        Assert.Equal(SupportVaultCodes.KindInvalid, notAKind.Code);

        // A credential of the whole org is one more row, held once too.
        await StoreAsync(_world.OrgId, null, PanelUser, PanelPassword);
        await StoreAsync(_world.OrgId, null, PanelUser, "ALTRA-PASSWORD");
        Assert.Equal(2, await _db.SupportVaultSecrets.CountAsync());

        Assert.True(await vault.DeleteAsync(Staff, first.Id, Ct));
        Assert.False(await vault.DeleteAsync(Staff, first.Id, Ct));
        Assert.Null((await vault.ListAsync(_world.OrgId, Ct)).SingleOrDefault(s => s.PropertyId is not null));
    }

    /// <summary>
    /// Proof (data, erasure): the vault follows its org and its property. The three tables cascade from the org (unlike the other tenant tables, so
    /// the vault never outlives nor blocks the erasure of an org), from the property, and the audit from its request; the values of the vault and of the
    /// ISTAT form are declared encrypted, each with its own purpose.
    /// </summary>
    [Fact]
    public void TheVault_FollowsItsOrgPropertyAndRequest_AndIsEncryptedAtRest()
    {
        foreach (var type in new[] { typeof(SupportVaultSecret), typeof(SupportVaultAccess), typeof(PropertyIstatCredentials) })
        {
            var entity = _db.Model.FindEntityType(type)!;
            var org = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(OrgEntity));
            Assert.Equal(DeleteBehavior.Cascade, org.DeleteBehavior);
            Assert.False(org.Properties.Single().IsNullable);
        }

        foreach (var type in new[] { typeof(SupportVaultSecret), typeof(PropertyIstatCredentials) })
        {
            var property = _db.Model.FindEntityType(type)!.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Property));
            Assert.Equal(DeleteBehavior.Cascade, property.DeleteBehavior);
        }

        var request = _db.Model.FindEntityType(typeof(SupportVaultAccess))!.GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(OnboardingAssistRequest));
        Assert.Equal(DeleteBehavior.Cascade, request.DeleteBehavior);

        var vault = EncryptedColumns.All.Where(c => c.EntityType == typeof(SupportVaultSecret)).ToList();
        Assert.Equal(["ExtraCode", "Password", "Username"], vault.Select(c => c.Property).Order());
        Assert.All(vault, c => Assert.Equal("Casazen.SupportVault", c.Purpose));
        Assert.DoesNotContain(EncryptedColumns.All, c => c.EntityType == typeof(SupportVaultAccess));
    }

    /// <summary>The services of the vault are registered with the activation, scoped.</summary>
    [Fact]
    public void Registration_TheVaultAndTheIstatCredentials()
    {
        var services = new ServiceCollection();
        services.AddCasazenOnboarding(new ConfigurationBuilder().Build());

        Assert.Equal(typeof(SupportVaultService), Assert.Single(services, d => d.ServiceType == typeof(ISupportVaultService)).ImplementationType);
        Assert.Equal(
            typeof(PropertyIstatCredentialsService),
            Assert.Single(services, d => d.ServiceType == typeof(IPropertyIstatCredentialsService)).ImplementationType);
        Assert.All(
            services.Where(d => d.ServiceType == typeof(ISupportVaultService) || d.ServiceType == typeof(IPropertyIstatCredentialsService)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The vault as a platform administrator reaches it: the context is under the tenant of ANOTHER org.</summary>
    private SupportVaultService Vault() =>
        new(_staffDb, new ActivityLog(_staffDb, _clock), _clock, NullLogger<SupportVaultService>.Instance);

    private Task<SupportVaultSecretPresence> StoreAsync(Guid orgId, Guid? propertyId, string username, string password) =>
        Vault().StoreAsync(Staff, new SupportVaultSecretInput(orgId, propertyId, SupportVaultSecretKind.OctoratePanel, username, password, null), Ct);

    private void AddPoliceCredentials(Guid orgId, Guid propertyId)
    {
        _db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Username = PoliceUser,
            Password = PolicePassword,
            WsKey = PoliceWsKey,
        });
        _db.SaveChanges();
    }

    /// <summary>A «Do it for me» request of the compliance part, asked by the owner as the host does.</summary>
    private Task<AssistRequestView> RequestAsync(string context, Guid? propertyId) =>
        AssistOn(_db).RequestAsync(_world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, context, propertyId), Ct);

    private OnboardingAssistService StaffAssist() => AssistOn(_staffDb);

    private OnboardingAssistService AssistOn(AppDbContext db) =>
        new(
            db,
            new ActivityLog(db, _clock),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock),
            Mock.Of<IPushNotificationService>(),
            _clock,
            NullLogger<OnboardingAssistService>.Instance);

    private static string[] PublicInstanceProperties(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order()];

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), "test"));

    private static List<MethodInfo> RoutedActions(Type controller) =>
        [
            .. controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any()),
        ];

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
