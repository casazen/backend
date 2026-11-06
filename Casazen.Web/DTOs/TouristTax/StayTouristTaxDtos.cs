using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;

namespace Casazen.Web.DTOs.TouristTax;

/// <summary>
/// The tourist tax of a stay guest by guest (<c>GET api/bookings/{id}/tourist-tax</c>, NF-46). No name, no document: positions only. When no
/// rate is known <see cref="MissingRate"/> is true and the amounts are null, never 0.
/// </summary>
public sealed class StayTouristTaxDto
{
    public Guid BookingId { get; init; }

    /// <summary><c>Calculated</c>, <c>RateUnavailable</c>, <c>CategoryRequired</c>, <c>ChildAgesRequired</c> or <c>NightlyPriceRequired</c>.</summary>
    public TouristTaxQuoteStatus Status { get; init; }

    /// <summary><c>Official</c>, <c>HostDeclared</c> (the org's own rate) or <c>None</c>.</summary>
    public TouristTaxRateSource RateSource { get; init; }

    public bool MissingRate { get; init; }
    public int Nights { get; init; }
    public int TaxableNights { get; init; }
    public bool AgeRulesApply { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public decimal? Total { get; init; }

    /// <summary>What the host must collect: 0 when the channel collects it, the total otherwise.</summary>
    public decimal? AmountDue { get; init; }

    /// <summary><c>Channel</c>, <c>Host</c> or null while unknown.</summary>
    public TouristTaxCollector? Collector { get; init; }

    /// <summary><c>ChannelRule</c>, <c>HostDeclared</c> or <c>Default</c>.</summary>
    public TouristTaxCollectorSource CollectorSource { get; init; }

    /// <summary>A channel stay with no rule and no declaration: ask the host who collected (<c>PUT …/tourist-tax/collector</c>).</summary>
    public bool CollectorNeeded { get; init; }

    public IReadOnlyList<StayTouristTaxGuestDto> Guests { get; init; } = [];

    /// <summary>The reasons the host may choose (active, not automatic): the code and the i18n key of its label.</summary>
    public IReadOnlyList<TouristTaxExemptionReasonDto> Reasons { get; init; } = [];

    public DateTime? ConfirmedAt { get; init; }

    /// <summary>The confirmed tax no longer matches the stay (rates, guests, exemptions or collector changed since).</summary>
    public bool Stale { get; init; }

    public static StayTouristTaxDto From(StayTouristTaxView view) => new()
    {
        BookingId = view.BookingId,
        Status = view.Status,
        RateSource = view.RateSource,
        MissingRate = view.MissingRate,
        Nights = view.Nights,
        TaxableNights = view.TaxableNights,
        AgeRulesApply = view.AgeRulesApply,
        Categories = view.Categories,
        Total = view.Total,
        AmountDue = view.AmountDue,
        Collector = view.Collector,
        CollectorSource = view.CollectorSource,
        CollectorNeeded = view.CollectorNeeded,
        Guests = view.Guests.Select(g => new StayTouristTaxGuestDto
        {
            Position = g.Position,
            Nights = g.Nights,
            TaxableNights = g.TaxableNights,
            Amount = g.Amount,
            ExemptionKind = g.ExemptionKind,
            ExemptionCode = g.ExemptionCode,
        }).ToList(),
        Reasons = view.Reasons.Select(r => new TouristTaxExemptionReasonDto { Code = r.Code, LabelKey = r.LabelKey }).ToList(),
        ConfirmedAt = view.ConfirmedAt,
        Stale = view.Stale,
    };
}

/// <summary>The tax of one guest: a position of the stay, never a person.</summary>
public sealed class StayTouristTaxGuestDto
{
    public int Position { get; init; }
    public int Nights { get; init; }
    public int TaxableNights { get; init; }

    /// <summary>Null when the tax of the stay is not calculated.</summary>
    public decimal? Amount { get; init; }

    /// <summary><c>None</c>, <c>Age</c> (automatic) or <c>Host</c> (a reason the host chose).</summary>
    public TouristTaxExemptionKind ExemptionKind { get; init; }

    public string? ExemptionCode { get; init; }
}

public sealed class TouristTaxExemptionReasonDto
{
    public string Code { get; init; } = string.Empty;
    public string LabelKey { get; init; } = string.Empty;
}

/// <summary>The exemptions the host chooses (<c>PUT api/bookings/{id}/tourist-tax/exemptions</c>); a null code clears one.</summary>
public sealed class SetTouristTaxExemptionsRequest
{
    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    public List<GuestExemptionDto> Exemptions { get; set; } = [];
}

public sealed class GuestExemptionDto
{
    [Range(0, 1000, ErrorMessage = "TouristTaxFieldInvalid")]
    public int Position { get; set; }

    [MaxLength(TouristTaxExemptionReason.CodeMaxLength, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? Code { get; set; }
}

/// <summary>Who collected the tax of a channel stay with no channel rule (<c>PUT api/bookings/{id}/tourist-tax/collector</c>); null clears it.</summary>
public sealed class SetTouristTaxCollectorRequest
{
    /// <summary><c>Channel</c> (the channel collected and pays the comune) or <c>Host</c>.</summary>
    public TouristTaxCollector? Collector { get; set; }
}

/// <summary>The comune of a property for its tourist tax and the rate the org declared for it (<c>GET api/properties/{id}/tourist-tax-rate</c>).</summary>
public sealed class MunicipalityRateDto
{
    public Guid PropertyId { get; init; }
    public string? IstatCode { get; init; }
    public string? City { get; init; }

    /// <summary>An official rate is in force in the comune today: no rate can be declared, the official one applies.</summary>
    public bool OfficialRateInForce { get; init; }

    public OrgTouristTaxRateDto? Rate { get; init; }

    public static MunicipalityRateDto From(MunicipalityRateView view) => new()
    {
        PropertyId = view.PropertyId,
        IstatCode = view.IstatCode,
        City = view.City,
        OfficialRateInForce = view.OfficialRateInForce,
        Rate = view.Rate is { } rate
            ? new OrgTouristTaxRateDto
            {
                RatePerPersonPerNight = rate.RatePerPersonPerNight,
                MaxNights = rate.MaxNights,
                MinimumAge = rate.MinimumAge,
                SeasonStart = rate.SeasonStart,
                SeasonEnd = rate.SeasonEnd,
                EffectiveFrom = rate.EffectiveFrom,
                SourceNote = rate.SourceNote,
                UpdatedAt = rate.UpdatedAt,
            }
            : null,
    };
}

public sealed class OrgTouristTaxRateDto
{
    public decimal RatePerPersonPerNight { get; init; }
    public int? MaxNights { get; init; }
    public int MinimumAge { get; init; }
    public string? SeasonStart { get; init; }
    public string? SeasonEnd { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public string SourceNote { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// The rate the org declares for a comune with no official one (<c>PUT api/properties/{id}/tourist-tax-rate</c>): a fixed amount per person per
/// night (0.01 to 50), the cap of nights, the exemption age (0 to 18), the season (<c>MM-dd</c>, both or none), the first day and the source.
/// </summary>
public sealed class SaveMunicipalityRateRequest
{
    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    public decimal? RatePerPersonPerNight { get; set; }

    public int? MaxNights { get; set; }

    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    public int? MinimumAge { get; set; }

    [MaxLength(5, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? SeasonStart { get; set; }

    [MaxLength(5, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? SeasonEnd { get; set; }

    public DateOnly? EffectiveFrom { get; set; }

    [MaxLength(OrgTouristTaxRate.SourceNoteMaxLength, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? SourceNote { get; set; }

    public MunicipalityRateInput ToInput() => new(
        RatePerPersonPerNight!.Value,
        MaxNights,
        MinimumAge!.Value,
        SeasonStart,
        SeasonEnd,
        EffectiveFrom,
        SourceNote);
}

/// <summary>
/// A channel rule of a comune (<c>PUT api/admin/tourist-tax/channel-rules</c>): which channel collects the tourist tax there, from which official
/// source. Upserted by channel and comune.
/// </summary>
public sealed class UpsertTouristTaxChannelRuleRequest
{
    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    public BookingSource? Channel { get; set; }

    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    [RegularExpression("^[0-9]{6}$", ErrorMessage = "TouristTaxFieldInvalid")]
    public string? IstatCode { get; set; }

    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    public TouristTaxCollector? Collector { get; set; }

    [Required(ErrorMessage = "TouristTaxFieldRequired")]
    [MaxLength(TouristTaxChannelRule.SourceUrlMaxLength, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? SourceUrl { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(TouristTaxChannelRule.NotesMaxLength, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? Notes { get; set; }
}

public sealed class TouristTaxChannelRuleDto
{
    public Guid Id { get; init; }
    public BookingSource Channel { get; init; }
    public string IstatCode { get; init; } = string.Empty;
    public TouristTaxCollector Collector { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public string? Notes { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static TouristTaxChannelRuleDto From(TouristTaxChannelRule rule) => new()
    {
        Id = rule.Id,
        Channel = rule.Channel,
        IstatCode = rule.IstatCode,
        Collector = rule.Collector,
        SourceUrl = rule.SourceUrl,
        IsActive = rule.IsActive,
        VerifiedAt = rule.VerifiedAt,
        Notes = rule.Notes,
        UpdatedAt = rule.UpdatedAt,
    };
}

/// <summary>The Octorate code and the state of an exemption reason (<c>PUT api/admin/tourist-tax/exemption-reasons/{code}</c>).</summary>
public sealed class UpdateTouristTaxExemptionReasonRequest
{
    /// <summary>The Octorate <c>cityTaxExemption</c> reason this one matches; null: no known match.</summary>
    [MaxLength(TouristTaxExemptionReason.OctorateCodeMaxLength, ErrorMessage = "TouristTaxFieldMaxLength")]
    public string? OctorateExemptionCode { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class AdminTouristTaxExemptionReasonDto
{
    public string Code { get; init; } = string.Empty;
    public string LabelKey { get; init; } = string.Empty;
    public bool IsAutomatic { get; init; }
    public string? OctorateExemptionCode { get; init; }
    public bool IsActive { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static AdminTouristTaxExemptionReasonDto From(TouristTaxExemptionReason reason) => new()
    {
        Code = reason.Code,
        LabelKey = reason.LabelKey,
        IsAutomatic = reason.IsAutomatic,
        OctorateExemptionCode = reason.OctorateExemptionCode,
        IsActive = reason.IsActive,
        UpdatedAt = reason.UpdatedAt,
    };
}
