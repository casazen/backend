using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class TouristTaxDeclarationDto
{
    public int Year { get; init; }

    public string Currency { get; init; } = "EUR";

    public IReadOnlyList<TouristTaxDeclarationComuneDto> Comuni { get; init; } = [];

    public TouristTaxSummaryTotalsDto Total { get; init; } = new();

    public static TouristTaxDeclarationDto From(TouristTaxDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return new TouristTaxDeclarationDto
        {
            Year = declaration.Year,
            Comuni = declaration.Comuni.Select(TouristTaxDeclarationComuneDto.From).ToList(),
            Total = TouristTaxSummaryTotalsDto.From(declaration.Total),
        };
    }
}

public sealed class TouristTaxDeclarationComuneDto
{
    public ComuneTaxSummaryDto Summary { get; init; } = new();

    public int TaxedStays { get; init; }

    /// <summary>
    /// The stays with no amount by reason (<see cref="TouristTaxStayReasons"/>: <c>exempt</c>, <c>collected_by_channel</c>, <c>not_calculated</c>, <c>no_tax_due</c>): a stay is never taken for a zero because it has none.
    /// Their sum is <c>summary.staysWithoutAmount</c>.
    /// </summary>
    public IReadOnlyList<TouristTaxReasonCountDto> StaysWithoutAmount { get; init; } = [];

    /// <summary>
    /// The exempt guests by reason: a count, never a guest. The reason can reveal a health condition (art. 9 GDPR), so it is decided on the server: a caller who holds <c>guest.read</c> gets the code of the catalogue
    /// (<c>resident</c>, <c>health_care</c>...) or <c>erased</c> once the reason went with the guest's data; everybody else gets <c>minor_age</c> (the age) and one <c>restricted</c> row that counts the other exempt guests.
    /// </summary>
    public IReadOnlyList<TouristTaxReasonCountDto> Exemptions { get; init; } = [];

    public static TouristTaxDeclarationComuneDto From(TouristTaxDeclarationComune comune)
    {
        ArgumentNullException.ThrowIfNull(comune);

        return new TouristTaxDeclarationComuneDto
        {
            Summary = ComuneTaxSummaryDto.From(comune.Summary),
            TaxedStays = comune.TaxedStays,
            StaysWithoutAmount = comune.StaysWithoutAmount.Select(TouristTaxReasonCountDto.From).ToList(),
            Exemptions = comune.Exemptions.Select(TouristTaxReasonCountDto.From).ToList(),
        };
    }
}

public sealed class TouristTaxReasonCountDto
{
    public string Reason { get; init; } = string.Empty;

    public int Stays { get; init; }

    public int Guests { get; init; }

    public int GuestNights { get; init; }

    public static TouristTaxReasonCountDto From(TouristTaxReasonCount count)
    {
        ArgumentNullException.ThrowIfNull(count);

        return new TouristTaxReasonCountDto
        {
            Reason = count.Reason,
            Stays = count.Stays,
            Guests = count.Guests,
            GuestNights = count.GuestNights,
        };
    }
}

public sealed class TouristTaxAgentAccountDto
{
    public int Year { get; init; }

    public TouristTaxAccountPeriod By { get; init; }

    public string Currency { get; init; } = "EUR";

    public IReadOnlyList<TouristTaxAgentAccountRowDto> Rows { get; init; } = [];

    public IReadOnlyList<TouristTaxRemittanceDto> Remittances { get; init; } = [];

    public TouristTaxSummaryTotalsDto Total { get; init; } = new();

    public static TouristTaxAgentAccountDto From(TouristTaxAgentAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new TouristTaxAgentAccountDto
        {
            Year = account.Year,
            By = account.By,
            Rows = account.Rows.Select(TouristTaxAgentAccountRowDto.FromRow).ToList(),
            Remittances = account.Remittances.Select(TouristTaxRemittanceDto.From).ToList(),
            Total = TouristTaxSummaryTotalsDto.From(account.Total),
        };
    }
}

public sealed class TouristTaxAgentAccountRowDto
{
    public string Comune { get; init; } = string.Empty;

    public string Period { get; init; } = string.Empty;

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public bool IsYear { get; init; }

    public int Stays { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    public decimal Collected { get; init; }

    public int StaysWithoutAmount { get; init; }

    public decimal Remitted { get; init; }

    public decimal Balance { get; init; }

    public static TouristTaxAgentAccountRowDto FromRow(TouristTaxAgentAccountRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TouristTaxAgentAccountRowDto
        {
            Comune = row.Comune,
            Period = row.Period,
            From = row.From,
            To = row.To,
            IsYear = row.IsYear,
            Stays = row.Stays,
            Nights = row.Nights,
            Guests = row.Guests,
            Collected = row.Collected,
            StaysWithoutAmount = row.StaysWithoutAmount,
            Remitted = row.Remitted,
            Balance = row.Balance,
        };
    }
}

public sealed class TicinoYearlyCountDto
{
    public int Year { get; init; }

    public DateOnly DueDate { get; init; }

    public int CantonUnknownCount { get; init; }

    public IReadOnlyList<TicinoPropertyCountDto> Properties { get; init; } = [];

    public static TicinoYearlyCountDto From(TicinoYearlyCount count)
    {
        ArgumentNullException.ThrowIfNull(count);

        return new TicinoYearlyCountDto
        {
            Year = count.Year,
            DueDate = count.DueDate,
            CantonUnknownCount = count.Properties.Count(p => p.CantonUnknown),
            Properties = count.Properties.Select(TicinoPropertyCountDto.From).ToList(),
        };
    }
}

public sealed class TicinoPropertyCountDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string? MunicipalityCode { get; init; }

    public bool MunicipalityDerived { get; init; }

    public string Currency { get; init; } = string.Empty;

    public int Stays { get; init; }

    public int Nights { get; init; }

    public int TaxedStays { get; init; }

    public int TaxedNights { get; init; }

    public int UntaxedStays { get; init; }

    public int UntaxedNights { get; init; }

    public int StaysWithoutDocument { get; init; }

    public int NightsWithoutDocument { get; init; }

    public decimal TouristTax { get; init; }

    public bool CantonUnknown { get; init; }

    public static TicinoPropertyCountDto From(TicinoPropertyCount property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new TicinoPropertyCountDto
        {
            PropertyId = property.PropertyId,
            PropertyName = property.PropertyName,
            Address = property.Address,
            City = property.City,
            MunicipalityCode = property.MunicipalityCode,
            MunicipalityDerived = property.MunicipalityDerived,
            Currency = property.Currency,
            Stays = property.Stays,
            Nights = property.Nights,
            TaxedStays = property.TaxedStays,
            TaxedNights = property.TaxedNights,
            UntaxedStays = property.UntaxedStays,
            UntaxedNights = property.UntaxedNights,
            StaysWithoutDocument = property.StaysWithoutDocument,
            NightsWithoutDocument = property.NightsWithoutDocument,
            TouristTax = property.TouristTax,
            CantonUnknown = property.CantonUnknown,
        };
    }
}
