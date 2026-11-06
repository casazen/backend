namespace Casazen.Core.Services;

/// <summary>
/// The words of the marketplace that live in the resource files (Italian default, English), read in the language the caller asks for and not
/// in the language of the request (E09/F01/US01/T03: the catalog is asked with <c>lang</c>). The services do not know the resource files:
/// the web project implements this over <c>SharedResources</c>, and a test can implement it with the keys.
/// </summary>
public interface IMarketplaceTexts
{
    /// <summary>
    /// The text of <paramref name="key"/> in <paramref name="language"/> (<c>it</c> or <c>en</c>, anything else is Italian), with
    /// <paramref name="args"/> put in its placeholders (<c>{0}</c>). The key itself when the resource files have no such key: a missing text is
    /// visible, and never an exception that takes the page down.
    /// </summary>
    string Get(string key, string language, params object[] args);
}
