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

/// <summary>The two languages of the generated documents and how their numbers and dates are written.</summary>
public static class DocumentLanguages
{
    public const string Italian = "it";
    public const string English = "en";

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary><c>en</c> for any English tag (<c>en</c>, <c>en-GB</c>...), <c>it</c> for everything else.</summary>
    public static string Normalize(string? language) =>
        language is { Length: >= 2 } && language.StartsWith(English, StringComparison.OrdinalIgnoreCase) ? English : Italian;

    /// <summary>The culture the amounts of a document in <paramref name="language"/> are written in.</summary>
    public static CultureInfo CultureOf(string? language) => Normalize(language) == English ? EnglishCulture : ItalianCulture;

    /// <summary>The language of the request in progress (the UI culture set by the request localization), normalized.</summary>
    public static string Current => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>An amount to two places with the thousands separator of the language; the currency is written by the caller, from the data.</summary>
    public static string Money(decimal amount, string? language) => amount.ToString("N2", CultureOf(language));

    /// <summary>A calendar day as <c>dd/MM/yyyy</c>.</summary>
    public static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
