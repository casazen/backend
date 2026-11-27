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
    [Required(ErrorMessage = "OctorateHandoffTopicRequired")]
    public OctorateHandoffTopic? Topic { get; set; }

    [Required(ErrorMessage = "OctorateHandoffPropertyRequired")]
    public Guid? PropertyId { get; set; }

    public Guid? BookingId { get; set; }

    [MaxLength(OctorateHandoffInput.MaxPeriodLength, ErrorMessage = "OctorateHandoffPeriodInvalid")]
    public string? Period { get; set; }

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

public sealed record OctorateComplianceHandoffGuideDto(string Code, string MenuPath, string Check)
{
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
