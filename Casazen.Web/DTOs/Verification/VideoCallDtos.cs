namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// The meeting an operator created for a video call with a guest (<c>POST api/verification/{bookingId}/video-call</c>, E22/F01/US01/T03): any provider, the
/// operator makes the meeting and gives CasaZen its address, which goes to the guest by e-mail and is kept in no table and no log of CasaZen.
/// </summary>
public sealed class VideoCallRequest
{
    /// <summary>
    /// The address of the meeting: absolute <c>https</c>, at most 500 characters, no spaces, no credentials in it; 422 <c>video_call_url_invalid</c> otherwise.
    /// It is never fetched, and no table or log of CasaZen holds it (only that a call was proposed, and when, on the "To do"); it stays in the body of the
    /// queued e-mail until the job of the message expires, 24 hours by default after a successful send.
    /// </summary>
    public string? Url { get; set; }
}

/// <summary>The answer of a video call that was proposed: the e-mail with the link was queued for the guest.</summary>
public sealed class VideoCallProposedDto
{
    /// <summary>The only status of an answer 200.</summary>
    public const string SentStatus = "sent";

    public string Status { get; set; } = SentStatus;
}
