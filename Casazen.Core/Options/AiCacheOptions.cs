namespace Casazen.Core.Options;

/// <summary>
/// Limits of the in-memory cache of AI answers (section <c>Ai:Cache</c>, Railway <c>Ai__Cache__*</c>, A8-25). The cache
/// only saves a paid call that was just made: it never grows without bound and never outlives <see cref="TtlHours"/>.
/// Runbook: <c>docs/runbooks/ai.md</c>.
/// </summary>
public class AiCacheOptions
{
    public const string SectionName = "Ai:Cache";

    public const int DefaultMaxEntries = 500;
    public const int DefaultMaxEntriesPerOrg = 50;
    public const int DefaultTtlHours = 24;

    public int MaxEntries { get; set; } = DefaultMaxEntries;

    public int MaxEntriesPerOrg { get; set; } = DefaultMaxEntriesPerOrg;

    public int TtlHours { get; set; } = DefaultTtlHours;

    public AiCacheOptions Normalized() => new()
    {
        MaxEntries = MaxEntries > 0 ? MaxEntries : DefaultMaxEntries,
        MaxEntriesPerOrg = MaxEntriesPerOrg > 0 ? MaxEntriesPerOrg : DefaultMaxEntriesPerOrg,
        TtlHours = TtlHours > 0 ? TtlHours : DefaultTtlHours,
    };
}
