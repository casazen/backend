using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Casazen.Core.Services;

/// <summary>
/// The booking code the guest reads in the confirmation email and on the checkout outcome page, and types with the email
/// address in "Le mie prenotazioni" (BK-11, A3-10). Not the booking id (logged, in the host's links and in Stripe): 10
/// random characters of the Crockford base32 alphabet (50 bits, no I, L, O, U), stored without separator
/// (<see cref="Entities.Booking.BookingCode"/>, unique per org) and shown as <c>XXXXX-XXXXX</c>.
/// </summary>
/// <remarks>Frontend mirror: <c>src/lib/booking-code.ts</c>.</remarks>
public static class BookingCodes
{
    /// <summary>Characters of a stored code.</summary>
    public const int Length = 10;

    /// <summary>Crockford base32: digits and letters without I, L, O and U, so that no two characters look alike.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>New random code, in the stored form (no separator).</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[bytes[i] & 31];
        return new string(chars);
    }

    /// <summary>The code as shown to people: <c>XXXXX-XXXXX</c>.</summary>
    public static string Format(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Length == Length ? $"{code[..5]}-{code[5..]}" : code;
    }

    /// <summary>
    /// The stored form of what a guest typed or pasted: spaces and dashes removed, upper case, and the characters a
    /// person may read for others (O as 0, I and L as 1). False when the result is not a code.
    /// </summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 64)
            return false;

        Span<char> chars = stackalloc char[Length];
        var count = 0;
        foreach (var raw in input)
        {
            if (char.IsWhiteSpace(raw) || raw is '-' or '‐' or '‑' or '‒' or '–' or '—' or '−')
                continue;

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (count == Length || Alphabet.IndexOf(c) < 0)
                return false;
            chars[count++] = c;
        }

        if (count != Length)
            return false;

        code = new string(chars);
        return true;
    }
}
