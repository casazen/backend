namespace Casazen.Infrastructure.Services.ICal;

public enum ICalFormatFailure
{
    Empty,

    NotICalendar,

    Unparsable,
}

public sealed class ICalFormatException : Exception
{
    public ICalFormatException(ICalFormatFailure failure, Exception? innerException = null)
        : base($"The iCal feed is not a readable iCalendar document ({failure}).", innerException)
    {
        Failure = failure;
    }

    public ICalFormatFailure Failure { get; }
}
