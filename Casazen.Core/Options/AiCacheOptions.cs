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

    /// <summary>Entries kept in all (platform content and every org together). The oldest goes first when it is full.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <summary>
    /// Entries one org can hold: past it, the oldest entry of that same org is replaced, so an org never pushes out the
    /// entries of the others.
    /// </summary>
    public int MaxEntriesPerOrg { get; set; } = DefaultMaxEntriesPerOrg;

    /// <summary>Hours an answer stays cached.</summary>
    public int TtlHours { get; set; } = DefaultTtlHours;

    /// <summary>The configured limits, with a non-positive value read as its default.</summary>
    public AiCacheOptions Normalized() => new()
    {
        MaxEntries = MaxEntries > 0 ? MaxEntries : DefaultMaxEntries,
        MaxEntriesPerOrg = MaxEntriesPerOrg > 0 ? MaxEntriesPerOrg : DefaultMaxEntriesPerOrg,
        TtlHours = TtlHours > 0 ? TtlHours : DefaultTtlHours,
    };
}
