using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public enum GuestThreadState
{
    Open = 0,

    Closed = 1,

    Unavailable = 2,
}

public static partial class GuestMessageRules
{
    public const int TextMaxLength = 2000;

    public const int ClosedAfterCheckOutDays = 7;

    public const int NotifyIntervalMinutes = 10;

    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 64;

    public const int TranscriptMaxMessages = 300;

    public const int PreviewLength = 140;

    public const int UnreadThreadsMax = 200;

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public const string RedactedMark = "[rimosso]";

    public static GuestThreadState StateOf(BookingSource source, BookingStatus status, DateTime checkOutDate, DateTime nowUtc, bool guestPortal = false)
    {
        if ((!guestPortal && source != BookingSource.Direct) || status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
            return GuestThreadState.Unavailable;

        return nowUtc >= ClosesAtUtc(checkOutDate) ? GuestThreadState.Closed : GuestThreadState.Open;
    }

    public static readonly Expression<Func<GuestThread, bool>> IsThreadOfADirectBooking = thread =>
        thread.Booking.Source == BookingSource.Direct
        && (thread.Booking.Status == BookingStatus.Confirmed
            || thread.Booking.Status == BookingStatus.CheckedIn
            || thread.Booking.Status == BookingStatus.CheckedOut);

    public static Expression<Func<GuestThread, bool>> EligibleThreads(bool guestPortal) =>
        guestPortal
            ? thread => thread.Booking.Status == BookingStatus.Confirmed
                || thread.Booking.Status == BookingStatus.CheckedIn
                || thread.Booking.Status == BookingStatus.CheckedOut
            : IsThreadOfADirectBooking;

    public static DateTime ClosesAtUtc(DateTime checkOutDate) =>
        RomeCalendar.StartOfDayUtc(RomeCalendar.DateInRome(checkOutDate).AddDays(ClosedAfterCheckOutDays + 1));

    public static string? NormalizeText(string? text) => ReviewRules.NormalizeText(text);

    public static Entities.Enums.MessageOrigin OriginOf(GuestMessageSender sender) =>
        sender == GuestMessageSender.Host ? Entities.Enums.MessageOrigin.Person : Entities.Enums.MessageOrigin.Guest;

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

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2}\d{2}(?:[ \-]?[A-Za-z0-9]){11,30}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanCandidate();

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
