using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Onboarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Security;

/// <summary>
/// The support vault (E20/F03/US01/T01): nothing of a credential reaches a log, an exception, a ProblemDetails, a Hangfire argument or the activity
/// log. The services run through every path that touches a value (a store, a replacement, the host's ISTAT form, every kind of opening) and every
/// refusal (a value too long, an org that does not exist, a reason too short, a credential the request does not need, a request closed, the limit of
/// the hour, a credential missing), and whatever a log provider, an error page or the log of the org could carry is searched for the values.
/// </summary>
public class SupportVaultNoSecretInLogsTests : IAsyncLifetime
{
    private const string Staff = "auth0|staff-support";
    private const string Reason = "Collego Polizia e ISTAT nel pannello per conto dell'host.";
    private const string PanelUser = "utente-pannello-segreto-47";
    private const string PanelPassword = "PASSWORD-PANNELLO-SEGRETA-47";
    private const string PanelReplaced = "PASSWORD-PANNELLO-NUOVA-47";
    private const string PanelCode = "PIN-PANNELLO-SEGRETO-47";
    private const string PoliceUser = "utente-questura-segreto-47";
    private const string PolicePassword = "PASSWORD-QUESTURA-SEGRETA-47";
    private const string PoliceWsKey = "WSKEY-QUESTURA-SEGRETA-47";
    private const string IstatUser = "utente-istat-segreto-47";
    private const string IstatPassword = "PASSWORD-ISTAT-SEGRETA-47";
    private const string IstatCode = "CIR-SEGRETO-47";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string[] Secrets =
    [
        PanelUser, PanelPassword, PanelReplaced, PanelCode, PoliceUser, PolicePassword, PoliceWsKey, IstatUser, IstatPassword, IstatCode,
    ];

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly AppDbContext _db;
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private AppDbContext _staffDb = null!;
    private HostScopeWorld _world = null!;

    public SupportVaultNoSecretInLogsTests() => _db = new AppDbContext(_options);

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

    /// <summary>
    /// Proof (privacy): across every path that touches a value, and every refusal, no value of a credential is in the logs (messages, structured values
    /// and exceptions), in the exceptions the API turns into a ProblemDetails (their message, their code, their key and their arguments), in the log of
    /// the org, in the audit rows or in the text of an input, an opening or a DTO. The positive control: the logs do say what happened, with ids.
    /// </summary>
    [Fact]
    public async Task Vault_NoSecretInLogsOrErrors_Test()
    {
        var vaultLog = new CapturingLogger<SupportVaultService>();
        var istatLog = new CapturingLogger<PropertyIstatCredentialsService>();
        var vault = new SupportVaultService(_staffDb, new ActivityLog(_staffDb, _clock), _clock, vaultLog);
        var istat = new PropertyIstatCredentialsService(_db, istatLog, _clock);
        var errors = new List<Exception>();
        var texts = new List<string>();

        // Every write: a credential stored, then replaced; the host's ISTAT form, set then replaced; the police credentials of the property.
        var input = new SupportVaultSecretInput(_world.OrgId, _world.Granted.Id, SupportVaultSecretKind.OctoratePanel, PanelUser, PanelPassword, PanelCode);
        texts.Add(input.ToString());
        await vault.StoreAsync(Staff, input, Ct);
        await vault.StoreAsync(Staff, input with { Password = PanelReplaced }, Ct);
        var istatInput = new IstatCredentialsInput(IstatUser, IstatPassword, IstatCode);
        texts.Add(istatInput.ToString());
        await istat.SetAsync(_world.Granted.Id, _world.OrgId, istatInput, Ct);
        await istat.SetAsync(_world.Granted.Id, _world.OrgId, istatInput, Ct);
        _db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            Username = PoliceUser,
            Password = PolicePassword,
            WsKey = PoliceWsKey,
        });
        await _db.SaveChangesAsync();

        // Refused writes that carry values.
        errors.Add(await Assert.ThrowsAsync<DomainRuleException>(() => vault.StoreAsync(
            Staff, input with { Password = new string('x', SupportVaultSecret.MaxPasswordLength) + PanelPassword }, Ct)));
        errors.Add(await Assert.ThrowsAsync<NotFoundException>(() => vault.StoreAsync(Staff, input with { OrgId = Guid.NewGuid() }, Ct)));

        // Every kind of opening, for a request of the whole compliance part of the property (it needs all three).
        var request = await AssistOn(_db).RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);
        foreach (var kind in Enum.GetValues<SupportVaultCredentialKind>())
        {
            var opened = await vault.RevealAsync(Staff, request.Id, kind, Reason, Ct);
            texts.Add(opened.ToString());
            texts.Add(SupportVaultRevealDto.From(opened).ToString());
            Assert.Contains(opened.Password, Secrets);
        }

        // Every refusal of an opening.
        errors.Add(await Assert.ThrowsAsync<DomainRuleException>(() =>
            vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.Police, PolicePassword[..5], Ct)));
        var channel = await AssistOn(_db).RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Channels, OnboardingAssistContexts.Part, _world.Granted.Id), Ct);
        errors.Add(await Assert.ThrowsAsync<DomainRuleException>(() =>
            vault.RevealAsync(Staff, channel.Id, SupportVaultCredentialKind.Police, Reason, Ct)));
        var hidden = await AssistOn(_db).RequestAsync(
            _world.OrgWide, _world.OwnerUserId, new AssistRequestInput(OnboardingPartKey.Compliance, OnboardingAssistContexts.Part, _world.Hidden.Id), Ct);
        errors.Add(await Assert.ThrowsAsync<NotFoundException>(() =>
            vault.RevealAsync(Staff, hidden.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct)));
        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct);
        await vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct);
        errors.Add(await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            vault.RevealAsync(Staff, request.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct)));
        await AssistOn(_staffDb).UpdateAsync(Staff, channel.Id, OnboardingAssistStatus.Cancelled, null, Ct);
        errors.Add(await Assert.ThrowsAsync<DomainConflictException>(() =>
            vault.RevealAsync(Staff, channel.Id, SupportVaultCredentialKind.OctoratePanel, Reason, Ct)));

        // Whatever a log provider, an error page or the log of the org could carry.
        texts.Add(vaultLog.AllOutput);
        texts.Add(istatLog.AllOutput);
        texts.AddRange(errors.Select(e => e.ToString()));
        texts.AddRange(errors.OfType<DomainException>().Select(e => $"{e.Code} {e.MessageKey} {string.Join(' ', e.MessageArgs)} {JsonSerializer.Serialize(e.Extensions)}"));
        texts.AddRange(errors.OfType<NotFoundException>().Select(e => $"{e.Code} {e.MessageKey} {e.Message}"));
        texts.Add(JsonSerializer.Serialize(await _db.OrgActivityEntries.AsNoTracking()
            .Select(l => new { l.OrgId, l.Type, l.ActorUserId, l.SubjectId, l.DetailsJson })
            .ToListAsync()));
        texts.Add(JsonSerializer.Serialize(await _db.SupportVaultAccesses.AsNoTracking().ToListAsync()));
        texts.Add(new StoreSupportVaultSecretDto { Username = PanelUser, Password = PanelPassword, ExtraCode = PanelCode }.ToString());
        texts.Add(new SetIstatCredentialsRequest { Username = IstatUser, Password = IstatPassword, StructureCode = IstatCode }.ToString());

        foreach (var secret in Secrets)
            Assert.All(texts, text => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));

        // The positive control: the logs say what happened, with ids and kinds.
        Assert.Contains(vaultLog.Entries, e => e.Message.Contains("opened for assist request", StringComparison.Ordinal));
        Assert.Contains(vaultLog.Entries, e => e.Message.Contains("replaced by staff", StringComparison.Ordinal));
        Assert.Contains(istatLog.Entries, e => e.Message.Contains("replaced", StringComparison.Ordinal));
        Assert.Equal(7, errors.Count);
    }

    /// <summary>
    /// No Hangfire argument can carry a value: the services of the vault, its controllers and the ISTAT form take no client of the background jobs, so
    /// nothing of an opening is ever queued.
    /// </summary>
    [Fact]
    public void TheVault_QueuesNothing_SoNoJobArgumentCarriesAValue()
    {
        foreach (var type in new[]
                 {
                     typeof(SupportVaultService), typeof(PropertyIstatCredentialsService), typeof(AdminSupportVaultController),
                     typeof(PropertyIstatCredentialsController), typeof(AdminOnboardingAssistController),
                 })
        {
            var parameters = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).SelectMany(c => c.GetParameters());
            Assert.DoesNotContain(parameters, p => p.ParameterType.Namespace?.StartsWith("Hangfire", StringComparison.Ordinal) == true);
        }
    }

    private OnboardingAssistService AssistOn(AppDbContext db) =>
        new(
            db,
            new ActivityLog(db, _clock),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock),
            Mock.Of<IPushNotificationService>(),
            _clock,
            NullLogger<OnboardingAssistService>.Instance);

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
