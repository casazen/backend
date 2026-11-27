using System.Text.Json.Serialization;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.Compliance;

public sealed class GuestRegistrationDto
{
    public GuestRegistrationRegimeDto? Regime { get; init; }

    public GuestRegistrationEffectiveDuty EffectiveDuty { get; init; }

    public GuestNotificationOverride? Override { get; init; }

    public SwissPlaceDto? Place { get; init; }

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

public sealed class GuestRegistrationRegimeDto
{
    public string Code { get; init; } = string.Empty;

    public string CountryCode { get; init; } = string.Empty;

    public string? CantonCode { get; init; }

    public GuestRegistrationObligation Obligation { get; init; }

    public GuestRegistrationChannel Channel { get; init; }

    public GuestRegistrationDeadlineKind DeadlineKind { get; init; }

    public string? DeadlineNote { get; init; }

    public int? RetentionYears { get; init; }

    public GuestRegistrationPrivateRentals PrivateRentals { get; init; }

    public string? PortalUrl { get; init; }

    public IReadOnlyList<string> LegalReferences { get; init; } = [];

    public IReadOnlyList<GuestRegistrationSourceDto> Sources { get; init; } = [];

    public DateOnly VerifiedOn { get; init; }

    public GuestRegistrationCertainty Certainty { get; init; }

    public GuestRegistrationSupport CasaZenSupport { get; init; }

    public bool RetainDocumentImages { get; init; }

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

public sealed class GuestRegistrationSourceDto
{
    public string Url { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
}

public sealed class UpdateGuestRegistrationRequest
{
    public GuestNotificationOverride? Override
    {
        get;
        set
        {
            field = value;
            OverrideSent = true;
        }
    }

    [JsonIgnore]
    public bool OverrideSent { get; private set; }
}
