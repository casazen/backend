using System.Text.Json;

namespace Casazen.Core.Services;

/// <summary>Commercial choices are a draft until the subscription webhook confirms payment.</summary>
public sealed record CommercialSignup
{
    public string Profile { get; init; } = "owner";
    public string PlanTier { get; init; } = "Starter";
    public int Rooms { get; init; } = 1;
    public string? AgencyName { get; init; }
    public string[] Addons { get; init; } = [];

    // Preserve the serialized form (and Stripe fingerprint) of checkouts started before rental areas were selectable.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RentalArea { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string RentalType => RentalArea ?? (Profile == "long-rent" ? "LongTerm" : "ShortTerm");
    public static CommercialSignup? Read(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CommercialSignup>(json);
    public string Serialize() => JsonSerializer.Serialize(this);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Serialize())));

    public string? Validate()
    {
        if (Profile is not ("agency" or "owner" or "long-rent")) return "Unknown profile";
        if (RentalType is not ("ShortTerm" or "LongTerm" or "Both") || (Profile == "long-rent" && RentalType != "LongTerm")) return "Invalid rental area";
        if (!PlanCatalog.TryParseTier(PlanTier, out var tier)) return "Unknown plan";
        if ((Profile == "agency") != (tier == Entities.Enums.PlanTier.Agency)) return "Agency requires the Agency plan";
        if (Profile == "agency" && (string.IsNullOrWhiteSpace(AgencyName) || AgencyName.Trim().Length > 200)) return "Agency name required";
        if (Rooms is < 1 or > 10000 || (Profile != "agency" && Rooms != 1)) return "Invalid room count";
        if (Addons is null || Addons.Length > 12 || Addons.Distinct(StringComparer.Ordinal).Count() != Addons.Length) return "Invalid add-ons";
        if (tier == Entities.Enums.PlanTier.Starter && Addons.Length > 0) return "Extra features require Pro or higher";
        if (Addons.Any(code => !CommercialAddon.All.Any(a => a.Code == code && a.AppliesTo(RentalType)))) return "Unknown or incompatible add-on";
        return null;
    }
}

public sealed record CommercialAddon(string Code, string Context)
{
    public bool AppliesTo(string rentalType) => rentalType == "Both" || Context == "all" || (rentalType == "LongTerm" ? Context == "long-rent" : Context == "short-rent");
    public static readonly CommercialAddon[] All = [
        new("channels", "short-rent"), new("direct-booking", "short-rent"),
        new("compliance", "short-rent"), new("identification", "short-rent"),
        new("messages-ai", "short-rent"), new("ai-agents", "short-rent"), new("operations", "all"),
        new("fiscal", "all"), new("revenue", "short-rent"), new("crm", "short-rent")
    ];
}
