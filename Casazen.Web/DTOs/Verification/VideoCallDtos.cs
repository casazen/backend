namespace Casazen.Web.DTOs.Verification;

public sealed class VideoCallRequest
{
    /// <summary>
    /// The address of the meeting: absolute <c>https</c>, at most 500 characters, no spaces, no credentials in it; 422 <c>video_call_url_invalid</c> otherwise.
    /// It is never fetched, and no table or log of CasaZen holds it (only that a call was proposed, and when, on the "To do"); it stays in the body of the
    /// queued e-mail until the job of the message expires, 24 hours by default after a successful send.
    /// </summary>
    public string? Url { get; set; }
}

public sealed class VideoCallProposedDto
{
    public const string SentStatus = "sent";

    public string Status { get; set; } = SentStatus;
}
