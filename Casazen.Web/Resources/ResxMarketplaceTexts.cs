using System.Globalization;
using System.Resources;
using Casazen.Core.Services;

namespace Casazen.Web.Resources;

/// <summary>
/// <see cref="IMarketplaceTexts"/> over <c>SharedResources.resx</c> (Italian, the default) and <c>SharedResources.en.resx</c> (English): the
/// text of a key in the language the caller names, whatever the language of the request, because the marketplace is asked with <c>lang</c>.
/// English falls back to the Italian text of the same key if a key has no English one; a key that exists in neither is returned as it is.
/// </summary>
public sealed class ResxMarketplaceTexts : IMarketplaceTexts
{
    private static readonly ResourceManager Manager = new(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    public string Get(string key, string language, params object[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var culture = MarketplaceLanguages.Normalize(language) == MarketplaceLanguages.English ? EnglishCulture : ItalianCulture;
        var template = Manager.GetString(key, culture);
        if (template is null)
            return key;

        return args is { Length: > 0 } ? string.Format(culture, template, args) : template;
    }
}
