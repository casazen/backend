using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// «Gestisci con AI» of the compliance cockpit: takes charge of what the host's list of adempimenti holds and that CasaZen can really
/// do, and says plainly what it cannot. It reads the same cockpit the page shows (<see cref="IComplianceWizardService.GetSummaryAsync"/>),
/// so what the page lists and what the assistant sees can never differ.
/// </summary>
/// <remarks>
/// <para><b>What it does, and only that.</b> It asks the guests of a stay for the data Alloggiati Web needs (the check-in link by e-mail,
/// and a message ready to send by WhatsApp or SMS, written by the AI) and it closes the departures of stays that are over. It never
/// writes a data of a guest, never declares a communication sent, never closes a departure it is not sure of (today's, one whose
/// arrival was never registered, one still waiting for the data of its guests) and never touches what only the host can do (the CIN,
/// the documents, the property to declare ready, the upload on the portal of the Questura).</para>
/// <para><b>The rules are the server's, not the AI's.</b> What to do for each item, when, and how many times is decided here by
/// <see cref="AutopilotLimits"/> and the rules of the service; the AI only writes the wording of the chat messages, behind the
/// checks of <see cref="AutopilotChatText"/> and with a plain text when it is not there.</para>
/// <para><b>Honest about the rest.</b> CasaZen does not transmit to the Questura and has no SMS or WhatsApp sender: the plan says so
/// per item (<see cref="AutopilotReasons"/>), it never reports as done what only the host can do.</para>
/// </remarks>
public interface IComplianceAutopilotService
{
    /// <summary>
    /// What the assistant would do now, item by item, and what stays with the host: it changes nothing and contacts nobody.
    /// </summary>
    Task<AutopilotPlan> PlanAsync(HostScope scope, AutopilotCaller caller, CancellationToken cancellationToken = default);

    /// <summary>
    /// Does what <see cref="PlanAsync"/> says it will do, planned again now: the items that are <see cref="AutopilotItemStatus.Ready"/>
    /// (only the ones named by <paramref name="keys"/> when it is given: what the host saw is what runs, never something new). Each item
    /// is its own unit: one that fails does not stop the others. At most <see cref="AutopilotLimits.MaxGuestRequestsPerRun"/> guests are
    /// asked and <see cref="AutopilotLimits.MaxDeparturesPerRun"/> departures closed in one run; the rest is counted.
    /// </summary>
    Task<AutopilotRunResult> RunAsync(
        HostScope scope,
        AutopilotCaller caller,
        IReadOnlyCollection<string>? keys,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the caller may do, said by the layer that knows the roles (the controller): the service never decides on roles itself (TN-3).
/// </summary>
/// <param name="CanWriteProperty">
/// Whether the caller has <c>booking.write</c> on a property: the permission to close a departure and to ask for the data of a stay.
/// Asked once per property per call.
/// </param>
/// <param name="AcquireAiPermit">
/// Takes one permit of the AI rate limit of the user and the org, right before the AI is called; false = the limit is used up and the
/// plain texts are used. Null = no limit (a job).
/// </param>
public sealed record AutopilotCaller(
    Func<Guid, CancellationToken, Task<bool>> CanWriteProperty,
    Func<bool>? AcquireAiPermit = null);

/// <summary>What an item of the cockpit is about.</summary>
public enum AutopilotTopic
{
    /// <summary>The data of the guests that Alloggiati Web needs (<see cref="Enums.ComplianceCockpitAction.CompleteGuestCheckIn"/>).</summary>
    GuestData,

    /// <summary>A departure to close (<see cref="Enums.ComplianceCockpitAction.CheckOut"/>).</summary>
    Departure,

    /// <summary>An Alloggiati Web communication to send or to fix: the assistant checks it, only the host can send it.</summary>
    Alloggiati,
}

/// <summary>Who does an item: the assistant now, the assistant later, or only the host.</summary>
public enum AutopilotItemStatus
{
    /// <summary>The assistant does it in the next run.</summary>
    Ready,

    /// <summary>The assistant has done its part, or does it when something else happens (the data of the guests arrive).</summary>
    Waiting,

    /// <summary>Only the host can: <see cref="AutopilotItem.Reason"/> says why.</summary>
    Host,
}

/// <summary>How an item ended in a run.</summary>
public enum AutopilotOutcomeStatus
{
    Done,
    Failed,

    /// <summary>It was no longer to be done when the run came to it (someone else did it, the state changed).</summary>
    Skipped,
}

/// <summary>How a guest can be reached, as <see cref="AutopilotItem.Channels"/> says.</summary>
public static class AutopilotChannels
{
    /// <summary>The check-in link goes by e-mail from CasaZen.</summary>
    public const string Email = "email";

    /// <summary>A message with the link is ready for the host to send by WhatsApp or SMS from the phone.</summary>
    public const string Chat = "chat";
}

/// <summary>
/// Why an item is what it is: stable snake_case codes the web app translates, never a text (the same convention as the cockpit).
/// </summary>
public static class AutopilotReasons
{
    /// <summary>The caller has no <c>booking.write</c> on the property of the stay.</summary>
    public const string NoPermission = "no_permission";

    // GuestData
    public const string AskGuests = "ask_guests";
    public const string AskedRecently = "asked_recently";
    public const string AskedEnough = "asked_enough";
    public const string AlreadySubmitted = "already_submitted";
    public const string NoContact = "no_contact";

    // Departure
    public const string CloseDeparture = "close_departure";
    public const string AwaitingGuestData = "awaiting_guest_data";
    public const string DepartureToday = "departure_today";
    public const string ArrivalNotRegistered = "arrival_not_registered";

    // Alloggiati
    public const string ReadyToUpload = "ready_to_upload";
    public const string DataMissing = "data_missing";
    public const string CodesToComplete = "codes_to_complete";

    /// <summary>A stay of less than 1 or more than 30 days: the portal takes no file for it.</summary>
    public const string StayDaysInvalid = "stay_days_invalid";

    public const string Failed = "failed";
}

/// <summary>How an item ended: stable codes the web app translates.</summary>
public static class AutopilotOutcomeCodes
{
    public const string EmailQueued = "email_queued";

    /// <summary>No e-mail left (or none could): the host sends the message he is given.</summary>
    public const string ChatReady = "chat_ready";

    public const string EmailFailed = "email_failed";
    public const string DepartureClosed = "departure_closed";
    public const string Failed = "failed";
    public const string NoLongerNeeded = "no_longer_needed";
}

/// <summary>
/// One thing of the cockpit seen by the assistant.
/// </summary>
/// <param name="Key"><c>{Topic}:{bookingId}</c>: what the web app sends back to run exactly what it showed.</param>
/// <param name="Reason">Why it is <paramref name="Status"/> (<see cref="AutopilotReasons"/>).</param>
/// <param name="GuestName">The name of the cockpit item (the booker).</param>
/// <param name="Channels">For a <see cref="AutopilotTopic.GuestData"/> that is <see cref="AutopilotItemStatus.Ready"/>: how the guest can be reached.</param>
/// <param name="DueAt">When it is due (UTC), as the cockpit says; null when no date fixes it.</param>
public sealed record AutopilotItem(
    string Key,
    AutopilotTopic Topic,
    AutopilotItemStatus Status,
    string Reason,
    Guid BookingId,
    Guid PropertyId,
    string GuestName,
    string PropertyName,
    IReadOnlyList<string> Channels,
    DateTime? DueAt);

/// <param name="Items">Every item of the cockpit that has a stay, most overdue first.</param>
/// <param name="AiAvailable">An AI provider is in effect, so the chat messages are written by it; otherwise they are the plain texts.</param>
/// <param name="BeyondLimit">The <see cref="AutopilotItemStatus.Ready"/> items one run does not reach (the limits of <see cref="AutopilotLimits"/>).</param>
public sealed record AutopilotPlan(IReadOnlyList<AutopilotItem> Items, bool AiAvailable, int BeyondLimit);

/// <summary>
/// A message for a guest that the host sends from his own phone: the text (with the link of the check-in), and the two links that open
/// WhatsApp and the messages app with it, when the guest has a number that can be written in international form.
/// </summary>
/// <param name="Language">The language of the text (<see cref="AutopilotLanguages"/>).</param>
/// <param name="AiWritten">The wording is the AI's (otherwise a plain text of CasaZen).</param>
public sealed record AutopilotChatDraft(string Language, string Text, string? WhatsAppUrl, string? SmsUrl, bool AiWritten);

/// <summary>How an item ended.</summary>
/// <param name="Code"><see cref="AutopilotOutcomeCodes"/>; for a failure the code of the rule that refused is in <paramref name="ErrorCode"/>.</param>
/// <param name="EmailStatus"><c>Queued</c> or <c>Failed</c> for the e-mail of the link; null when none was asked for.</param>
/// <param name="EmailError">Why the e-mail did not leave (<see cref="Entities.GuestCheckInLinkEmailErrors"/>).</param>
public sealed record AutopilotOutcome(
    string Key,
    AutopilotTopic Topic,
    Guid BookingId,
    string GuestName,
    string PropertyName,
    AutopilotOutcomeStatus Status,
    string Code,
    string? EmailStatus = null,
    string? EmailError = null,
    AutopilotChatDraft? Chat = null,
    string? ErrorCode = null);

/// <param name="AiUsed">The wording of at least one chat message is the AI's.</param>
/// <param name="AiReason">Why the AI did not write them (<see cref="AiSuggestionFallbackReasons"/>), when it was asked and did not.</param>
/// <param name="BeyondLimit">What this run did not reach: run again.</param>
public sealed record AutopilotRunResult(IReadOnlyList<AutopilotOutcome> Outcomes, bool AiUsed, string? AiReason, int BeyondLimit);
