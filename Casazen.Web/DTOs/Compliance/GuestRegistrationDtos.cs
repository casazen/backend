using System.Text.Json.Serialization;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// What the regime of the place of a property says and what CasaZen does with it (<c>GET api/properties/{id}/guest-registration</c>, E29/F02/US01/T01,
/// NF-125): the card of the property. The enums are written by name; every sentence is in the language of the request.
/// </summary>
public sealed class GuestRegistrationDto
{
    /// <summary>The regime of the place: Italy, one of the 26 cantons, or the unknown Swiss place. Never <c>null</c> for a property of Italy or Switzerland.</summary>
    public GuestRegistrationRegimeDto? Regime { get; init; }

    /// <summary>The duty CasaZen treats as in effect: <c>Required</c> (raised), <c>NotRequired</c> (not raised) or <c>Ask</c> (the host decides, nothing in red).</summary>
    public GuestRegistrationEffectiveDuty EffectiveDuty { get; init; }

    /// <summary>The host's setting: <c>Required</c>, <c>NotRequired</c> or <c>null</c> (follow the regime).</summary>
    public GuestNotificationOverride? Override { get; init; }

    /// <summary>Where the one place resolver puts a Swiss property (the municipality, the canton, whether it is ambiguous); <c>null</c> outside Switzerland.</summary>
    public SwissPlaceDto? Place { get; init; }

    /// <summary>
    /// What the property still lacks to be notified, as stable reason codes: <c>swiss_municipality_missing</c> (the municipality is not known, so neither is the
    /// canton) and <c>ticino_structure_id_missing</c> (a property of Ticino has no structure number). Empty when nothing is missing and for an Italian property.
    /// </summary>
    public IReadOnlyList<string> IdentifierMissing { get; init; } = [];

    public static GuestRegistrationDto From(GuestRegistrationResolution resolution, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(localizer);
        return new GuestRegistrationDto
        {
            Regime = resolution.Regime is null ? null : GuestRegistrationRegimeDto.From(resolution.Regime, localizer),
            EffectiveDuty = resolution.EffectiveDuty,
            Override = resolution.Override,
            Place = resolution.Place is null ? null : SwissPlaceDto.From(resolution.Place),
            IdentifierMissing = resolution.IdentifierMissing,
        };
    }
}

/// <summary>
/// One regime as the card reads it: the same fields as the catalog, with the sentences resolved in the language of the request.
/// <c>deadlineNote</c> and <c>summary</c> are the user-facing texts; <c>sources</c> and <c>legalReferences</c> are the pages and the laws the entry comes from,
/// in their own language; <c>certainty</c> says how far they are to be trusted (<c>Verified</c>, <c>Partial</c>, <c>Unverified</c>) and <c>verifiedOn</c> when they were read.
/// </summary>
public sealed class GuestRegistrationRegimeDto
{
    public string Code { get; init; } = string.Empty;

    public string CountryCode { get; init; } = string.Empty;

    public string? CantonCode { get; init; }

    public GuestRegistrationObligation Obligation { get; init; }

    public GuestRegistrationChannel Channel { get; init; }

    public GuestRegistrationDeadlineKind DeadlineKind { get; init; }

    /// <summary>The sentence that says what the deadline is when its kind alone does not; <c>null</c> when it needs none.</summary>
    public string? DeadlineNote { get; init; }

    public int? RetentionYears { get; init; }

    public GuestRegistrationPrivateRentals PrivateRentals { get; init; }

    public string? PortalUrl { get; init; }

    public IReadOnlyList<string> LegalReferences { get; init; } = [];

    public IReadOnlyList<GuestRegistrationSourceDto> Sources { get; init; } = [];

    /// <summary>The day the entry was read on its sources (<c>yyyy-MM-dd</c>).</summary>
    public DateOnly VerifiedOn { get; init; }

    public GuestRegistrationCertainty Certainty { get; init; }

    public GuestRegistrationSupport CasaZenSupport { get; init; }

    /// <summary>CasaZen keeps the photograph of the identity document of the guests: <c>false</c> for every Swiss regime.</summary>
    public bool RetainDocumentImages { get; init; }

    /// <summary>The sentence that says what the rule is and what CasaZen does.</summary>
    public string Summary { get; init; } = string.Empty;

    public static GuestRegistrationRegimeDto From(GuestRegistrationRegime regime, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(regime);
        ArgumentNullException.ThrowIfNull(localizer);
        return new GuestRegistrationRegimeDto
        {
            Code = regime.Code,
            CountryCode = regime.CountryCode,
            CantonCode = regime.CantonCode,
            Obligation = regime.Obligation,
            Channel = regime.Channel,
            DeadlineKind = regime.DeadlineKind,
            DeadlineNote = regime.DeadlineNoteKey is null ? null : localizer[regime.DeadlineNoteKey].Value,
            RetentionYears = regime.RetentionYears,
            PrivateRentals = regime.PrivateRentals,
            PortalUrl = regime.PortalUrl,
            LegalReferences = regime.LegalReferences,
            Sources = [.. regime.Sources.Select(source => new GuestRegistrationSourceDto { Url = source.Url, Title = source.Title })],
            VerifiedOn = regime.VerifiedOn,
            Certainty = regime.Certainty,
            CasaZenSupport = regime.CasaZenSupport,
            RetainDocumentImages = regime.RetainDocumentImages,
            Summary = localizer[regime.SummaryKey].Value,
        };
    }
}

/// <summary>A source of a regime: the address and the title of the page or of the text.</summary>
public sealed class GuestRegistrationSourceDto
{
    public string Url { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// The body of <c>PUT api/properties/{id}/guest-registration</c>: the host's setting. <c>override</c> is <c>Required</c>, <c>NotRequired</c> or <c>null</c> (follow
/// the regime again); the member must be there: a body without it is a 400, never «follow the regime» by omission.
/// </summary>
public sealed class UpdateGuestRegistrationRequest
{
    /// <summary><c>Required</c>, <c>NotRequired</c> or <c>null</c>.</summary>
    public GuestNotificationOverride? Override
    {
        get;
        set
        {
            field = value;
            OverrideSent = true;
        }
    }

    /// <summary>True when the body carries <see cref="Override"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool OverrideSent { get; private set; }
}
