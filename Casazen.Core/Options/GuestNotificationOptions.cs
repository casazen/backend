using Casazen.Core.Regulatory;

namespace Casazen.Core.Options;

/// <summary>
/// The notification of the guests to the police outside Italy (casazen/backend#979, NF-126), bound from the section <c>GuestNotifications</c> and
/// validated at startup: a value that is not one of the accepted ones stops the application with a clear message, never a file in a character set
/// nobody chose. Runbook: <c>docs/runbooks/guest-notification-switzerland.md</c>.
/// </summary>
public sealed class GuestNotificationOptions
{
    public const string SectionName = "GuestNotifications";

    public const string Windows1252 = "windows-1252";

    public const string Utf8 = "utf-8";

    public string TicinoCsvEncoding { get; set; } = Windows1252;

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

    public TicinoCsvCharset ResolveTicinoCsvCharset() =>
        TryParseTicinoCsvEncoding(TicinoCsvEncoding, out var charset) ? charset : TicinoCsvCharset.Windows1252;
}
