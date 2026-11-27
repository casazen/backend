namespace Casazen.Core.Services;

public interface IMarketplaceTexts
{
    /// <summary>
    /// The text of <paramref name="key"/> in <paramref name="language"/> (<c>it</c> or <c>en</c>, anything else is Italian), with
    /// <paramref name="args"/> put in its placeholders (<c>{0}</c>). The key itself when the resource files have no such key: a missing text is
    /// visible, and never an exception that takes the page down.
    /// </summary>
    string Get(string key, string language, params object[] args);
}
