namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a portal of the channel manager's catalog: the two <c>portalCategory</c> values of <c>GET /rest/v1/meta/portals</c> that the
/// marketplace shows (E09/F01/US01, NF-54). A portal with any other category, or none, is not stored. Stored as its number, never reused.
/// </summary>
public enum MarketplacePortalCategory
{
    /// <summary>A sales channel: an online travel agency or any other site that sells the accommodations (<c>OTA</c>).</summary>
    Ota = 1,

    /// <summary>A tool that works with the channel manager and does not sell the accommodations (<c>EXTERNAL_TOOL</c>).</summary>
    ExternalTool = 2,
}
