using Casazen.Core.Regulatory;

namespace Casazen.Core.Options;

/// <summary>
/// The notification of the guests to the police outside Italy (casazen/backend#979, NF-126), bound from the section <c>GuestNotifications</c> and
/// validated at startup: a value that is not one of the accepted ones stops the application with a clear message, never a file in a character set
/// nobody chose. Runbook: <c>docs/runbooks/guest-notification-switzerland.md</c>.
/// </summary>
public sealed class GuestNotificationOptions
{
    /// <summary>The section the options are read from.</summary>
    public const string SectionName = "GuestNotifications";

    /// <summary>The value of <see cref="TicinoCsvEncoding"/> for Windows-1252, the single-byte set of the official model (the default).</summary>
    public const string Windows1252 = "windows-1252";

    /// <summary>The value of <see cref="TicinoCsvEncoding"/> for UTF-8 (without byte order mark).</summary>
    public const string Utf8 = "utf-8";

    /// <summary>
    /// The character set of the CSV for the Ticino police portal (<c>GuestNotifications:TicinoCsvEncoding</c>): <c>windows-1252</c> (default: the
    /// official model <c>D-Modello_2.CSV</c> is single-byte, decision D8 ⚖ to confirm with the police) or <c>utf-8</c>, case-insensitive. A character the
    /// set cannot hold loses its accent, or becomes <c>?</c> as the last resort, and the day says how many did.
    /// </summary>
    public string TicinoCsvEncoding { get; set; } = Windows1252;

    /// <summary>The character set <paramref name="value"/> names; false when it is neither <c>windows-1252</c> nor <c>utf-8</c> (blank included).</summary>
    public static bool TryParseTicinoCsvEncoding(string? value, out TicinoCsvCharset charset)
    {
        charset = TicinoCsvCharset.Windows1252;
        switch (value?.Trim().ToLowerInvariant())
        {
            case Windows1252:
                return true;
            case Utf8:
                charset = TicinoCsvCharset.Utf8;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The character set of the CSV: the configured one, Windows-1252 when it is not valid (the startup validation stops that case first).</summary>
    public TicinoCsvCharset ResolveTicinoCsvCharset() =>
        TryParseTicinoCsvEncoding(TicinoCsvEncoding, out var charset) ? charset : TicinoCsvCharset.Windows1252;
}
