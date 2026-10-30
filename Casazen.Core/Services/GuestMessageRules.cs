using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>Whether the thread of a booking takes new messages now (<see cref="GuestMessageRules.StateOf"/>).</summary>
public enum GuestThreadState
{
    /// <summary>Both sides can write.</summary>
    Open = 0,

    /// <summary>The seventh day after the check-out is over: the thread is read-only (new messages answer 409 <c>thread_closed</c>).</summary>
    Closed = 1,

    /// <summary>There is no thread for this booking: it is not a direct booking, or it was cancelled or never confirmed.</summary>
    Unavailable = 2,
}

/// <summary>
/// The rules of the messages between the guest and the host (DB-14a), in one place so the guest's API, the host's API and the emails
/// cannot disagree. Pure: no clock, no database.
/// </summary>
public static partial class GuestMessageRules
{
    public const int TextMaxLength = 2000;

    /// <summary>Days after the check-out day in which the thread still takes messages (the last day is the check-out day plus seven).</summary>
    public const int ClosedAfterCheckOutDays = 7;

    /// <summary>At most one email to each side per thread in this many minutes (an email is a consequence of a message, never of a timer).</summary>
    public const int NotifyIntervalMinutes = 10;

    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 64;

    /// <summary>How many messages a transcript carries at most (the latest ones).</summary>
    public const int TranscriptMaxMessages = 300;

    /// <summary>Characters of the last message of the guest shown in the host's list.</summary>
    public const int PreviewLength = 140;

    /// <summary>How many threads with unread messages the badges endpoint returns at most (the ones with the latest guest message first).</summary>
    public const int UnreadThreadsMax = 200;

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>What replaces a card number or an IBAN in the text of the guest.</summary>
    public const string RedactedMark = "[rimosso]";

    /// <summary>
    /// The state of the thread of a booking at <paramref name="nowUtc"/>. Only a direct booking (the guest has the area of the site)
    /// that was confirmed and not cancelled has a thread; it is open until the end of the seventh day after the check-out day
    /// (Europe/Rome).
    /// </summary>
    public static GuestThreadState StateOf(BookingSource source, BookingStatus status, DateTime checkOutDate, DateTime nowUtc)
    {
        if (source != BookingSource.Direct || status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
            return GuestThreadState.Unavailable;

        return nowUtc >= ClosesAtUtc(checkOutDate) ? GuestThreadState.Closed : GuestThreadState.Open;
    }

    /// <summary>The UTC instant from which the thread is closed: the start, in Europe/Rome, of the day after the last day to write.</summary>
    public static DateTime ClosesAtUtc(DateTime checkOutDate) =>
        RomeCalendar.StartOfDayUtc(RomeCalendar.DateInRome(checkOutDate).AddDays(ClosedAfterCheckOutDays + 1));

    /// <summary>The text as stored before redaction (line breaks normalised, control characters dropped, trimmed); <c>null</c> when it is empty.</summary>
    public static string? NormalizeText(string? text) => ReviewRules.NormalizeText(text);

    /// <summary>
    /// The key of a send as stored: 8 to 64 letters, digits, dashes or underscores (a UUID fits); <c>null</c> when the header is
    /// missing or is something else.
    /// </summary>
    public static string? NormalizeIdempotencyKey(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < IdempotencyKeyMinLength || trimmed.Length > IdempotencyKeyMaxLength)
            return null;

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return null;
        }

        return trimmed;
    }

    /// <summary>The first <see cref="PreviewLength"/> characters of a text on one line, with «…» when it was cut.</summary>
    public static string Preview(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= PreviewLength ? line : line[..PreviewLength].TrimEnd() + "…";
    }

    /// <summary>
    /// Replaces the card numbers (13 to 19 digits that pass the Luhn check, with spaces or dashes between the digits) and the IBANs (the
    /// check digits of ISO 13616 must be right) of <paramref name="text"/> with <see cref="RedactedMark"/>. The guest's identity papers
    /// and cards must never sit in a thread the host and the platform keep: the check-in has its own safe form for the documents.
    /// <paramref name="changed"/> says whether anything was replaced.
    /// </summary>
    public static string Redact(string text, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = RedactCards(RedactIbans(text));
        changed = !string.Equals(redacted, text, StringComparison.Ordinal);
        return redacted;
    }

    // Two letters, two digits and 11 to 30 more letters or digits, with a space or a dash allowed between any two: the usual groups of four.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2}\d{2}(?:[ \-]?[A-Za-z0-9]){11,30}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanCandidate();

    // 13 to 19 digits, a space or a dash allowed between two of them, not part of a longer run of digits.
    [GeneratedRegex(@"(?<!\d)\d(?:[ \-]?\d){12,18}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CardCandidate();

    private static string RedactIbans(string text) =>
        IbanCandidate().Replace(text, match =>
        {
            var value = match.Value;
            var compact = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsAsciiLetterOrDigit(c))
                    compact.Append(char.ToUpperInvariant(c));
            }

            // The match may run on into the words after the IBAN: the longest valid IBAN at its start is the one to hide.
            for (var length = Math.Min(compact.Length, 34); length >= 15; length--)
            {
                if (!IsValidIban(compact.ToString(0, length)))
                    continue;

                var seen = 0;
                var end = 0;
                for (; end < value.Length && seen < length; end++)
                {
                    if (char.IsAsciiLetterOrDigit(value[end]))
                        seen++;
                }

                return RedactedMark + value[end..];
            }

            return value;
        });

    private static string RedactCards(string text) =>
        CardCandidate().Replace(text, match =>
        {
            var digits = new string(match.Value.Where(char.IsAsciiDigit).ToArray());
            return digits.Length is >= 13 and <= 19 && PassesLuhn(digits) ? RedactedMark : match.Value;
        });

    // ISO 13616: the first four characters move to the end, the letters become 10..35, and the number is 1 modulo 97.
    private static bool IsValidIban(string compact)
    {
        var remainder = 0;
        foreach (var c in string.Concat(compact.AsSpan(4), compact.AsSpan(0, 4)))
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = (value < 10 ? remainder * 10 + value : remainder * 100 + value) % 97;
        }

        return remainder == 1;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
