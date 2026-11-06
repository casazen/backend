using System.Collections.Concurrent;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the contact reads from Octorate remember between two calls (E07/F06/US02/T02, NF-94): how many reads an org made in the last hour (the
/// cap of <c>Crm:OctorateContactReadsPerHour</c>), which bookings were read lately (not asked again) and which ones Octorate refused (not asked
/// again in a loop). In memory, one instance for the application (singleton): a restart forgets it, which can cost one more call for a booking
/// and never a stored contact, and with several API instances each one counts its own reads. No address, number or name is kept here: only ids
/// and instants.
/// </summary>
public sealed class OctorateContactReadLedger
{
    /// <summary>The span the cap is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>How long a booking that was read is not read again.</summary>
    public static readonly TimeSpan ReadMemory = TimeSpan.FromHours(24);

    /// <summary>How long a booking Octorate would not give the contact of, or whose answer was not understood, is not asked about again.</summary>
    public static readonly TimeSpan NotAvailableMemory = TimeSpan.FromHours(6);

    /// <summary>Most bookings remembered: past it the memory is cleaned, and cleared if cleaning is not enough (it only makes a call possible again).</summary>
    private const int MaxRemembered = 20_000;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _reads = new();
    private readonly ConcurrentDictionary<(Guid OrgId, Guid BookingId), Memory> _remembered = new();

    /// <summary>
    /// Takes one read of the hour for <paramref name="orgId"/>: <c>true</c> when it had fewer than <paramref name="perHour"/> in the last
    /// <see cref="Window"/>, and the read is counted; <c>false</c> when the cap is reached (nothing is counted).
    /// </summary>
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

    /// <summary>
    /// What is remembered of the booking, or <c>null</c> when nothing is (or what was has been forgotten): <see cref="OctorateContactReadOutcome.AlreadyRead"/>,
    /// <see cref="OctorateContactReadOutcome.NothingToStore"/>, <see cref="OctorateContactReadOutcome.NotAvailable"/> or
    /// <see cref="OctorateContactReadOutcome.Unrecognized"/>.
    /// </summary>
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

    /// <summary>Remembers that the booking was read (<paramref name="hadContact"/>: something was stored) at <paramref name="now"/>.</summary>
    public void RememberRead(Guid orgId, Guid bookingId, bool hadContact, DateTimeOffset now) =>
        Remember(
            orgId,
            bookingId,
            hadContact ? OctorateContactReadOutcome.AlreadyRead : OctorateContactReadOutcome.NothingToStore,
            now);

    /// <summary>Remembers that Octorate does not give the contact of the booking, so it is not asked again for <see cref="NotAvailableMemory"/>.</summary>
    public void RememberNotAvailable(Guid orgId, Guid bookingId, DateTimeOffset now) =>
        Remember(orgId, bookingId, OctorateContactReadOutcome.NotAvailable, now);

    /// <summary>
    /// Remembers that Octorate answered the booking in a form CasaZen does not understand, so it is not asked again for <see cref="NotAvailableMemory"/>
    /// (the same wait as a refusal: the answer will be the same until the code reads it).
    /// </summary>
    public void RememberUnrecognized(Guid orgId, Guid bookingId, DateTimeOffset now) =>
        Remember(orgId, bookingId, OctorateContactReadOutcome.Unrecognized, now);

    /// <summary>How long what is remembered is kept: a read for <see cref="ReadMemory"/>, a refusal or an answer not understood for <see cref="NotAvailableMemory"/>.</summary>
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
