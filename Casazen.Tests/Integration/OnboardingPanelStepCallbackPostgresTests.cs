using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E10/F01/US03/T01 (NF-107) on a real PostgreSQL database: the state of the callback of a guided panel step is used once. The unit tests run on EF
/// InMemory, where nothing runs together; here the claim is the statement that decides. The same state presented by many requests at once passes
/// once, and a state that was replaced by a new hand-over or that ran out between the check in memory and the claim cannot claim the row.
/// </summary>
public class OnboardingPanelStepCallbackPostgresTests : IAsyncLifetime
{
    private const int Together = 8;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync("panelcb");

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    /// <summary>
    /// Trust rule: eight requests that present the same state at the same moment, each with a context of its own, verify the step once. The claim is
    /// one conditional update: the others find the state consumed and get the same 422 as any state that is not valid.
    /// </summary>
    [PostgresFact]
    public async Task Callback_TheSameStatePresentedTogether_PassesOnce_Test()
    {
        var world = await SeedAsync();
        var scope = new HostScope(world.OrgId);
        var panel = new CallbackPanel();
        PanelStepStart started;
        await using (var db = _database!.CreateContext())
            started = await NewService(db, panel).StartAsync(scope, world.OwnerId, StartRequest(world), Ct);
        var state = StateOf(started);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = Enumerable.Range(0, Together).Select(_ => PresentAsync(scope, started.Step.Id, state, panel, gate.Task)).ToList();
        gate.SetResult();
        var outcomes = await Task.WhenAll(presented);

        Assert.Equal(1, outcomes.Count(refusal => refusal is null));
        Assert.All(
            outcomes.Where(refusal => refusal is not null),
            refusal => Assert.Equal(OnboardingPanelStepCodes.StateInvalid, refusal!.Code));
        Assert.Equal(1, panel.Calls);

        await using var check = _database!.CreateContext();
        var stored = await check.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == started.Step.Id);
        Assert.Equal(PanelStepStatus.Verified, stored.Status);
        Assert.NotNull(stored.CallbackConsumedAt);
    }

    /// <summary>
    /// Trust rule: a request that read the row before a new hand-over replaced the nonce cannot burn the new state with the old one. The claim names
    /// the hash it was presented with, so it finds nothing to update, and the new state is still good.
    /// </summary>
    [PostgresFact]
    public async Task Callback_TheClaimOfAReplacedState_IsRefused_AndTheNewStateIsStillGood_Test()
    {
        var world = await SeedAsync();
        var scope = new HostScope(world.OrgId);
        var panel = new CallbackPanel();

        await using var reading = _database!.CreateContext();
        var first = await NewService(reading, panel).StartAsync(scope, world.OwnerId, StartRequest(world), Ct);
        var oldState = StateOf(first);
        var staleRow = await reading.OnboardingPanelSteps.SingleAsync(s => s.Id == first.Step.Id);

        // A new hand-over (the host opens the panel again) replaces the nonce while the request above is between its check and its claim.
        string newState;
        await using (var other = _database.CreateContext())
            newState = StateOf(await NewService(other, panel).StartAsync(scope, world.OwnerId, StartRequest(world), Ct));
        Assert.NotEqual(oldState, newState);

        var claimed = await NewService(reading, panel).ClaimStateAsync(staleRow, OnboardingPanelStepService.HashOf(oldState), DateTime.UtcNow, Ct);

        Assert.False(claimed);
        await using (var check = _database.CreateContext())
            Assert.Null((await check.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == first.Step.Id)).CallbackConsumedAt);

        await using var completing = _database.CreateContext();
        var done = await NewService(completing, panel).CompleteCallbackAsync(scope, first.Step.Id, newState, Ct);
        Assert.Equal(PanelStepStatus.Verified, done.Status);
    }

    /// <summary>Trust rule: a state that ran out between the check in memory and the claim cannot claim the row either.</summary>
    [PostgresFact]
    public async Task Callback_TheClaimOfAnExpiredState_IsRefused_Test()
    {
        var world = await SeedAsync();
        var scope = new HostScope(world.OrgId);
        var panel = new CallbackPanel();

        await using var reading = _database!.CreateContext();
        var started = await NewService(reading, panel).StartAsync(scope, world.OwnerId, StartRequest(world), Ct);
        var state = StateOf(started);
        var row = await reading.OnboardingPanelSteps.SingleAsync(s => s.Id == started.Step.Id);

        await using (var other = _database.CreateContext())
        {
            var expired = await other.OnboardingPanelSteps.SingleAsync(s => s.Id == started.Step.Id);
            expired.HandoffExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await other.SaveChangesAsync();
        }

        var claimed = await NewService(reading, panel).ClaimStateAsync(row, OnboardingPanelStepService.HashOf(state), DateTime.UtcNow, Ct);

        Assert.False(claimed);
        await using var check = _database.CreateContext();
        Assert.Null((await check.OnboardingPanelSteps.AsNoTracking().SingleAsync(s => s.Id == started.Step.Id)).CallbackConsumedAt);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One request: its own context, released with the others, presenting the state. The refusal, or <c>null</c> when it passed.</summary>
    private async Task<DomainRuleException?> PresentAsync(HostScope scope, Guid stepId, string state, CallbackPanel panel, Task gate)
    {
        await using var db = _database!.CreateContext();
        var service = NewService(db, panel);
        await gate;
        try
        {
            await service.CompleteCallbackAsync(scope, stepId, state, Ct);
            return null;
        }
        catch (DomainRuleException refusal)
        {
            return refusal;
        }
    }

    private static OnboardingPanelStepService NewService(AppDbContext db, IPanelStepVerifier verifier) =>
        new(
            db,
            [verifier],
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance),
            Options.Create(new OctorateOptions
            {
                FrontendBaseUrl = "https://panel.octorate.test/",
                RedirectUri = "https://app.casazen.test/octorate/callback",
            }),
            TimeProvider.System,
            NullLogger<OnboardingPanelStepService>.Instance);

    private static PanelStepStartRequest StartRequest(World world) => new(PanelStepKind.PoliceConnection, world.PropertyId);

    private static string StateOf(PanelStepStart started) =>
        QueryHelpers.ParseQuery(new Uri(Assert.IsType<string>(started.CallbackUrl)).Query)["state"].ToString();

    /// <summary>An org with its owner and one house.</summary>
    private async Task<World> SeedAsync()
    {
        var kit = new OrgInvitationTestKit(() => _database!.CreateContext());
        var (org, owner) = await kit.SeedOwnerOrgAsync($"auth0|panelcb-{Guid.NewGuid():N}");

        await using var db = _database!.CreateContext();
        var property = HostScopeScenario.NewProperty(org.Id, owner.Id, "Casa Prova");
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return new World(org.Id, owner.Id, property.Id);
    }

    private sealed record World(Guid OrgId, string OwnerId, Guid PropertyId);

    /// <summary>A panel that sends the person back (so the step is a <c>redirectCallback</c>) and whose check always finds the step done.</summary>
    private sealed class CallbackPanel : IPanelStepVerifier
    {
        private int _calls;

        public PanelStepKind Kind => PanelStepKind.PoliceConnection;

        public int Order => 10;

        public bool SupportsCallback => true;

        public int Calls => Volatile.Read(ref _calls);

        public Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(PanelVerificationOutcome.Verified);
        }
    }
}
