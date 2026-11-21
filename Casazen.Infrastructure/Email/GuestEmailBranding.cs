using System.Globalization;
using System.Text;
using Casazen.Core.Features;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// The brand of a host in the e-mails to its guests (E50/F01/US03/T01, NF-155, D3, section 5.10 of the specification): the name the e-mail is sent in, the logo and
/// the primary color of the showcase, and the address the guest's answer goes to. Used only by the templates of the guest (<see cref="Templates.EmailHtmlBuilder"/>
/// with a brand); the e-mails of the host, of the staff and of the suppliers keep the CasaZen layout. Every member is optional: a value that is missing or not safe
/// is dropped (<see cref="Cleaned"/>), the rest of the brand stays.
/// </summary>
/// <remarks>
/// <b>Nothing in it can inject anything.</b> The values come from a host's settings, so they are cleaned before they are used, and cleaned again by the builder
/// (<see cref="Templates.EmailHtmlBuilder"/> and <see cref="EmailContent"/> never read a raw member): the name loses every control and format character (a line break,
/// a right-to-left override), quotes, brackets and the separators of a header, and is at most <see cref="NameMaxLength"/> characters; the logo must be an absolute
/// <c>https</c> address with no credentials; the color must be exactly <c>#rrggbb</c>; the reply-to must be one plain, valid address. Whatever is left is HTML-encoded by
/// the builder anyway.
/// </remarks>
/// <param name="Name">The name of the host as the guest knows it: the sender's display name and the title of the header. <c>null</c> = none.</param>
/// <param name="LogoUrl">Absolute <c>https</c> address of the logo, or <c>null</c>.</param>
/// <param name="PrimaryColor"><c>#rrggbb</c> of the showcase, or <c>null</c> (the CasaZen blue).</param>
/// <param name="ReplyTo">The address of the host a guest's answer goes to (<c>Org.ContactEmail</c>), or <c>null</c>.</param>
public sealed record GuestEmailBranding(string? Name, string? LogoUrl, string? PrimaryColor, string? ReplyTo)
{
    /// <summary>The longest sender name that is used: the display name of an e-mail is kept short, so no client cuts it in the middle of a word.</summary>
    public const int NameMaxLength = 64;

    /// <summary>The longest logo address that is used (the column of <c>Org.LogoUrl</c> holds 1000).</summary>
    public const int LogoUrlMaxLength = 1000;

    /// <summary>The color of the buttons and of the header of every other e-mail of CasaZen, used when the host has no usable color.</summary>
    public const string DefaultAccent = "#0d8abc";

    /// <summary>The color of the text on <see cref="DefaultAccent"/> (white, as the buttons of every e-mail have always been).</summary>
    public const string DefaultAccentText = "#ffffff";

    private const string White = "#ffffff";
    private const string Black = "#000000";

    /// <summary>The brand with only what is safe left: see the remarks of the type. Cleaning it again changes nothing.</summary>
    public GuestEmailBranding Cleaned() => new(CleanName(Name), CleanLogoUrl(LogoUrl), CleanColor(PrimaryColor), CleanReplyTo(ReplyTo));

    /// <summary>True when there is nothing to show or to send in the name of the host (every member is missing or was dropped).</summary>
    public bool IsEmpty => Name is null && LogoUrl is null && PrimaryColor is null && ReplyTo is null;

    /// <summary>The color of the buttons and of the header of a cleaned brand: its own, or <see cref="DefaultAccent"/>.</summary>
    public string Accent => PrimaryColor ?? DefaultAccent;

    /// <summary>The color of the text on <see cref="Accent"/>: white on the CasaZen blue (as ever), otherwise the one of white and black that reads better on the color.</summary>
    public string AccentText => PrimaryColor is null ? DefaultAccentText : ReadableTextOn(PrimaryColor);

    /// <summary>
    /// A display name that is safe to put in the <c>From</c> header and in the page: the control and format characters of any kind (a line break, a tab, a
    /// right-to-left override, a zero-width joiner) become a space, the quotes, the brackets and the separators of an address (<c>" “ ” « » &lt; &gt; [ ] ( ) { } \ , ; : @</c>)
    /// are dropped, the spaces are collapsed and the result has at most <see cref="NameMaxLength"/> characters. <c>null</c> when nothing is left.
    /// </summary>
    public static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    builder.Append(' ');
                    continue;
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.Surrogate:
                    continue;
            }

            if (IsDroppedInName(rune))
                continue;

            builder.Append(rune.ToString());
        }

        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > NameMaxLength)
        {
            // Never in the middle of a surrogate pair.
            var length = char.IsHighSurrogate(name[NameMaxLength - 1]) ? NameMaxLength - 1 : NameMaxLength;
            name = name[..length].TrimEnd();
        }

        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// A logo address that is safe to put in an <c>img</c>: absolute, <c>https</c> only, with a host and no credentials, without a space, a control character, a quote or an angle
    /// bracket, at most <see cref="LogoUrlMaxLength"/> characters, written the way <see cref="Uri.AbsoluteUri"/> writes it. <c>null</c> for anything else (a <c>javascript:</c> or
    /// <c>data:</c> address, plain <c>http</c>, a relative path).
    /// </summary>
    public static string? CleanLogoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var url = value.Trim();
        if (url.Length > LogoUrlMaxLength)
            return null;

        foreach (var c in url)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '\\' or '`')
                return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <summary>The color when it is exactly <c>#</c> and six hexadecimal digits (written in lower case), otherwise <c>null</c>: no <c>#rgb</c>, no name, no function, nothing after it.</summary>
    public static string? CleanColor(string? value)
    {
        var color = value?.Trim();
        if (color is null || color.Length != 7 || color[0] != '#')
            return null;

        for (var i = 1; i < color.Length; i++)
        {
            if (!char.IsAsciiHexDigit(color[i]))
                return null;
        }

        return color.ToLowerInvariant();
    }

    /// <summary>The address when it is one plain, valid e-mail address (no name, no list, no space, no control character), otherwise <c>null</c>.</summary>
    public static string? CleanReplyTo(string? value)
    {
        var address = value?.Trim();
        if (string.IsNullOrEmpty(address) || address.Length > 254)
            return null;

        foreach (var c in address)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';' or '"')
                return null;
        }

        return EmailOptions.IsValidAddress(address) ? address : null;
    }

    /// <summary>
    /// The color of the text on <paramref name="background"/> (<c>#rrggbb</c>): white or black, whichever has the better contrast (WCAG relative luminance). One of them is
    /// always readable (at least 4.5 to 1). A value that is not <c>#rrggbb</c> gets white.
    /// </summary>
    public static string ReadableTextOn(string background)
    {
        if (CleanColor(background) is not { } color)
            return White;

        var luminance = 0.2126 * Linear(color, 1) + 0.7152 * Linear(color, 3) + 0.0722 * Linear(color, 5);
        var againstWhite = 1.05 / (luminance + 0.05);
        var againstBlack = (luminance + 0.05) / 0.05;
        return againstWhite >= againstBlack ? White : Black;
    }

    /// <summary>The channel of a <c>#rrggbb</c> color that starts at <paramref name="start"/>, linearized as sRGB is.</summary>
    private static double Linear(string color, int start)
    {
        var channel = int.Parse(color.AsSpan(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) / 255.0;
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static bool IsDroppedInName(Rune rune) =>
        rune.Value is '"' or '“' or '”' or '„' or '‟' or '«' or '»' or '<' or '>' or '[' or ']' or '(' or ')' or '{' or '}' or '\\' or ',' or ';' or ':' or '@';
}

/// <summary>
/// Gives the brand a host's guest e-mails carry (E50/F01/US03/T01). Used by every service that sends an e-mail to a guest, which takes it as an optional
/// dependency: without it, or with <c>GuestPortal</c> off, the e-mail is the one CasaZen has always sent.
/// </summary>
public interface IGuestEmailBrandingProvider
{
    /// <summary>
    /// The brand of the org's guest e-mails, or <c>null</c> while <c>Features:GuestPortal</c> is off, when the org is not there, when nothing of it is usable and when
    /// it cannot be read (logged): the e-mail is then the CasaZen one, never lost for it. Never throws except for a cancellation.
    /// </summary>
    Task<GuestEmailBranding?> ForOrgAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGuestEmailBrandingProvider"/>
/// <remarks>
/// The values are the ones the public profile of the org shows (<c>GET api/public/orgs/{slug}</c>, <c>PublicOrgDto</c> and <c>OrgBrandingDto</c>): the logo, the primary color
/// (<c>Org.ThemeColor</c>) and the name, which is how the host is named on the site (<c>Org.HostName</c>), else the name of the account shown on the site
/// (<c>Org.DisplayName</c>), else <c>Org.Name</c>. The reply-to is <c>Org.ContactEmail</c> when it is a valid address: the guests already see it in the confirmation. Each is cleaned
/// on its own (<see cref="GuestEmailBranding"/>), so a hostile name falls back to the next one and a hostile logo or color leaves only the name and the default color. The logs carry the
/// id of the org only.
/// </remarks>
public sealed class GuestEmailBrandingProvider(
    AppDbContext db,
    IFeatureFlags featureFlags,
    ILogger<GuestEmailBrandingProvider> logger) : IGuestEmailBrandingProvider
{
    public async Task<GuestEmailBranding?> ForOrgAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.GuestPortal))
            return null;

        try
        {
            var org = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => new { o.HostName, o.DisplayName, o.Name, o.LogoUrl, o.ThemeColor, o.ContactEmail })
                .FirstOrDefaultAsync(cancellationToken);
            if (org is null)
                return null;

            var branding = new GuestEmailBranding(
                GuestEmailBranding.CleanName(org.HostName)
                    ?? GuestEmailBranding.CleanName(org.DisplayName)
                    ?? GuestEmailBranding.CleanName(org.Name),
                GuestEmailBranding.CleanLogoUrl(org.LogoUrl),
                GuestEmailBranding.CleanColor(org.ThemeColor),
                GuestEmailBranding.CleanReplyTo(org.ContactEmail));
            return branding.IsEmpty ? null : branding;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The brand of the guest e-mails of org {OrgId} could not be read: they go out as CasaZen e-mails", orgId);
            return null;
        }
    }
}

/// <summary>The one way the services read the brand when it is an optional dependency of theirs.</summary>
public static class GuestEmailBrandingProviderExtensions
{
    /// <summary>The brand of the org's guest e-mails, or <c>null</c> when the service was built without a provider (an older test, a tool): the CasaZen e-mail.</summary>
    public static async Task<GuestEmailBranding?> ForOrgOrNullAsync(
        this IGuestEmailBrandingProvider? provider,
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
            return null;

        return await provider.ForOrgAsync(orgId, cancellationToken);
    }
}
