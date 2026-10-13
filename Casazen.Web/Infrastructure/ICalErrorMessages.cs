using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Turns the iCal sync error stored on a feed into what the API shows: the stable code (<see cref="ICalErrorCodes"/>)
/// and its localized message. A stored value that is not a code (an exception message saved before FD-16) is shown
/// as <see cref="ICalErrorCodes.SyncFailed"/>, never as is.
/// </summary>
public static class ICalErrorMessages
{
    public static (string? Code, string? Message) Describe(string? storedError, IStringLocalizer localizer)
    {
        var code = ICalErrorCodes.Normalize(storedError);
        return code is null ? (null, null) : (code, localizer[ICalErrorCodes.MessageKey(code)].Value);
    }
}
