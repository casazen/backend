using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The annual declaration data of the tourist tax per comune as a screen reads it (<c>GET /api/tourist-tax/year-end/{year}/declaration?format=json</c>, FIX/BE-YEAREND-JSON on E18/F01/US02/T03, NF-117): the typed
/// result of <see cref="ITouristTaxSummaryService.GetDeclarationAsync"/>, field for field, and the very data of the CSV and Excel files of the same address. Nothing is recomputed here and nothing is filed by
/// CasaZen. Every amount is a number in <see cref="Currency"/>, to the cent (the summaries and the payments, the DTOs of <c>GET summary</c>, say it again in whole cents).
/// </summary>
public sealed class TouristTaxDeclarationDto
{
    /// <summary>The year of the check-ins.</summary>
    public int Year { get; init; }

    /// <summary>Always EUR: only the properties in Italy are in the declaration, a house abroad is left out as the summary leaves it out.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>The comuni with stays or payments in the year, by name.</summary>
    public IReadOnlyList<TouristTaxDeclarationComuneDto> Comuni { get; init; } = [];

    /// <summary>The totals of the comuni, as the summary gives them.</summary>
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

/// <summary>One comune of the declaration of the year: its summary, the stays that have a tax, the stays that have none with the reason, and the exempt guests by reason.</summary>
public sealed class TouristTaxDeclarationComuneDto
{
    /// <summary>
    /// The comune in the year as <c>GET summary</c> gives it: stays, nights, guests, owed, paid, balance (owed less paid, to the cent), status and the payments with their days and references. Year-end reads need a scope of the
    /// whole org, so <c>paid</c>, <c>balance</c>, <c>status</c> and <c>remittances</c> are always there.
    /// </summary>
    public ComuneTaxSummaryDto Summary { get; init; } = new();

    /// <summary>Stays with a recorded amount above zero.</summary>
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

/// <summary>A reason with the stays, the guests and the nights it counts.</summary>
public sealed class TouristTaxReasonCountDto
{
    /// <summary>The reason code, a word of the file and never to be renamed.</summary>
    public string Reason { get; init; } = string.Empty;

    public int Stays { get; init; }

    public int Guests { get; init; }

    /// <summary>The nights of the stay for each guest, added up.</summary>
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

/// <summary>
/// The account of the agent («conto giudiziale») for the year as a screen reads it (<c>GET /api/tourist-tax/year-end/{year}/agent-account?format=json</c>): the typed result of
/// <see cref="ITouristTaxSummaryService.GetAgentAccountAsync"/>, field for field: for each comune the tax collected and remitted by month or quarter, the line of the year, and every payment of the year. Every amount is a
/// number in <see cref="Currency"/>, to the cent.
/// </summary>
public sealed class TouristTaxAgentAccountDto
{
    public int Year { get; init; }

    /// <summary>How the account is cut: <c>Month</c> or <c>Quarter</c> (the <c>by</c> of the request).</summary>
    public TouristTaxAccountPeriod By { get; init; }

    /// <summary>Always EUR, as in the declaration.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>The comuni, by name, each with its periods in order and, last, the line of the year (<c>isYear</c>).</summary>
    public IReadOnlyList<TouristTaxAgentAccountRowDto> Rows { get; init; } = [];

    /// <summary>Every payment of the year, a payment that spans two periods included, by comune and period.</summary>
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

/// <summary>
/// One comune in one period of the account of the agent, or in the whole year: what the stays record as owed to the comune in it (the tax the agent collects), what was remitted for it and the difference. The figures are those
/// of the tourist tax summary of the period.
/// </summary>
public sealed class TouristTaxAgentAccountRowDto
{
    public string Comune { get; init; } = string.Empty;

    /// <summary>The label of the period: <c>2026-03</c>, <c>2026-Q1</c>, or the year (<c>2026</c>) for the line of the year.</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary>First day of the period, <c>yyyy-MM-dd</c>.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last day of the period, included.</summary>
    public DateOnly To { get; init; }

    /// <summary>True for the line of the year.</summary>
    public bool IsYear { get; init; }

    public int Stays { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    /// <summary>The tax the stays record as owed to the comune in the period.</summary>
    public decimal Collected { get; init; }

    /// <summary>Stays with no amount: counted apart, never taken as a tax of zero. Above zero, <see cref="Collected"/> may be incomplete.</summary>
    public int StaysWithoutAmount { get; init; }

    /// <summary>The payments whose declared period lies inside the period.</summary>
    public decimal Remitted { get; init; }

    /// <summary><see cref="Collected"/> less <see cref="Remitted"/>.</summary>
    public decimal Balance { get; init; }

    // Not named From: the first day of the period already is.
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

/// <summary>
/// The yearly count of the nights of the properties in Ticino for the regional tourist organization as a screen reads it (<c>GET /api/tourist-tax/year-end/{year}/ticino?format=json</c>): the typed result of
/// <see cref="ITouristTaxSummaryService.GetTicinoYearlyCountAsync"/>, field for field, plus <see cref="CantonUnknownCount"/>.
/// </summary>
public sealed class TicinoYearlyCountDto
{
    public int Year { get; init; }

    /// <summary>The day the count is due: 5 January of the next year, <c>yyyy-MM-dd</c>.</summary>
    public DateOnly DueDate { get; init; }

    /// <summary>
    /// How many of the <see cref="Properties"/> are in Switzerland with a canton that is not known (<c>cantonUnknown</c> on their row): they may be in Ticino, so they are in the count and flagged, never dropped in
    /// silence. The screen says it without counting.
    /// </summary>
    public int CantonUnknownCount { get; init; }

    /// <summary>The properties in the canton of Ticino and those whose canton is not known, by name.</summary>
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

/// <summary>
/// The count of the nights of one property in Ticino: the stays with check-in in the year, those whose document of the stay carries a tourist tax (taxed), those whose document has none (untaxed) and those with no
/// document issued yet (told apart, never counted as untaxed); <see cref="TouristTax"/> is the tourist tax of the issued documents of those stays, credit notes taken away, in <see cref="Currency"/>.
/// </summary>
public sealed class TicinoPropertyCountDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    /// <summary>The Swiss municipality code stored on the property; <c>null</c> when there is none.</summary>
    public string? MunicipalityCode { get; init; }

    /// <summary>The currency of the property and of <see cref="TouristTax"/>: CHF for a house in Switzerland.</summary>
    public string Currency { get; init; } = string.Empty;

    public int Stays { get; init; }

    public int Nights { get; init; }

    public int TaxedStays { get; init; }

    public int TaxedNights { get; init; }

    public int UntaxedStays { get; init; }

    public int UntaxedNights { get; init; }

    public int StaysWithoutDocument { get; init; }

    public int NightsWithoutDocument { get; init; }

    /// <summary>The amount of the documents in <see cref="Currency"/>, never a calculation.</summary>
    public decimal TouristTax { get; init; }

    /// <summary>
    /// True for a property in Switzerland whose canton is not known (no Swiss municipality on it, or one the registry of the municipalities does not have): it may be in Ticino, so it is counted and flagged.
    /// </summary>
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
