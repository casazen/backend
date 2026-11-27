using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The signed link of the review invitation (DB-12a): a token that names one booking and expires at the end of the review window. It is
/// encrypted and authenticated by ASP.NET Core Data Protection (the key ring is in the database, so a link outlives a redeploy), under a
/// purpose of its own: nobody can make one for another booking, extend it, or reuse a token made for something else. Nothing is stored for
/// it, and it carries nothing but the booking id (no name, no address). Looking at a link does not change anything; what it allows
/// (one review per booking) is enforced by the review itself, not by the link.
/// </summary>
public sealed class ReviewLinkTokens
{
    public const string Purpose = "Casazen.Reviews.InviteLink.v1";

    private const int MaxTokenLength = 1024;

    private readonly ITimeLimitedDataProtector _protector;

    public ReviewLinkTokens(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    public string Create(Guid bookingId, DateTimeOffset expiresAt) =>
        _protector.Protect(bookingId.ToString("N"), expiresAt);

    public bool TryRead(string? token, out Guid bookingId)
    {
        bookingId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return false;

        try
        {
            return Guid.TryParseExact(_protector.Unprotect(token.Trim()), "N", out bookingId);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
