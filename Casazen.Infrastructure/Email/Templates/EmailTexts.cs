using System.Globalization;
using System.Resources;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Localized texts of the emails: <c>EmailTexts.resx</c> (Italian, default) and <c>EmailTexts.en.resx</c> (English).
/// Every key must exist in both files. Values are trusted markup (e.g. <c>&lt;strong&gt;{0}&lt;/strong&gt;</c>):
/// the <c>{n}</c> placeholders are filled by <see cref="EmailHtmlBuilder"/> with HTML-encoded values only.
/// <c>EmailTexts.de.resx</c>, <c>.fr.resx</c> and <c>.es.resx</c> hold the texts of the emails to a guest and nothing else (E50/F01/US03/T02, NF-155): every
/// key of a guest template is in all five files (<c>EmailTextsGuestParityTests</c>), and any other key falls back to the Italian text, which a guest never reads.
/// </summary>
public static class EmailTexts
{
    public const string ResourceName = "Casazen.Infrastructure.Email.Templates.EmailTexts";

    public static ResourceManager Resources { get; } = new(ResourceName, typeof(EmailTexts).Assembly);

    public static string Get(string key, CultureInfo culture) =>
        Resources.GetString(key, culture)
        ?? throw new MissingManifestResourceException($"Email text '{key}' is missing from {ResourceName}.");
}
