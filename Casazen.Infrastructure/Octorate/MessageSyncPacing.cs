namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// How the sync of the OTA conversations behaves when the calls of an org to Octorate are used up (ADR-023-R8, R9), for the unit of work it lives in
/// (one DI scope: the service and whoever calls it share it). The jobs and the import wait for the bucket to refill, at most
/// <see cref="MessageSyncService.MaxRateWait"/>, because nobody waits for them. The handler of the chat notification runs in the outbox dispatcher,
/// one event after the other for every org within a budget of seconds: it must not wait, so it ends at once with
/// <see cref="OctorateRateLimitException"/> and the dispatcher delivers the event again at its next run.
/// </summary>
public sealed class MessageSyncPacing
{
    /// <summary><c>true</c> (the default): wait for the bucket to refill. <c>false</c>: end at once with <see cref="OctorateRateLimitException"/>.</summary>
    public bool WaitForCapacity { get; set; } = true;
}
