using System.Globalization;

namespace Casazen.Core.Services;

/// <summary>
/// The numbers of the fiscal documents (NF-83, ADR-019-R4, E06/F01/US02/T02): consecutive from the first, without gaps and without duplicates per issuer
/// (org) and series. The documents of the guests and the owner invoices (E14/F01/US01/T01, their own series) draw from it; owner statements are not fiscal
/// documents and never do.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The caller's transaction.</b> The caller opens a READ COMMITTED transaction that also saves the document; the reservation joins it under a
/// transaction-scoped advisory lock of the org and series. A failure rolls the number back with the document: the next call gets the same number.</item>
/// <item><b>Seed.</b> At the first use of a series the counter starts above the highest fiscal number already used in that org and series, the legacy
/// agency invoices included, only so that no number is used twice.</item>
/// </list>
/// </remarks>
public interface IFiscalNumberingService
{
    /// <summary>
    /// The next number of the series, reserved in the caller's transaction. A series longer than <see cref="FiscalNumberFormat.SeriesMaxLength"/> is
    /// 422 <c>series_too_long</c>.
    /// </summary>
    Task<FiscalNumber> ReserveNextAsync(Guid orgId, string series, CancellationToken ct = default);
}

/// <summary>A reserved fiscal number: the series, the integer and its display form.</summary>
public sealed record FiscalNumber(string Series, int Number, string Display);

/// <summary>The one place where a fiscal number is written and a default series is made.</summary>
public static class FiscalNumberFormat
{
    /// <summary>The longest series: the display number must fit the 20 characters of <c>Numero</c> in FatturaPA.</summary>
    public const int SeriesMaxLength = 10;

    /// <summary>The longest display number (FatturaPA <c>Numero</c>).</summary>
    public const int DisplayMaxLength = 20;

    /// <summary>The placeholder of the year of the issue in a series pattern.</summary>
    public const string YearPlaceholder = "{year}";

    /// <summary>The default pattern of a series: the year of the issue.</summary>
    public const string DefaultPattern = YearPlaceholder;

    /// <summary>The display form of a number: <c>{series}/{number}</c>.</summary>
    public static string Display(string series, int number) => $"{series}/{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The series of a document issued on <paramref name="issueDate"/> (a Europe/Rome calendar day): <paramref name="pattern"/> with
    /// <c>{year}</c> replaced by its year; the default pattern when it is empty.
    /// </summary>
    public static string SeriesFor(string? pattern, DateOnly issueDate)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        return effective.Replace(YearPlaceholder, issueDate.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}

/// <summary>The stable codes of the errors of the numbering, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class FiscalNumberingErrors
{
    /// <summary>422: the series is longer than <see cref="FiscalNumberFormat.SeriesMaxLength"/> (or empty).</summary>
    public const string SeriesTooLong = "series_too_long";
}
