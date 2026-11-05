using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// ADR-022-R3, R6, R17: the in-process bus resolves the handlers of an event from the container and runs each in its own scope,
/// under the tenant of the event's org. The scopes are those of the web app: the tenant context of the web layer (no HTTP request
/// here, as in a Hangfire job) and an <see cref="AppDbContext"/> that reads it.
/// </summary>
public class InProcessEventBusTests
{
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");

    private readonly string _database = $"event-bus-{Guid.NewGuid()}";
    private readonly Seen _seen = new();

    /// <summary>
    /// Proof of ADR-022-R6/R17: a handler is given the org of its event, in the event and in the tenant context of its scope, and a
    /// read without any org predicate sees the rows of that org only, never those of another org.
    /// </summary>
    [Fact]
    public async Task PublishAsync_Handler_IsGivenTheOrgOfItsEvent_AndReadsNoOtherOrg()
    {
        await SeedRoutinesAsync(OrgA, OrgB);
        await using var services = BuildServices(s => s.AddScoped<IEventHandler<DomainEvent>, OrgProbeHandler>());
        var bus = Bus(services);

        await bus.PublishAsync(Event(OrgA), CancellationToken.None);
        await bus.PublishAsync(Event(OrgB), CancellationToken.None);

        Assert.Collection(
            _seen.All,
            a =>
            {
                Assert.Equal(OrgA, a.EventOrg);
                Assert.Equal(OrgA, a.TenantOrg);
                Assert.True(a.FilterEnabled);
                Assert.Equal([OrgA], a.VisibleRoutineOrgs);
            },
            b =>
            {
                Assert.Equal(OrgB, b.EventOrg);
                Assert.Equal(OrgB, b.TenantOrg);
                Assert.True(b.FilterEnabled);
                Assert.Equal([OrgB], b.VisibleRoutineOrgs);
            });

        // The scope that is not a bus scope (a Hangfire job, the dispatcher) still reads every org.
        await using var jobScope = services.CreateAsyncScope();
        var everyOrg = await jobScope.ServiceProvider.GetRequiredService<AppDbContext>().Routines.Select(r => r.OrgId).ToListAsync();
        Assert.Equal(2, everyOrg.Distinct().Count());
    }

    [Fact]
    public async Task PublishAsync_TypedEvent_RunsItsOwnHandlersAndThoseOfDomainEvent_APlainEventOnlyTheLatter()
    {
        await using var services = BuildServices(s =>
        {
            s.AddScoped<IEventHandler<DomainEvent>, EveryEventHandler>();
            s.AddScoped<IEventHandler<ProbeTypedEvent>, TypedEventHandler>();
        });
        var bus = Bus(services);
        var plain = Event(OrgA);
        var typed = new ProbeTypedEvent(Guid.NewGuid(), OrgA, DomainEventType.OctorateWebhookReceived, Guid.NewGuid(), Now, "{}");

        await bus.PublishAsync(plain, CancellationToken.None);
        await bus.PublishAsync(typed, CancellationToken.None);

        Assert.Equal(
            [(nameof(EveryEventHandler), plain.Id), (nameof(TypedEventHandler), typed.Id), (nameof(EveryEventHandler), typed.Id)],
            _seen.All.Select(o => (o.Handler, o.EventId)));
    }

    [Fact]
    public async Task PublishAsync_EveryHandler_RunsInAScopeOfItsOwn()
    {
        await using var services = BuildServices(s =>
        {
            s.AddScoped<IEventHandler<DomainEvent>, EveryEventHandler>();
            s.AddScoped<IEventHandler<DomainEvent>, OrgProbeHandler>();
        });

        await Bus(services).PublishAsync(Event(OrgA), CancellationToken.None);

        Assert.Equal([nameof(EveryEventHandler), nameof(OrgProbeHandler)], _seen.All.Select(o => o.Handler));
        Assert.Equal(2, _seen.All.Select(o => o.DbContextInstance).Distinct().Count());
    }

    [Fact]
    public async Task PublishAsync_AFailingHandler_DoesNotStopTheOthers_AndTheEventFails()
    {
        await using var services = BuildServices(s =>
        {
            s.AddScoped<IEventHandler<DomainEvent>, FailingHandler>();
            s.AddScoped<IEventHandler<DomainEvent>, EveryEventHandler>();
        });
        var domainEvent = Event(OrgA);

        var error = await Assert.ThrowsAsync<AggregateException>(() => Bus(services).PublishAsync(domainEvent, CancellationToken.None));

        Assert.Contains(nameof(FailingHandler), error.Message);
        Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
        Assert.Equal([(nameof(EveryEventHandler), domainEvent.Id)], _seen.All.Select(o => (o.Handler, o.EventId)));
    }

    [Fact]
    public async Task PublishAsync_NoHandler_Returns()
    {
        await using var services = BuildServices(_ => { });

        await Bus(services).PublishAsync(Event(OrgA), CancellationToken.None);

        Assert.Empty(_seen.All);
    }

    private static readonly DateTime Now = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);

    private static DomainEvent Event(Guid orgId) =>
        new(Guid.NewGuid(), orgId, DomainEventType.BookingConfirmed, Guid.NewGuid(), Now, "{}");

    private static InProcessEventBus Bus(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<InProcessEventBus>.Instance);

    private ServiceProvider BuildServices(Action<IServiceCollection> handlers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_database));
        services.AddSingleton(_seen);
        handlers(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private async Task SeedRoutinesAsync(params Guid[] orgs)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);
        foreach (var orgId in orgs)
            db.Routines.Add(new Routine { OrgId = orgId, Name = $"routine of {orgId}", EventType = RoutineEventType.BookingConfirmed });
        await db.SaveChangesAsync();
    }

    public sealed record Observation(
        string Handler,
        Guid EventId,
        Guid EventOrg,
        Guid? TenantOrg,
        bool FilterEnabled,
        IReadOnlyList<Guid> VisibleRoutineOrgs,
        Guid DbContextInstance);

    public sealed class Seen
    {
        private readonly List<Observation> _observations = [];

        public IReadOnlyList<Observation> All
        {
            get
            {
                lock (_observations)
                    return [.. _observations];
            }
        }

        public void Add(Observation observation)
        {
            lock (_observations)
                _observations.Add(observation);
        }
    }

    public sealed record ProbeTypedEvent(Guid Id, Guid OrgId, DomainEventType Type, Guid EntityId, DateTime OccurredAt, string PayloadJson)
        : DomainEvent(Id, OrgId, Type, EntityId, OccurredAt, PayloadJson);

    /// <summary>Reads the routines without any org predicate: the tenant filter of its scope decides what it sees.</summary>
    public sealed class OrgProbeHandler(Seen seen, ITenantContext tenant, AppDbContext db) : IEventHandler<DomainEvent>
    {
        public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            var orgs = await db.Routines.Select(r => r.OrgId).Distinct().ToListAsync(ct);
            seen.Add(new Observation(
                nameof(OrgProbeHandler), domainEvent.Id, domainEvent.OrgId, tenant.OrgId, tenant.FilterEnabled, orgs, db.ContextId.InstanceId));
        }
    }

    public sealed class EveryEventHandler(Seen seen, ITenantContext tenant, AppDbContext db) : IEventHandler<DomainEvent>
    {
        public Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            seen.Add(new Observation(
                nameof(EveryEventHandler), domainEvent.Id, domainEvent.OrgId, tenant.OrgId, tenant.FilterEnabled, [], db.ContextId.InstanceId));
            return Task.CompletedTask;
        }
    }

    public sealed class TypedEventHandler(Seen seen, ITenantContext tenant, AppDbContext db) : IEventHandler<ProbeTypedEvent>
    {
        public Task HandleAsync(ProbeTypedEvent domainEvent, CancellationToken ct)
        {
            seen.Add(new Observation(
                nameof(TypedEventHandler), domainEvent.Id, domainEvent.OrgId, tenant.OrgId, tenant.FilterEnabled, [], db.ContextId.InstanceId));
            return Task.CompletedTask;
        }
    }

    public sealed class FailingHandler : IEventHandler<DomainEvent>
    {
        public Task HandleAsync(DomainEvent domainEvent, CancellationToken ct) =>
            throw new InvalidOperationException("The handler failed.");
    }
}
