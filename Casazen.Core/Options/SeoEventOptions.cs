namespace Casazen.Core.Options;

public class SeoEventOptions
{
    public const string SectionName = "Seo:Events";

    public const int DefaultRetentionDays = 90;

    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public int EffectiveRetentionDays => RetentionDays > 0 ? RetentionDays : DefaultRetentionDays;
}
