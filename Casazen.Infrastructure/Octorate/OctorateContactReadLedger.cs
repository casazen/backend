using System.Collections.Concurrent;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the contact reads from Octorate remember between two calls (E07/F06/US02/T02, NF-94): how many reads an org made in the last hour (the
/// cap of <c>Crm:OctorateContactReadsPerHour</c>), which bookings were read lately (not asked again) and which ones Octorate refused (not asked
/// again in a loop). In memory, one instance for the application (singleton): a restart forgets it, which can cost one more call for a booking
/// and never a stored contact, and with several API instances each one counts its own reads. <b>It therefore holds only with ONE API instance</b>
/// (one replica while <c>Crm:OctorateContactsEnabled</c> is on: the deploy checklist and the runbook say so); persisting it would take a table, a
/// migration the task does not have. No address, number or name is kept here: only ids and instants.
/// </summary>
public sealed class OctorateContactReadLedger
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static readonly TimeSpan ReadMemory = TimeSpan.FromHours(24);

    public static readonly TimeSpan NotAvailableMemory = TimeSpan.FromHours(6);

    private const int MaxRemembered = 20_000;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _reads = new();
    private readonly ConcurrentDictionary<(Guid OrgId, Guid BookingId), Memory> _remembered = new();

    public bool TryTakeRead(Guid orgId, DateTimeOffset now, int perHour)
    {
        if (perHour < 1)
            return false;

        var reads = _reads.GetOrAdd(orgId, _ => new Queue<DateTimeOffset>());
        lock (reads)
        {
            while (reads.Count > 0 && now - reads.Peek() >= Window)
                reads.Dequeue();

            if (reads.Count >= perHour)
                return false;

            reads.Enqueue(now);
            return true;
        }
    }

    public OctorateContactReadOutcome? Recall(Guid orgId, Guid bookingId, DateTimeOffset now)
    {
        var key = (orgId, bookingId);
        if (!_remembered.TryGetValue(key, out var memory))
            return null;

        if (now - memory.At < KeptFor(memory.Outcome))
            return memory.Outcome;

        _remembered.TryRemove(key, out _);
        return null;
    }

    public void RememberRead(Guid orgId, Guid bookingId, bool hadContact, DateTimeOffset now) =>
        Remember(
            orgId,
            bookingId,
            hadContact ? OctorateContactReadOutcome.AlreadyRead : OctorateContactReadOutcome.NothingToStore,
            now);

    public void RememberNotAvailable(Guid orgId, Guid bookingId, DateTimeOffset now) =>
        Remember(orgId, bookingId, OctorateContactReadOutcome.NotAvailable, now);

    public void RememberUnrecognized(Guid orgId, Guid bookingId, DateTimeOffset now) =>
        Remember(orgId, bookingId, OctorateContactReadOutcome.Unrecognized, now);

    private static TimeSpan KeptFor(OctorateContactReadOutcome outcome) =>
        outcome is OctorateContactReadOutcome.NotAvailable or OctorateContactReadOutcome.Unrecognized ? NotAvailableMemory : ReadMemory;

    private void Remember(Guid orgId, Guid bookingId, OctorateContactReadOutcome outcome, DateTimeOffset now)
    {
        if (_remembered.Count >= MaxRemembered)
            Clean(now);

        _remembered[(orgId, bookingId)] = new Memory(outcome, now);
    }

    private void Clean(DateTimeOffset now)
    {
        foreach (var (key, memory) in _remembered)
        {
            if (now - memory.At >= KeptFor(memory.Outcome))
                _remembered.TryRemove(key, out _);
        }

        if (_remembered.Count >= MaxRemembered)
            _remembered.Clear();
    }

    private sealed record Memory(OctorateContactReadOutcome Outcome, DateTimeOffset At);
}
