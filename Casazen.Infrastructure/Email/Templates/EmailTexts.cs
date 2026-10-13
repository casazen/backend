using System.Globalization;
using System.Resources;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Localized texts of the emails: <c>EmailTexts.resx</c> (Italian, default) and <c>EmailTexts.en.resx</c> (English).
/// Every key must exist in both files. Values are trusted markup (e.g. <c>&lt;strong&gt;{0}&lt;/strong&gt;</c>):
/// the <c>{n}</c> placeholders are filled by <see cref="EmailHtmlBuilder"/> with HTML-encoded values only.
/// </summary>
public static class EmailTexts
{
    public const string ResourceName = "Casazen.Infrastructure.Email.Templates.EmailTexts";

    public static ResourceManager Resources { get; } = new(ResourceName, typeof(EmailTexts).Assembly);

    public static string Get(string key, CultureInfo culture) =>
        Resources.GetString(key, culture)
        ?? throw new MissingManifestResourceException($"Email text '{key}' is missing from {ResourceName}.");
}
