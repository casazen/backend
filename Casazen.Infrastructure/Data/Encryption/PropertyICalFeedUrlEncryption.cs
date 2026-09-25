using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// Encryption at rest of <see cref="PropertyICalFeed.ImportUrl"/> (PC-11, A2-20): one of the columns of
/// <see cref="EncryptedColumns"/> (Data Protection value converter of <see cref="AppDbContext"/>), with its own purpose.
/// URLs stored in clear before PC-11 are rewritten encrypted at startup by
/// <see cref="EncryptedColumns.EncryptLegacyPlaintextAsync"/>.
/// </summary>
public static class PropertyICalFeedUrlEncryption
{
    /// <summary>Data Protection purpose of the import URLs (never change it: stored values could not be read).</summary>
    public const string Purpose = "Casazen.PropertyICalFeed.ImportUrl";

    /// <summary>
    /// True for a URL stored in clear before PC-11. A Data Protection payload is base64url (it starts with
    /// <c>CfDJ8</c>) and never contains <c>://</c>.
    /// </summary>
    public static bool IsLegacyPlaintext(string stored) =>
        stored.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || stored.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
