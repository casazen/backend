using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// ADR-022-R3: <see cref="DomainEventTypeMap"/> builds the event the dispatcher publishes from an outbox row: the typed event of
/// the event type when the map has a line for it, a plain <see cref="DomainEvent"/> otherwise, always with the row's values.
/// </summary>
public class DomainEventTypeMapTests
{
    public static TheoryData<DomainEventType> EventTypes()
    {
        var data = new TheoryData<DomainEventType>();
        foreach (var type in Enum.GetValues<DomainEventType>())
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Create_AnyEventType_IsTheMappedTypeWithTheRowValues(DomainEventType type)
    {
        var row = new OutboxEvent
        {
            OrgId = Guid.NewGuid(),
            EventType = type,
            EntityId = Guid.NewGuid(),
            OccurredAt = new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc),
            PayloadJson = """{"bookingId":"6c1f2f0e-8f7e-4f42-9c1d-0d6b2f3e4a5b"}""",
        };

        var domainEvent = DomainEventTypeMap.Create(row);

        Assert.Equal(DomainEventTypeMap.ClrTypeOf(type), domainEvent.GetType());
        Assert.Equal(row.Id, domainEvent.Id);
        Assert.Equal(row.OrgId, domainEvent.OrgId);
        Assert.Equal(type, domainEvent.Type);
        Assert.Equal(row.EntityId, domainEvent.EntityId);
        Assert.Equal(row.OccurredAt, domainEvent.OccurredAt);
        Assert.Equal(row.PayloadJson, domainEvent.PayloadJson);
    }

    [Fact]
    public void Typed_EveryMappedType_IsAConcreteDomainEvent()
    {
        Assert.All(DomainEventTypeMap.Typed, entry =>
        {
            Assert.True(typeof(DomainEvent).IsAssignableFrom(entry.Value), $"{entry.Key} -> {entry.Value.Name}");
            Assert.False(entry.Value.IsAbstract, $"{entry.Key} -> {entry.Value.Name}");
        });
        Assert.Equal(typeof(DomainEvent), DomainEventTypeMap.ClrTypeOf(DomainEventType.TimeTrigger));
    }
}
