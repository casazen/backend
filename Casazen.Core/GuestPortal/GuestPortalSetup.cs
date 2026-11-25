using System.Text.Json;

namespace Casazen.Core.GuestPortal;

/// <summary>The host's saved choices for one accommodation.</summary>
public sealed record GuestPortalSetup
{
    public bool BrandConfirmed { get; init; }
    public bool GuideConfirmed { get; init; }
    public bool ExtrasConfirmed { get; init; }
    public bool? RegionalSuppliers { get; init; }
    public bool ChatConfirmed { get; init; }
    public bool? ReviewsEnabled { get; init; }
    public bool HidePaidServicesOnAirbnb { get; init; }
    public bool OtaRulesAcknowledged { get; init; }

    public bool IsComplete(bool hasGuide) =>
        BrandConfirmed && GuideConfirmed && hasGuide && ExtrasConfirmed && RegionalSuppliers.HasValue &&
        ChatConfirmed && ReviewsEnabled.HasValue && OtaRulesAcknowledged;

    public static GuestPortalSetup Read(string? json) =>
        json is null ? new() : JsonSerializer.Deserialize<GuestPortalSetup>(json) ?? new();

    public string Write() => JsonSerializer.Serialize(this);
}
