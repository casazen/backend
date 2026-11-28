using System.Globalization;

namespace Casazen.Core.Services;

/// <summary>
/// The numbers of the fiscal documents (NF-83, NF-116, ADR-019-R4, E06/F01/US02/T02, E17/F01/US01/T01): consecutive from the first, without gaps and without
/// duplicates per org, issuer and series. The issuer is the profile the document is issued on: the organization, or an owner who issues the documents of its
/// stays with its own series (the sezionale). The documents of the guests and the owner invoices (E14/F01/US01/T01, their own series) draw from it; owner
/// statements are not fiscal documents and never do.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The caller's transaction.</b> The caller opens a READ COMMITTED transaction that also saves the document; the reservation joins it under a
/// transaction-scoped advisory lock of the org, the issuer and the series (<c>{orgId}:{issuerProfileId}:{series}</c>). A failure rolls the number back with
/// the document: the next call gets the same number.</item>
/// <item><b>Seed.</b> At the first use of a series the counter starts above the highest fiscal number the issuer already used in it (for the profile of the
/// organization, the documents of before the issuers and the legacy agency invoices included), only so that no number is used twice.</item>
/// </list>
/// </remarks>
public interface IFiscalNumberingService
{
    Task<FiscalNumber> ReserveNextAsync(Guid orgId, Guid issuerProfileId, string series, CancellationToken ct = default);
}

public sealed record FiscalNumber(string Series, int Number, string Display);

public static class FiscalNumberFormat
{
    /// <summary>The longest series: the display number must fit the 20 characters of <c>Numero</c> in FatturaPA.</summary>
    public const int SeriesMaxLength = 10;

    public const int DisplayMaxLength = 20;

    public const string YearPlaceholder = "{year}";

    public const string DefaultPattern = YearPlaceholder;

    public static string Display(string series, int number) => $"{series}/{number.ToString(CultureInfo.InvariantCulture)}";

    public static string SeriesFor(string? pattern, DateOnly issueDate)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        return effective.Replace(YearPlaceholder, issueDate.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}

public static class FiscalNumberingErrors
{
    public const string SeriesTooLong = "series_too_long";
}
