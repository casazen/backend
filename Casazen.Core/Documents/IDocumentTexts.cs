using System.Globalization;

namespace Casazen.Core.Documents;

/// <summary>
/// The labels of the documents CasaZen generates for the fiscal chain (owner statements, F24 data, fiscal documents of a booking), read from the
/// resource files (Italian default, English) in the language the caller names. The builders of the documents live where the resource files
/// are not visible: the web project implements this over <c>SharedResources</c>, and a test can implement it with the keys.
/// </summary>
public interface IDocumentTexts
{
    /// <summary>
    /// The text of <paramref name="key"/> in <paramref name="language"/> (<c>it</c> or <c>en</c>, anything else is Italian), with
    /// <paramref name="args"/> put in its placeholders. The key itself when the resource files have no such key: a missing label is visible in the
    /// document and never an exception that fails the download.
    /// </summary>
    string Get(string key, string language, params object[] args);
}

public static class DocumentLanguages
{
    public const string Italian = "it";
    public const string English = "en";

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    public static string Normalize(string? language) =>
        language is { Length: >= 2 } && language.StartsWith(English, StringComparison.OrdinalIgnoreCase) ? English : Italian;

    public static CultureInfo CultureOf(string? language) => Normalize(language) == English ? EnglishCulture : ItalianCulture;

    public static string Current => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public static string Money(decimal amount, string? language) => amount.ToString("N2", CultureOf(language));

    public static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
