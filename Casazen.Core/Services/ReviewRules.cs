using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public enum ReviewEligibility
{
    Eligible = 0,

    NotDirectBooking = 1,

    BookingNotConfirmed = 2,

    StayNotEnded = 3,

    WindowClosed = 4,
    ReviewsDisabled = 5,
    NoShow = 6,
}

public static class ReviewRules
{
    public const int WindowDays = 14;

    public const int MinRating = 1;
    public const int MaxRating = 5;

    public const int TextMaxLength = 1500;
    public const int ReplyMaxLength = 800;
    public const int ReportNoteMaxLength = 500;
    public const int DecisionReasonMaxLength = 500;

    public const string AnonymousAuthorName = "Ospite";

    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 50;

    public const int PreviewLength = 120;

    private const string ErasedName = "ANONYMIZED";

    public static ReviewEligibility Evaluate(BookingSource source, BookingStatus status, DateTime checkOutDate, DateOnly today, bool guestPortal = false)
    {
        if (source != BookingSource.Direct && !guestPortal)
            return ReviewEligibility.NotDirectBooking;

        if (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(status))
            return ReviewEligibility.BookingNotConfirmed;

        var checkOutDay = RomeCalendar.DateInRome(checkOutDate);
        if (today <= checkOutDay)
            return ReviewEligibility.StayNotEnded;

        return today > checkOutDay.AddDays(WindowDays) ? ReviewEligibility.WindowClosed : ReviewEligibility.Eligible;
    }

    public static DateTime WindowEndsAtUtc(DateTime checkOutDate) =>
        RomeCalendar.StartOfDayUtc(RomeCalendar.DateInRome(checkOutDate).AddDays(WindowDays + 1));

    /// <summary>
    /// How the author is shown: the first given name of the guest when the guest agreed, otherwise «Ospite». A first-name field may hold
    /// a whole name by mistake, so only its first word is ever shown.
    /// </summary>
    public static string AuthorNameFor(string? guestFirstName, bool showName)
    {
        if (!showName)
            return AnonymousAuthorName;

        var firstWord = guestFirstName?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrEmpty(firstWord) || string.Equals(firstWord, ErasedName, StringComparison.OrdinalIgnoreCase)
            ? AnonymousAuthorName
            : firstWord.Length > 100 ? firstWord[..100] : firstWord;
    }

    public static string? NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new StringBuilder(text.Length);
        var newlines = 0;
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (c == '\n')
            {
                if (++newlines > 2)
                    continue;
            }
            else if (char.IsControl(c))
            {
                continue;
            }
            else
            {
                newlines = 0;
            }

            builder.Append(c);
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    public static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= PreviewLength ? line : line[..PreviewLength].TrimEnd() + "…";
    }

    public static string StayMonthOf(DateTime stayEndedOn) => RomeCalendar.DateInRome(stayEndedOn).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public static decimal Average(long sum, int count) =>
        count <= 0 ? 0m : Math.Round((decimal)sum / count, 1, MidpointRounding.AwayFromZero);
}
