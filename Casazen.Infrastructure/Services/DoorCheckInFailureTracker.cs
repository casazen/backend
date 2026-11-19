using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDoorCheckInFailureTracker" />
/// <remarks>
/// The instants of the failed attempts of each door are kept in a sliding window (<see cref="DoorCheckInRules.FailureWindow"/>): the door is closed while it holds
/// <see cref="DoorCheckInRules.FailureLimit"/> of them, and opens again as the oldest leave the window. Once the door is closed no more are added, so the list of a door never
/// holds more than the limit. The key is a hash of the door token, never the token; a door whose failures are all out of the window leaves the table.
/// </remarks>
public sealed class DoorCheckInFailureTracker(
    TimeProvider? timeProvider = null,
    int limit = DoorCheckInRules.FailureLimit,
    TimeSpan? window = null) : IDoorCheckInFailureTracker
{
    /// <summary>Doors in the table past which the ones that have no failure left in the window are dropped.</summary>
    private const int CleanupThreshold = 1024;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? DoorCheckInRules.FailureWindow;
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new(StringComparer.Ordinal);

    public bool IsLocked(string doorToken)
    {
        if (!_failures.TryGetValue(Key(doorToken), out var failures))
            return false;

        lock (failures)
        {
            Prune(failures);
            return failures.Count >= limit;
        }
    }

    public bool RecordFailure(string doorToken)
    {
        var failures = _failures.GetOrAdd(Key(doorToken), _ => []);
        bool closesTheDoor;
        lock (failures)
        {
            Prune(failures);
            var wasOpen = failures.Count < limit;
            if (wasOpen)
                failures.Add(_clock.GetUtcNow().UtcDateTime);

            closesTheDoor = wasOpen && failures.Count >= limit;
        }

        if (_failures.Count > CleanupThreshold)
            DropTheDoorsWithNoFailureLeft();

        return closesTheDoor;
    }

    private static string Key(string doorToken)
    {
        ArgumentNullException.ThrowIfNull(doorToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doorToken)), 0, 16);
    }

    /// <summary>Drops the failures that are out of the window.</summary>
    private void Prune(List<DateTime> failures)
    {
        var oldest = _clock.GetUtcNow().UtcDateTime - _window;
        failures.RemoveAll(instant => instant <= oldest);
    }

    private void DropTheDoorsWithNoFailureLeft()
    {
        foreach (var (key, failures) in _failures)
        {
            bool empty;
            lock (failures)
            {
                Prune(failures);
                empty = failures.Count == 0;
            }

            if (empty)
                _failures.TryRemove(new KeyValuePair<string, List<DateTime>>(key, failures));
        }
    }
}
