using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>GET api/marketplace/health?propertyId=&amp;lang=</c> (E09/F03/US01/T01, NF-57, MKT-04): the health of the active integrations the caller reaches, one row for each
/// channel, tool or function of the channel manager's license that is not turned off. Read from CasaZen's own rows and the quota of the last sync; nothing is asked of
/// Octorate while the page loads.
/// </summary>
public sealed record MarketplaceHealthDto(IReadOnlyList<MarketplaceHealthItemDto> Items)
{
    public static MarketplaceHealthDto From(MarketplaceHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return new MarketplaceHealthDto([.. health.Items.Select(MarketplaceHealthItemDto.From)]);
    }
}

/// <summary>
/// One activation in the health panel. <c>status</c> is <c>Pending</c>, <c>Active</c>, <c>ToFix</c> (or <c>Requested</c>, a function that waits for the license);
/// <c>isDeclared</c> is true while only the host's word backs it. <c>lastSyncedAt</c> is when CasaZen last read it from Octorate. <c>error</c> is the problem in simple
/// words in the language asked, with its stable code; never a text of Octorate's. <c>quota</c> is the quota of the channel manager's API at the last sync of the org (the same
/// for every row, <c>null</c> until a sync read it): <c>used</c> of <c>limit</c> units, and <c>warn</c> when <c>used</c> reached <c>Marketplace:Health:QuotaWarnPercent</c> of
/// <c>limit</c>. <c>changeRequestedAt</c> is set while a change the host asked for is waiting for the license. <c>propertyId</c> and <c>propertyName</c> are <c>null</c> for a row of
/// the whole org.
/// </summary>
public sealed record MarketplaceHealthItemDto(
    Guid ActivationId,
    Guid? PropertyId,
    string? PropertyName,
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    MarketplaceActivationStatus Status,
    bool IsDeclared,
    DateTime? LastSyncedAt,
    MarketplaceHealthErrorDto? Error,
    MarketplaceQuotaDto? Quota,
    DateTime? ChangeRequestedAt)
{
    public static MarketplaceHealthItemDto From(MarketplaceHealthItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new MarketplaceHealthItemDto(
            item.ActivationId,
            item.PropertyId,
            item.PropertyName,
            item.Kind,
            item.Code,
            item.Name,
            item.Status,
            item.IsDeclared,
            item.LastSyncedAt,
            item.Error is { } error ? new MarketplaceHealthErrorDto(error.Code, error.Message) : null,
            item.Quota is { } quota ? new MarketplaceQuotaDto(quota.Used, quota.Limit, quota.Warn, quota.ReadAt) : null,
            item.ChangeRequestedAt);
    }
}

/// <summary>A problem in simple words: <c>code</c> is the stable code a client may translate, <c>message</c> the text in the language asked.</summary>
public sealed record MarketplaceHealthErrorDto(string Code, string Message);

/// <summary>The quota of the channel manager's API at the last sync: <c>used</c> of <c>limit</c> units, <c>warn</c> when the share the setting names is reached, <c>readAt</c> when it was read (UTC).</summary>
public sealed record MarketplaceQuotaDto(int Used, int Limit, bool Warn, DateTime ReadAt);
