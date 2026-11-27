using System.Security.Cryptography;
using Casazen.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The signed link of the document sent to the guest (E12/F01/US02/T01, NF-83): a token that names one document and stops opening at the end of its life
/// (<see cref="FiscalDocumentLinkRules.Lifetime"/>). It is encrypted and authenticated by ASP.NET Core Data Protection (the key ring is in the database, so a link outlives a redeploy), under a purpose of
/// its own: nobody can make one for another document, extend it, or reuse a token made for something else. Nothing is stored for it, and it carries nothing but the document id (no name, no address,
/// no amount). What the link opens (the summary and the PDF of an issued guest document) is judged by the delivery service at every read, never by the token alone.
/// </summary>
public sealed class FiscalDocumentLinkTokens
{
    /// <summary>The Data Protection purpose: a token made for another purpose does not open here.</summary>
    public const string Purpose = FiscalDocumentLinkRules.TokenPurpose;

    /// <summary>The longest text taken for a token: a real one is a few hundred characters, anything longer is not one.</summary>
    private const int MaxTokenLength = 1024;

    private readonly ITimeLimitedDataProtector _protector;

    public FiscalDocumentLinkTokens(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    /// <summary>The token of <paramref name="documentId"/>, valid until <paramref name="expiresAt"/>.</summary>
    public string Create(Guid documentId, DateTimeOffset expiresAt) =>
        _protector.Protect(documentId.ToString("N"), expiresAt);

    /// <summary>
    /// The document a token was made for. False for anything that is not a token of ours: empty, too long, changed by a single character, made with another key or purpose, or expired. The text of the
    /// token is never kept: not in a log, not in an error.
    /// </summary>
    public bool TryRead(string? token, out Guid documentId)
    {
        documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return false;

        try
        {
            return Guid.TryParseExact(_protector.Unprotect(token.Trim()), "N", out documentId) && documentId != Guid.Empty;
        }
        catch (CryptographicException)
        {
            documentId = Guid.Empty;
            return false;
        }
    }
}
