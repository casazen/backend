namespace Casazen.Infrastructure.Services.ICal;

/// <summary>Why a downloaded document is not an iCalendar feed that can be imported.</summary>
public enum ICalFormatFailure
{
    /// <summary>The response body is empty.</summary>
    Empty,

    /// <summary>The body does not start with <c>BEGIN:VCALENDAR</c> (an HTML login page, JSON, ...).</summary>
    NotICalendar,

    /// <summary>The body starts like a calendar but Ical.Net cannot read it (truncated, broken lines, ...).</summary>
    Unparsable,
}

/// <summary>
/// The feed is not a readable iCalendar document (PC-10, A2-10). Only this is an import error: a valid calendar with
/// no events, or with events that are all cancelled, is a successful import with nothing to block.
/// </summary>
public sealed class ICalFormatException : Exception
{
    public ICalFormatException(ICalFormatFailure failure, Exception? innerException = null)
        : base($"The iCal feed is not a readable iCalendar document ({failure}).", innerException)
    {
        Failure = failure;
    }

    public ICalFormatFailure Failure { get; }
}
