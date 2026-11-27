namespace Casazen.Core.Options;

public class ScheduledMessageOptions
{
    public const string SectionName = "Messaging:ScheduledMessages";

    public const int DefaultPlanHorizonDays = 30;

    public const int DefaultCatchUpMinutes = 15;

    public const int DefaultBlockedRetryMinutes = 15;

    public TimeOnly SendWindowFrom { get; set; } = new(8, 0);

    public TimeOnly SendWindowTo { get; set; } = new(21, 0);

    public int PlanHorizonDays { get; set; } = DefaultPlanHorizonDays;

    public int CatchUpMinutes { get; set; } = DefaultCatchUpMinutes;

    public int BlockedRetryMinutes { get; set; } = DefaultBlockedRetryMinutes;

    public (TimeOnly From, TimeOnly To) EffectiveSendWindow =>
        SendWindowFrom < SendWindowTo ? (SendWindowFrom, SendWindowTo) : (new TimeOnly(8, 0), new TimeOnly(21, 0));

    public int EffectivePlanHorizonDays => PlanHorizonDays is >= 1 and <= 365 ? PlanHorizonDays : DefaultPlanHorizonDays;

    public int EffectiveCatchUpMinutes => CatchUpMinutes is >= 1 and <= 24 * 60 ? CatchUpMinutes : DefaultCatchUpMinutes;

    public int EffectiveBlockedRetryMinutes => BlockedRetryMinutes is >= 1 and <= 24 * 60 ? BlockedRetryMinutes : DefaultBlockedRetryMinutes;
}
