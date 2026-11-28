using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public enum TouristTaxAccountPeriod
{
    Month = 1,

    Quarter = 2,
}

/// <summary>
/// Why a stay has no tourist tax amount in the declaration (E18/F01/US02/T03): the codes are part of the file, never rename one. A stay is never taken for a zero because it has none: it is counted
/// apart with its reason, so the declaration says what it knows and what it does not.
/// </summary>
public static class TouristTaxStayReasons
{
    /// <summary>The tax the host confirmed guest by guest is nothing because every guest is exempt (the reasons are in the exemptions).</summary>
    public const string Exempt = "exempt";

    public const string CollectedByChannel = "collected_by_channel";

    public const string NotCalculated = "not_calculated";

    public const string NoTaxDue = "no_tax_due";
}

public static class TouristTaxExemptionKinds
{
    /// <summary>The reason was erased with the data of the guest (art. 9 GDPR): the guest was exempt, the reason is no longer known.</summary>
    public const string Erased = "erased";

    /// <summary>
    /// The reason is not shown because the caller does not hold <c>guest.read</c>: the reason of an exemption can reveal a health condition (art. 9 GDPR), so, as on the tax of a stay,
    /// only the kind of the exemption stays visible (the age is <c>minor_age</c>) and every other exempt guest is counted under this word.
    /// </summary>
    public const string Restricted = "restricted";
}

public sealed record TouristTaxReasonCount(string Reason, int Stays, int Guests, int GuestNights);

/// <summary>
/// One comune of the declaration of the year (E18/F01/US02/T03): the summary of the comune as the tourist tax summary gives it (stays, nights, guests, owed, paid, difference, status and the
/// payments with their days and references: nothing recomputed), the stays that have a tax, the stays that have none with the reason, and the exemptions by reason.
/// </summary>
/// <param name="Summary">The comune in the year: <see cref="ComuneTaxSummary.Balance"/> is what is owed less what was remitted, to the cent.</param>
/// <param name="TaxedStays">Stays with a recorded amount above zero.</param>
/// <param name="StaysWithoutAmount">The stays with no amount by reason (<see cref="TouristTaxStayReasons"/>): their sum is <see cref="ComuneTaxSummary.StaysWithoutAmount"/>.</param>
/// <param name="Exemptions">The exempt guests by reason (a code of <see cref="TouristTaxExemptionReason"/>, <c>minor_age</c> for the age, <see cref="TouristTaxExemptionKinds.Erased"/>): a count, never a guest.</param>
public sealed record TouristTaxDeclarationComune(
    ComuneTaxSummary Summary,
    int TaxedStays,
    IReadOnlyList<TouristTaxReasonCount> StaysWithoutAmount,
    IReadOnlyList<TouristTaxReasonCount> Exemptions);

public sealed record TouristTaxDeclaration(int Year, IReadOnlyList<TouristTaxDeclarationComune> Comuni, TouristTaxSummaryTotals Total);

public sealed record TouristTaxAgentAccountRow(
    string Comune,
    string Period,
    DateOnly From,
    DateOnly To,
    bool IsYear,
    int Stays,
    int Nights,
    int Guests,
    decimal Collected,
    int StaysWithoutAmount,
    decimal Remitted,
    decimal Balance);

public sealed record TouristTaxAgentAccount(
    int Year,
    TouristTaxAccountPeriod By,
    IReadOnlyList<TouristTaxAgentAccountRow> Rows,
    IReadOnlyList<TouristTaxRemittanceLine> Remittances,
    TouristTaxSummaryTotals Total);

/// <summary>
/// The yearly count of the nights of one property in Ticino for the regional tourist organization (E18/F01/US02/T03, acceptance step D3): the stays with check-in in the year, their nights, those
/// whose document of the stay carries a tourist tax (<see cref="TaxedStays"/>), those whose document has none (a portal that collects it, an exemption, no rule: <see cref="UntaxedStays"/>) and
/// those with no document issued yet (<see cref="StaysWithoutDocument"/>: told apart, never counted as untaxed). <see cref="TouristTax"/> is the tourist tax lines of the issued documents of those stays,
/// credit notes taken away, in <see cref="Currency"/>: the amount of the documents, never a calculation. <see cref="CantonUnknown"/> marks a property in Switzerland whose canton is not known (no
/// Swiss municipality stored on the property nor derivable from its postal code and town, or a postal code that leaves several municipalities open): it may be in Ticino, so it is counted and
/// the file says so, never dropped in silence. <see cref="MunicipalityCode"/> is the municipality the one resolver of the place gives (E29/F01/US01/T02): the stored one, or the one derived from
/// the postal code and the town, then <see cref="MunicipalityDerived"/> is true and the file says it is to be confirmed.
/// </summary>
public sealed record TicinoPropertyCount(
    Guid PropertyId,
    string PropertyName,
    string Address,
    string City,
    string? MunicipalityCode,
    string Currency,
    int Stays,
    int Nights,
    int TaxedStays,
    int TaxedNights,
    int UntaxedStays,
    int UntaxedNights,
    int StaysWithoutDocument,
    int NightsWithoutDocument,
    decimal TouristTax,
    bool CantonUnknown = false,
    bool MunicipalityDerived = false);

public sealed record TicinoYearlyCount(int Year, DateOnly DueDate, IReadOnlyList<TicinoPropertyCount> Properties);

public sealed record TouristTaxGuestFact(TouristTaxExemptionKind Kind, string? Code);

public sealed record TouristTaxStayFacts(
    bool Confirmed,
    TouristTaxQuoteStatus Status,
    TouristTaxCollector? Collector,
    TouristTaxCollector? DeclaredCollector,
    IReadOnlyList<TouristTaxGuestFact> Guests);

public static class TouristTaxYearEndRules
{
    public static DateOnly TicinoCountDue(int year) => new(year + 1, 1, 5);

    public static string NoAmountReasonOf(TouristTaxStayFacts? facts)
    {
        if (facts is null)
            return TouristTaxStayReasons.NotCalculated;

        var collector = facts.Confirmed ? facts.Collector ?? facts.DeclaredCollector : facts.DeclaredCollector;
        if (collector == TouristTaxCollector.Channel)
            return TouristTaxStayReasons.CollectedByChannel;

        if (!facts.Confirmed || facts.Status != TouristTaxQuoteStatus.Calculated)
            return TouristTaxStayReasons.NotCalculated;

        return facts.Guests.Count > 0 && facts.Guests.All(g => g.Kind != TouristTaxExemptionKind.None)
            ? TouristTaxStayReasons.Exempt
            : TouristTaxStayReasons.NoTaxDue;
    }

    /// <summary>
    /// The reason an exempt guest is counted under: <c>minor_age</c> for the exemption the calculator applies from the age, the code the host chose from the catalogue, and
    /// <see cref="TouristTaxExemptionKinds.Erased"/> once the reason was erased with the data of the guest. <c>null</c> for a guest who pays. Without <paramref name="withReasons"/> (the caller has no
    /// <c>guest.read</c>: the reason can reveal a health condition, art. 9 GDPR) only the age is told apart and every other exempt guest is <see cref="TouristTaxExemptionKinds.Restricted"/>.
    /// </summary>
    public static string? ExemptionReasonOf(TouristTaxGuestFact guest, bool withReasons = true)
    {
        ArgumentNullException.ThrowIfNull(guest);

        return guest.Kind switch
        {
            TouristTaxExemptionKind.None => null,
            TouristTaxExemptionKind.Age => TouristTaxExemptionReason.MinorAgeCode,
            _ when !withReasons => TouristTaxExemptionKinds.Restricted,
            _ => string.IsNullOrWhiteSpace(guest.Code) ? TouristTaxExemptionKinds.Erased : guest.Code,
        };
    }

    public static IReadOnlyList<(string Label, DateOnly From, DateOnly To)> PeriodsOf(int year, TouristTaxAccountPeriod by)
    {
        var step = by == TouristTaxAccountPeriod.Quarter ? 3 : 1;
        var periods = new List<(string Label, DateOnly From, DateOnly To)>();
        for (var month = 1; month <= 12; month += step)
        {
            var from = new DateOnly(year, month, 1);
            var to = from.AddMonths(step).AddDays(-1);
            var label = by == TouristTaxAccountPeriod.Quarter
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{year:0000}-Q{((month - 1) / 3) + 1}")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{year:0000}-{month:00}");
            periods.Add((label, from, to));
        }

        return periods;
    }
}
