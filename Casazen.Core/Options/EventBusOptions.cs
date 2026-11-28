namespace Casazen.Core.Options;

public class EventBusOptions
{
    public const string SectionName = "EventBus";

    public const int DefaultRetentionDays = 60;

    public const int MinimumRetentionDays = 30;

    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public int EffectiveRetentionDays => Math.Max(RetentionDays, MinimumRetentionDays);
}
