using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// What the host asks for when it goes to check an Alloggiati or ISTAT outcome in the Cassetto Compliance
/// (<c>POST api/compliance/octorate-handoffs</c>, CHK-15). There is no address in it, on purpose: where the host goes is decided by
/// CasaZen's configuration alone. A member the contract does not know (a <c>url</c>, a <c>redirect</c>) is ignored.
/// </summary>
public sealed class OctorateComplianceHandoffRequest
{
    /// <summary><c>Alloggiati</c> or <c>Istat</c>.</summary>
    [Required(ErrorMessage = "OctorateHandoffTopicRequired")]
    public OctorateHandoffTopic? Topic { get; set; }

    /// <summary>The property the check is about.</summary>
    [Required(ErrorMessage = "OctorateHandoffPropertyRequired")]
    public Guid? PropertyId { get; set; }

    /// <summary>The stay, when the check starts from a booking; it must be a booking of the property.</summary>
    public Guid? BookingId { get; set; }

    /// <summary>The period of the check, <c>yyyy-MM</c> or <c>yyyy-MM-dd</c>, when it has one.</summary>
    [MaxLength(OctorateHandoffInput.MaxPeriodLength, ErrorMessage = "OctorateHandoffPeriodInvalid")]
    public string? Period { get; set; }

    /// <summary>The relative CasaZen route the host comes from (path and query, starting with <c>/</c>): the client saves it to come back to.</summary>
    [Required(ErrorMessage = "OctorateHandoffReturnPathRequired")]
    [MaxLength(OctorateHandoffInput.MaxReturnPathLength, ErrorMessage = "OctorateHandoffReturnPathInvalid")]
    public string? ReturnPath { get; set; }
}

/// <summary>
/// A handoff ready to be used: the address the client opens in the current tab, what the Cassetto Compliance dialog shows and the guide
/// to follow in the panel. The address is the only one the client may open; it never builds one. Nothing here is a result: CasaZen does
/// not know what the panel will show.
/// </summary>
/// <param name="HandoffId">Opaque id the client saves with the route it will come back to.</param>
/// <param name="StartedAt">When the handoff was prepared (UTC).</param>
/// <param name="ExpiresAt">Until when the saved context may be restored (UTC).</param>
/// <param name="Topic"><c>Alloggiati</c> or <c>Istat</c>.</param>
/// <param name="DestinationUrl">The allowlisted address to open in the same tab.</param>
/// <param name="DestinationKind"><c>Home</c> (the authenticated home of the panel: follow the guide) or <c>DeepLink</c> (a verified link to the section).</param>
/// <param name="ReturnPath">The route the host comes from, as CasaZen checked it.</param>
/// <param name="Context">What the dialog shows about the target.</param>
/// <param name="Guide">The menu path in the panel and what to check, in the language of the request.</param>
public sealed record OctorateComplianceHandoffResponse(
    string HandoffId,
    DateTime StartedAt,
    DateTime ExpiresAt,
    OctorateHandoffTopic Topic,
    string DestinationUrl,
    OctorateHandoffDestinationKind DestinationKind,
    string ReturnPath,
    OctorateComplianceHandoffContextDto Context,
    OctorateComplianceHandoffGuideDto Guide)
{
    /// <summary>The answer for a prepared handoff, with the guide written in the language of the request.</summary>
    public static OctorateComplianceHandoffResponse From(OctorateHandoff handoff, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(localizer);

        return new OctorateComplianceHandoffResponse(
            handoff.HandoffId,
            handoff.StartedAt,
            handoff.ExpiresAt,
            handoff.Topic,
            handoff.Destination.Url,
            handoff.Destination.Kind,
            handoff.ReturnPath,
            new OctorateComplianceHandoffContextDto(
                handoff.PropertyId,
                handoff.PropertyName,
                handoff.OctorateListingName,
                handoff.BookingId,
                handoff.BookingCode,
                handoff.Period),
            OctorateComplianceHandoffGuideDto.From(handoff.GuideCode, localizer));
    }
}

/// <summary>The target of the check, as the dialog names it: the host's own property, listing and stay reference, no guest.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="OctorateListingName">The name of the property's listing on the channel manager, when known: the structure to open there.</param>
/// <param name="BookingId">The stay, when the check starts from a booking.</param>
/// <param name="BookingCode">The reference of that stay the host knows (<c>XXXXX-XXXXX</c>).</param>
/// <param name="Period">The period of the check, when it has one.</param>
public sealed record OctorateComplianceHandoffContextDto(
    Guid PropertyId,
    string PropertyName,
    string? OctorateListingName,
    Guid? BookingId,
    string? BookingCode,
    string? Period);

/// <summary>The guide of the check: a stable code, and the two texts of the code in the language of the request.</summary>
/// <param name="Code">One of <see cref="OctorateHandoffGuides.All"/>.</param>
/// <param name="MenuPath">Where to go in the panel, once the host is signed in.</param>
/// <param name="Check">What to look at there: the host's to verify, CasaZen does not know the outcome.</param>
public sealed record OctorateComplianceHandoffGuideDto(string Code, string MenuPath, string Check)
{
    /// <summary>The guide of <paramref name="code"/> from the resources (Italian default, English).</summary>
    public static OctorateComplianceHandoffGuideDto From(string code, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        var (menuKey, checkKey) = code switch
        {
            OctorateHandoffGuides.AlloggiatiDaily => ("OctorateHandoffGuideAlloggiatiDailyMenu", "OctorateHandoffGuideAlloggiatiDailyCheck"),
            OctorateHandoffGuides.AlloggiatiShortStay => ("OctorateHandoffGuideAlloggiatiShortStayMenu", "OctorateHandoffGuideAlloggiatiShortStayCheck"),
            OctorateHandoffGuides.AlloggiatiLongStay => ("OctorateHandoffGuideAlloggiatiLongStayMenu", "OctorateHandoffGuideAlloggiatiLongStayCheck"),
            OctorateHandoffGuides.IstatPeriod => ("OctorateHandoffGuideIstatPeriodMenu", "OctorateHandoffGuideIstatPeriodCheck"),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown handoff guide."),
        };

        return new OctorateComplianceHandoffGuideDto(code, localizer[menuKey].Value, localizer[checkKey].Value);
    }
}
