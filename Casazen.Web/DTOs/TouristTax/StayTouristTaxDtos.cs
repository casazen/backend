using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Web.DTOs.CheckIn;

namespace Casazen.Web.DTOs.TouristTax;

public sealed class StayTouristTaxDto
{
    public Guid BookingId { get; init; }

    public TouristTaxQuoteStatus Status { get; init; }

    public TouristTaxRateSource RateSource { get; init; }

    public bool MissingRate { get; init; }
    public int Nights { get; init; }
    public int TaxableNights { get; init; }
    public bool AgeRulesApply { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public decimal? Total { get; init; }

    /// <summary>What the host must collect: 0 when the channel collects it, the total otherwise.</summary>
    public decimal? AmountDue { get; init; }

    public TouristTaxCollector? Collector { get; init; }

    public TouristTaxCollectorSource CollectorSource { get; init; }

    public bool CollectorNeeded { get; init; }

    public IReadOnlyList<StayTouristTaxGuestDto> Guests { get; init; } = [];

    public IReadOnlyList<TouristTaxExemptionReasonDto> Reasons { get; init; } = [];

    public DateTime? ConfirmedAt { get; init; }

    /// <summary>The confirmed tax no longer matches the stay (rates, guests, exemptions or collector changed since).</summary>
    public bool Stale { get; init; }

    public TouristTaxCollectionDto? Collection { get; init; }

    /// <param name="view">The tax of the stay.</param>
    /// <param name="includeExemptionCodes">
    /// False for a caller who does not hold <c>guest.read</c>: the reason of an exemption can reveal a health condition (art. 9 GDPR), so its code
    /// is <c>null</c> (the kind of the exemption and the amount stay).
    /// </param>
    /// <param name="collection">The collection block, when the caller reads it.</param>
    public static StayTouristTaxDto From(StayTouristTaxView view, bool includeExemptionCodes, TouristTaxCollectionDto? collection = null) => new()
    {
        Collection = collection,
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
            ExemptionCode = includeExemptionCodes ? g.ExemptionCode : null,
        }).ToList(),
        Reasons = view.Reasons.Select(r => new TouristTaxExemptionReasonDto { Code = r.Code, LabelKey = r.LabelKey }).ToList(),
        ConfirmedAt = view.ConfirmedAt,
        Stale = view.Stale,
    };
}

public sealed class StayTouristTaxGuestDto
{
    public int Position { get; init; }
    public int Nights { get; init; }
    public int TaxableNights { get; init; }

    public decimal? Amount { get; init; }

    public TouristTaxExemptionKind ExemptionKind { get; init; }

    public string? ExemptionCode { get; init; }
}

public sealed class TouristTaxExemptionReasonDto
{
    public string Code { get; init; } = string.Empty;
    public string LabelKey { get; init; } = string.Empty;
}

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

public sealed class SetTouristTaxCollectorRequest
{
    public TouristTaxCollector? Collector { get; set; }
}

public sealed class MunicipalityRateDto
{
    public Guid PropertyId { get; init; }
    public string? IstatCode { get; init; }
    public string? City { get; init; }

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

public sealed class UpdateTouristTaxExemptionReasonRequest
{
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
