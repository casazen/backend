namespace Casazen.Core.Options;

/// <summary>
/// The scheduled messages of the routines (E05/F07/US01, NF-71), section <c>Messaging:ScheduledMessages</c>: the send window, how far ahead the messages are planned and how
/// the sweep keeps its distance. They are proposals of this plan for the product owner to confirm; a value that cannot work is brought back to its default (nothing fails the startup).
/// </summary>
public class ScheduledMessageOptions
{
    public const string SectionName = "Messaging:ScheduledMessages";

    public const int DefaultPlanHorizonDays = 30;

    public const int DefaultCatchUpMinutes = 15;

    public const int DefaultBlockedRetryMinutes = 15;

    /// <summary>The start of the send window on the clock of the property, 08:00 by default. A message planned before it waits for it.</summary>
    public TimeOnly SendWindowFrom { get; set; } = new(8, 0);

    /// <summary>The end of the send window on the clock of the property, 21:00 by default. A message planned after it waits for the next morning.</summary>
    public TimeOnly SendWindowTo { get; set; } = new(21, 0);

    /// <summary>How many days ahead the stays are planned: the ones whose check-in or check-out is that close. Default 30.</summary>
    public int PlanHorizonDays { get; set; } = DefaultPlanHorizonDays;

    /// <summary>How late a planned message has to be before the sweep sends it (the routine that should have fired did not), in minutes. Default 15.</summary>
    public int CatchUpMinutes { get; set; } = DefaultCatchUpMinutes;

    /// <summary>How often a blocked or failed message is tried again, in minutes. Default 15.</summary>
    public int BlockedRetryMinutes { get; set; } = DefaultBlockedRetryMinutes;

    /// <summary>The send window: the configured one, or 08:00 to 21:00 when it is empty or upside down.</summary>
    public (TimeOnly From, TimeOnly To) EffectiveSendWindow =>
        SendWindowFrom < SendWindowTo ? (SendWindowFrom, SendWindowTo) : (new TimeOnly(8, 0), new TimeOnly(21, 0));

    public int EffectivePlanHorizonDays => PlanHorizonDays is >= 1 and <= 365 ? PlanHorizonDays : DefaultPlanHorizonDays;

    public int EffectiveCatchUpMinutes => CatchUpMinutes is >= 1 and <= 24 * 60 ? CatchUpMinutes : DefaultCatchUpMinutes;

    public int EffectiveBlockedRetryMinutes => BlockedRetryMinutes is >= 1 and <= 24 * 60 ? BlockedRetryMinutes : DefaultBlockedRetryMinutes;
}
