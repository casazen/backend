using Microsoft.Net.Http.Headers;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The headers of a ZIP that is written straight to the response (the document archive export, NF-99). They are set when the first byte goes out
/// (<see cref="HttpResponse.OnStarting(Func{object, Task}, object)"/>) and only for a 200: the service refuses an export beyond its caps before it
/// writes anything, and that refusal, like any other problem, must not carry the name of a file to save.
/// </summary>
public static class ZipDownloadHeaders
{
    /// <summary>Registers the headers to be set when the response starts, under the name <paramref name="fileName"/>.</summary>
    public static void WhenTheResponseStarts(HttpResponse response, string fileName)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        response.OnStarting(
            static state =>
            {
                var (started, name) = ((HttpResponse, string))state;
                Apply(started, name);
                return Task.CompletedTask;
            },
            (response, fileName));
    }

    /// <summary><c>application/zip</c> and an attachment disposition with the name, for a response that is a 200; nothing for any other status.</summary>
    public static void Apply(HttpResponse response, string fileName)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != StatusCodes.Status200OK)
            return;

        response.ContentType = "application/zip";
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();
    }
}
