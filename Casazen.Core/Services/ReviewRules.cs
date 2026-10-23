using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>Whether a booking may be reviewed now (<see cref="ReviewRules.Evaluate"/>).</summary>
public enum ReviewEligibility
{
    Eligible = 0,

    /// <summary>The booking did not come from the booking site (entered by the host, an OTA stay): only direct bookings are reviewed.</summary>
    NotDirectBooking = 1,

    /// <summary>The booking was cancelled, or never confirmed.</summary>
    BookingNotConfirmed = 2,

    /// <summary>The stay is not over: its check-out day has not passed.</summary>
    StayNotEnded = 3,

    /// <summary>The 14 days after the check-out are gone.</summary>
    WindowClosed = 4,
}

/// <summary>
/// The rules of the verified reviews (DB-12a), in one place so the invite, the link page, the submission and the host's screen cannot
/// disagree. Pure: no clock, no database.
/// </summary>
public static class ReviewRules
{
    /// <summary>Days after the check-out day in which the guest may still write the review (decision DB-D12, default).</summary>
    public const int WindowDays = 14;

    public const int MinRating = 1;
    public const int MaxRating = 5;

    public const int TextMaxLength = 1500;
    public const int ReplyMaxLength = 800;
    public const int ReportNoteMaxLength = 500;
    public const int DecisionReasonMaxLength = 500;

    /// <summary>What the public reads in place of a name the guest did not agree to show, or that was erased.</summary>
    public const string AnonymousAuthorName = "Ospite";

    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 50;

    /// <summary>The stored placeholder of an erased name (<c>GuestDataEraser.AnonymizedName</c>).</summary>
    private const string ErasedName = "ANONYMIZED";

    /// <summary>
    /// «Verified»: a booking of the booking site that was not cancelled, whose stay is over, inside the window. <paramref name="today"/>
    /// is the Europe/Rome day; the check-out day itself is not over yet, the day after is the first day to review, and the last is the
    /// check-out day plus <see cref="WindowDays"/>. It does not prove that the guest arrived, and the page that explains «verified» says so.
    /// </summary>
    public static ReviewEligibility Evaluate(BookingSource source, BookingStatus status, DateTime checkOutDate, DateOnly today)
    {
        if (source != BookingSource.Direct)
            return ReviewEligibility.NotDirectBooking;

        if (status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
            return ReviewEligibility.BookingNotConfirmed;

        var checkOutDay = RomeCalendar.DateInRome(checkOutDate);
        if (today <= checkOutDay)
            return ReviewEligibility.StayNotEnded;

        return today > checkOutDay.AddDays(WindowDays) ? ReviewEligibility.WindowClosed : ReviewEligibility.Eligible;
    }

    /// <summary>
    /// The UTC instant the window closes: the start, in Europe/Rome, of the day after the last day to review. The signed link of the
    /// invite expires then.
    /// </summary>
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

    /// <summary>
    /// Plain text as stored: line breaks normalised, control characters dropped, three or more blank lines made two, trimmed;
    /// <c>null</c> when nothing is left. Never HTML: the text is shown as text.
    /// </summary>
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

    /// <summary>The month of a stay as the public reads it, <c>yyyy-MM</c>.</summary>
    public static string StayMonthOf(DateTime stayEndedOn) => RomeCalendar.DateInRome(stayEndedOn).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The average of the marks shown to the public: one decimal, halves up.</summary>
    public static decimal Average(long sum, int count) =>
        count <= 0 ? 0m : Math.Round((decimal)sum / count, 1, MidpointRounding.AwayFromZero);
}
