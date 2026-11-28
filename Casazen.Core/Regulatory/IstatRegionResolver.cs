using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Regulatory;

/// <summary>The row of the region table that applies to a comune, with what it was found by (E21/F01/US01/T01).</summary>
/// <param name="RegionIstatCode">The ISTAT code of the region of the comune, two digits.</param>
/// <param name="ProvinceCode">The plate code of the province of the comune.</param>
/// <param name="RegionCode">CasaZen's three-letter code of the region (<c>LOM</c>), the key of <c>Compliance:IstatDeadlineDays</c>; null when the region is unknown.</param>
/// <param name="Rule">The row of the table; null when the table has none for this territory.</param>
public sealed record IstatRegionMatch(string RegionIstatCode, string? ProvinceCode, string? RegionCode, IstatRegionRule? Rule);

/// <summary>
/// The pure rules of the ISTAT region table (E21/F01/US01/T01, decision O6): which row applies to a territory, which deadline the cockpit uses, which
/// statistical fields a guest is asked and what the host does next. No database, no call: the services read the rows and ask these.
/// </summary>
public static class IstatRegionResolver
{
    public static IReadOnlyList<string> FieldNames { get; } =
    [
        IstatGuestFieldLists.TourismTypeField,
        IstatGuestFieldLists.TravelWayField,
        IstatGuestFieldLists.EducationLevelField,
    ];

    public static IstatRegionRule? Find(IEnumerable<IstatRegionRule> rules, string? regionIstatCode, string? provinceCode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(regionIstatCode))
            return null;

        var ofRegion = rules
            .Where(r => string.Equals(r.RegionIstatCode, regionIstatCode, StringComparison.Ordinal))
            .ToList();
        if (!string.IsNullOrWhiteSpace(provinceCode)
            && ofRegion.FirstOrDefault(r => string.Equals(r.ProvinceCode, provinceCode, StringComparison.OrdinalIgnoreCase)) is { } ofProvince)
        {
            return ofProvince;
        }

        return ofRegion.FirstOrDefault(r => r.ProvinceCode is null);
    }

    /// <summary>
    /// The day of the following month the ISTAT communication of a month is due by, for the cockpit and its alerts: the day set for the region in
    /// <c>Compliance:IstatDeadlineDays</c> when there is one (an explicit configuration overrides the table), else the monthly day of the row of the
    /// table. Null when neither says one: a region «to define» has no ISTAT item, a deadline is never guessed.
    /// </summary>
    /// <param name="rule">The row of the territory, or null.</param>
    /// <param name="regionCode">CasaZen's three-letter code of the region (<c>LOM</c>), or null when the region is unknown.</param>
    /// <param name="overrides">The configured days by region code (<c>Compliance:IstatDeadlineDays</c>), or null.</param>
    /// <param name="onInvalidOverride">
    /// Called with the region code and the value when the day configured for the region is not a day of the month (1 to 31): such a value is ignored, never
    /// used, so the cockpit (which has no item for a day out of range) and the answer for a property never disagree. The caller logs it.
    /// </param>
    public static int? DeadlineDay(
        IstatRegionRule? rule,
        string? regionCode,
        IReadOnlyDictionary<string, int>? overrides,
        Action<string, int>? onInvalidOverride = null)
    {
        if (regionCode is not null && overrides is not null && overrides.TryGetValue(regionCode, out var configured))
        {
            if (configured is >= 1 and <= 31)
                return configured;

            onInvalidOverride?.Invoke(regionCode, configured);
        }

        return rule?.MonthlyDeadlineDay;
    }

    /// <summary>
    /// Who sends the ISTAT communication of a property once the coverage of its region is known: the sender of the property (ISTAT follows Alloggiati),
    /// except that the channel manager does not send it for a region it does not cover (<see cref="IstatCoverage.NotCovered"/>): the host uses the portal of
    /// the region, so the sender is manual. A coverage not verified, or verified as covered, leaves the sender as it is. The one rule of the coverage of a
    /// property and of the ISTAT item of the cockpit, so the two always say the same and a property is never «handled outside» where nobody outside does it.
    /// </summary>
    /// <param name="sender">The sender of the property (the Alloggiati one).</param>
    /// <param name="coverage">Whether the channel manager covers the region of the property.</param>
    public static ComplianceSenderType IstatSenderFor(ComplianceSenderType sender, IstatCoverage coverage) =>
        sender == ComplianceSenderType.ChannelManager && coverage == IstatCoverage.NotCovered ? ComplianceSenderType.Manual : sender;

    public static IstatNextStep NextStep(IstatCoverage coverage) => coverage switch
    {
        IstatCoverage.Covered => IstatNextStep.ConnectInPanel,
        IstatCoverage.NotCovered => IstatNextStep.ManualPortal,
        _ => IstatNextStep.AskSupport,
    };

    public static IReadOnlyList<string> RequiredFields(IstatRegionRule? rule) => ParseFields(rule?.RequiredGuestFields);

    public static IReadOnlyList<string> ParseFields(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        var wanted = stored
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return FieldNames.Where(wanted.Contains).ToList();
    }

    public static string FormatFields(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', FieldNames.Where(wanted.Contains));
    }

    public static bool TryNormalizeFields(IEnumerable<string> input, out IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = new List<string>();
        foreach (var raw in input)
        {
            var known = FieldNames.FirstOrDefault(f => string.Equals(f, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                names = [];
                return false;
            }

            if (!normalized.Contains(known))
                normalized.Add(known);
        }

        names = FieldNames.Where(normalized.Contains).ToList();
        return true;
    }
}
