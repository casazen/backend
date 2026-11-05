namespace Casazen.Core.Options;

/// <summary>
/// The outbox of the event bus (ADR-022), section <c>EventBus</c>: how long a dispatched event stays in the outbox before the
/// nightly <c>outbox-cleanup</c> job deletes it, and when an old dead letter loses its payload (ADR-022-R15, R21).
/// </summary>
public class EventBusOptions
{
    public const string SectionName = "EventBus";

    /// <summary>Default of <see cref="RetentionDays"/>.</summary>
    public const int DefaultRetentionDays = 60;

    /// <summary>The test mode of the routines replays the last 30 days of events: a shorter retention would take them away.</summary>
    public const int MinimumRetentionDays = 30;

    /// <summary>Days a dispatched event is kept (default 60, never below <see cref="MinimumRetentionDays"/>).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary><see cref="RetentionDays"/>, raised to <see cref="MinimumRetentionDays"/> when it is lower.</summary>
    public int EffectiveRetentionDays => Math.Max(RetentionDays, MinimumRetentionDays);
}
