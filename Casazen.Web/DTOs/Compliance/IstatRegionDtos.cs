using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// One row of the ISTAT region table as the platform staff sees it (<c>GET api/admin/istat-regions</c>, E21/F01/US01/T01). A value the source does not
/// state is null or <c>Unknown</c> («da verificare»). <c>requiredGuestFields</c> is null when the row is not verified (no field is asked) and a list,
/// possibly empty, once it is.
/// </summary>
public sealed class IstatRegionRuleDto
{
    public Guid Id { get; init; }

    public string RegionIstatCode { get; init; } = string.Empty;

    /// <summary>The plate code of the autonomous province (<c>BZ</c>, <c>TN</c>) the row is about; null for a region.</summary>
    public string? ProvinceCode { get; init; }

    public string RegionName { get; init; } = string.Empty;

    public string? System { get; init; }

    public string? PortalUrl { get; init; }

    public IstatDeadlineKind DeadlineKind { get; init; }

    public int? DeadlineDay { get; init; }

    public IReadOnlyList<string>? RequiredGuestFields { get; init; }

    public IstatCoverage OctorateCoverage { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public DateTime? VerifiedAt { get; init; }

    public string? Notes { get; init; }

    public DateTime UpdatedAt { get; init; }

    public static IstatRegionRuleDto From(IstatRegionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new IstatRegionRuleDto
        {
            Id = rule.Id,
            RegionIstatCode = rule.RegionIstatCode,
            ProvinceCode = rule.ProvinceCode,
            RegionName = rule.RegionName,
            System = rule.System,
            PortalUrl = rule.PortalUrl,
            DeadlineKind = rule.DeadlineKind,
            DeadlineDay = rule.DeadlineDay,
            RequiredGuestFields = rule.RequiredGuestFields is null ? null : IstatRegionResolver.ParseFields(rule.RequiredGuestFields),
            OctorateCoverage = rule.OctorateCoverage,
            SourceUrl = rule.SourceUrl,
            VerifiedAt = rule.VerifiedAt,
            Notes = rule.Notes,
            UpdatedAt = rule.UpdatedAt,
        };
    }
}

/// <summary>
/// What the platform staff saves for a region or autonomous province (<c>PUT api/admin/istat-regions</c>): the territory names the row and the rest
/// replaces what it holds. <c>requiredGuestFields</c>: null = not verified (none is asked), an empty list = verified, none required; the names are
/// <c>TourismType</c>, <c>TravelWay</c> and <c>EducationLevel</c>. <c>sourceUrl</c> is required and https; <c>verifiedAt</c> is the day the row was checked
/// against it (now when left out).
/// </summary>
public sealed class UpdateIstatRegionRequest
{
    [Required(ErrorMessage = "IstatRegionFieldRequired")]
    [RegularExpression("^[0-9]{2}$", ErrorMessage = "IstatRegionFieldInvalid")]
    public string? RegionIstatCode { get; set; }

    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "IstatRegionFieldInvalid")]
    public string? ProvinceCode { get; set; }

    [MaxLength(IstatRegionRule.SystemMaxLength, ErrorMessage = "IstatRegionFieldMaxLength")]
    public string? System { get; set; }

    [MaxLength(IstatRegionRule.UrlMaxLength, ErrorMessage = "IstatRegionFieldMaxLength")]
    public string? PortalUrl { get; set; }

    [Required(ErrorMessage = "IstatRegionFieldRequired")]
    public IstatDeadlineKind? DeadlineKind { get; set; }

    [Range(1, 31, ErrorMessage = "IstatRegionFieldInvalid")]
    public int? DeadlineDay { get; set; }

    [MaxLength(6, ErrorMessage = "IstatRegionFieldMaxLength")]
    public List<string>? RequiredGuestFields { get; set; }

    [Required(ErrorMessage = "IstatRegionFieldRequired")]
    public IstatCoverage? OctorateCoverage { get; set; }

    [Required(ErrorMessage = "IstatRegionFieldRequired")]
    [MaxLength(IstatRegionRule.UrlMaxLength, ErrorMessage = "IstatRegionFieldMaxLength")]
    public string? SourceUrl { get; set; }

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(IstatRegionRule.NotesMaxLength, ErrorMessage = "IstatRegionFieldMaxLength")]
    public string? Notes { get; set; }

    /// <summary>The update the service applies.</summary>
    public IstatRegionRuleUpdate ToUpdate() => new(
        RegionIstatCode!,
        ProvinceCode,
        System,
        PortalUrl,
        DeadlineKind!.Value,
        DeadlineDay,
        RequiredGuestFields,
        OctorateCoverage!.Value,
        SourceUrl!,
        VerifiedAt,
        Notes);
}

/// <summary>
/// The ISTAT communication of a property as the region table says it (<c>GET api/properties/{propertyId}/istat-coverage</c>, E21/F01/US01/T01). Every
/// value the table does not state is «da verificare»: null, <c>Unknown</c>. <c>coverage</c> is never <c>Covered</c> unless the staff verified it, and
/// <c>nextStep</c> follows it: <c>ConnectInPanel</c>, <c>ManualPortal</c> or <c>AskSupport</c> (then <c>assistContext</c> names the request to open).
/// <c>requiredGuestFields</c> are the statistical fields the guests of the region are asked; CasaZen collects them and does not pass them on to the channel
/// manager. No personal data and no secret.
/// </summary>
public sealed class IstatCoverageDto
{
    public Guid PropertyId { get; init; }

    /// <summary>False for a property outside Italy: no region, no next step.</summary>
    public bool Applies { get; init; }

    /// <summary>True when the comune of the property is in the list and the table has the row of its territory.</summary>
    public bool RegionKnown { get; init; }

    public string? RegionIstatCode { get; init; }

    public string? ProvinceCode { get; init; }

    public string? RegionName { get; init; }

    public string? System { get; init; }

    public string? PortalUrl { get; init; }

    public IstatDeadlineKind DeadlineKind { get; init; }

    /// <summary>The day of the following month the cockpit uses for the ISTAT month; null when none is stated.</summary>
    public int? DeadlineDay { get; init; }

    public IReadOnlyList<string> RequiredGuestFields { get; init; } = [];

    /// <summary>False when the row is not verified: no field is asked, and none can be said to be not required.</summary>
    public bool RequiredGuestFieldsKnown { get; init; }

    public IstatCoverage Coverage { get; init; }

    public ComplianceSenderType IstatSender { get; init; }

    public IstatNextStep? NextStep { get; init; }

    public string? AssistContext { get; init; }

    public string? SourceUrl { get; init; }

    public DateTime? VerifiedAt { get; init; }

    public static IstatCoverageDto From(IstatCoverageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new IstatCoverageDto
        {
            PropertyId = view.PropertyId,
            Applies = view.Applies,
            RegionKnown = view.RegionKnown,
            RegionIstatCode = view.RegionIstatCode,
            ProvinceCode = view.ProvinceCode,
            RegionName = view.RegionName,
            System = view.System,
            PortalUrl = view.PortalUrl,
            DeadlineKind = view.DeadlineKind,
            DeadlineDay = view.DeadlineDay,
            RequiredGuestFields = view.RequiredGuestFields,
            RequiredGuestFieldsKnown = view.RequiredGuestFieldsKnown,
            Coverage = view.Coverage,
            IstatSender = view.IstatSender,
            NextStep = view.NextStep,
            AssistContext = view.AssistContext,
            SourceUrl = view.SourceUrl,
            VerifiedAt = view.VerifiedAt,
        };
    }
}
