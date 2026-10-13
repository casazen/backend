using System.Security.Cryptography;
using System.Text;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Comparison of the e-mail address of a private customer of a supplier (SP-10). The address is stored encrypted and found by an
/// HMAC index, so it is never compared as stored: it is compared <b>after</b> it was decrypted, in constant time, and only
/// once the record to compare with has been found by something else (the index here, the booking code in SP-11). A wrong
/// address therefore costs the same as a right one and tells nothing about the record.
/// </summary>
public static class ServiceCustomerEmails
{
    /// <summary>
    /// True when <paramref name="stored"/> and <paramref name="provided"/> are the same address once trimmed and lowercased
    /// (<see cref="ShowcaseBookingRules.NormalizeEmail"/>). Constant time on the bytes of the two normalized forms; a missing
    /// value is never the same as anything.
    /// </summary>
    public static bool SameAddress(string? stored, string? provided)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(provided))
            return false;

        var left = Encoding.UTF8.GetBytes(ShowcaseBookingRules.NormalizeEmail(stored));
        var right = Encoding.UTF8.GetBytes(ShowcaseBookingRules.NormalizeEmail(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
