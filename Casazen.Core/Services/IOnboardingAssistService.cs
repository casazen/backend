using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// «Do it for me» (UX-15, NF-15, NF-107, E10/F01/US03/T01): the host asks CasaZen support to do a part, or a step of the Octorate panel, of the
/// activation for it, and support handles the request from a staff queue.
/// </summary>
/// <remarks>
/// <para><b>A reference, never a copy.</b> The request names the draft of the host's wizard (<see cref="WizardType.OnboardingActivation"/> and the
/// entity it edits) and holds nothing of what the host typed: a draft may hold personal data. The staff view shows ids, context codes, the property
/// and its comune, never the draft and never a secret: the credentials of the Questura stay write-only, so what staff cannot see is asked of the host
/// through the "To do".</para>
/// <para><b>The host's "To do".</b> A request raises one "To do" (entity <see cref="OnboardingAssistCodes.TodoEntityType"/>, the id of the request)
/// whose status is read from the request; it is closed when support does the step or closes the request.</para>
/// <para><b>The log.</b> The request writes <see cref="OrgActivityType.OnboardingAssistRequested"/> and every staff update writes
/// <see cref="OrgActivityType.OnboardingAssistUpdated"/> in the org <b>of the request</b>, with the staff user as the actor (AC-NF15-3).</para>
/// <para><b>Reach.</b> The host operations are bound to the org and to what the scope reaches (AM-03). The staff operations are for the platform
/// administrators only (the web layer decides) and name the org of the request explicitly in every query.</para>
/// </remarks>
public interface IOnboardingAssistService
{
    /// <summary>
    /// The host asks for help. Stores the request with the reference to the draft (and the channel of a channel access), marks the panel step (a
    /// panel context) as asked for, raises the "To do" and writes the line of the log. 409 <see cref="OnboardingAssistCodes.AlreadyOpen"/> when a
    /// request of the same part, property and channel is open (one that a failed call left without its "To do" gets it on that answer): each portal
    /// of a channel access has its own request; 409 <see cref="OnboardingAssistCodes.StepAlreadyDone"/> when the panel step is already done; 422 for a
    /// context that does not fit.
    /// </summary>
    Task<AssistRequestView> RequestAsync(HostScope scope, string userId, AssistRequestInput input, CancellationToken ct);

    /// <summary>The requests of the org the scope reaches, the most recent first (at most <see cref="OnboardingAssistLimits.MaxPageSize"/>).</summary>
    Task<IReadOnlyList<AssistRequestView>> ListAsync(HostScope scope, CancellationToken ct);

    /// <summary>
    /// The staff queue: the requests of every org, open ones first and the oldest first, in SQL (a page of at most
    /// <see cref="OnboardingAssistLimits.MaxPageSize"/>). <paramref name="status"/> narrows to one status.
    /// </summary>
    Task<AssistQueuePage> ListQueueAsync(OnboardingAssistStatus? status, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Staff takes (<see cref="OnboardingAssistStatus.InProgress"/>), completes (<see cref="OnboardingAssistStatus.Done"/>) or closes
    /// (<see cref="OnboardingAssistStatus.Cancelled"/>) a request, with an optional note. Every update writes the line of the log in the org of the
    /// request.
    /// <para><b>«Fatto» is verified or declared, never a silent success</b> (E20/F03/US01/T02). On a panel context <c>Done</c> first runs the verifier
    /// of the step for the org of the request (<see cref="IAssistPanelVerification"/>), outside the lock: the police connection is
    /// <see cref="OnboardingAssistOutcomes.Verified"/> only when the panel reads <c>policeAccount.valid</c>, and the step becomes
    /// <see cref="PanelStepStatus.Verified"/>; what the panel cannot read (no police account on a structure created through the API, ISTAT never) is
    /// <see cref="OnboardingAssistOutcomes.Declared"/> by the staff user, and the step becomes <see cref="PanelStepStatus.Confirmed"/> with the staff user
    /// as who completed it; a step the panel reads as not valid is 409 <see cref="OnboardingAssistCodes.DoneNotValid"/> and nothing changes; a read that
    /// failed (the channel manager down, the quota used) reaches the caller with its code and nothing changes. A whole part (no panel step) is
    /// declared. The host's "To do" is closed and the administrators of the org are told live (the bell, the pop-up, the open page).</para>
    /// 422 <see cref="OnboardingAssistCodes.NoteInvalid"/> for a note over 500 characters or with an e-mail address; 409
    /// <see cref="OnboardingAssistCodes.Closed"/> for a request already done or cancelled; 404 <see cref="OnboardingAssistCodes.NotFound"/>.
    /// </summary>
    Task<AssistQueueItem> UpdateAsync(string staffUserId, Guid requestId, OnboardingAssistStatus status, string? note, CancellationToken ct);

    /// <summary>
    /// One request as staff reads it, with the checklist of what support needs, computed per property (E20/F03/US01/T02): the user of the panel in the
    /// vault, the police credentials, the ISTAT credentials and whether the region needs them, the sender of the property, and each panel step to do
    /// with the key of its guide and its state. Presence and dates only, never a value. A request about the whole org lists the org itself and its
    /// properties (at most <see cref="OnboardingAssistLimits.MaxChecklistProperties"/>). 404 <see cref="OnboardingAssistCodes.NotFound"/>.
    /// </summary>
    Task<AssistRequestDetail> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>
    /// The host says it does not work («Non funziona», E20/F03/US01/T02): a request support marked <c>Done</c> goes back to
    /// <see cref="OnboardingAssistStatus.Requested"/> with the host's note, its panel step goes back to support
    /// (<see cref="PanelStepStatus.AssistRequested"/>), the host's "To do" is raised again, the line of the log is written and the platform staff is
    /// told. 422 <see cref="OnboardingAssistCodes.ReopenNoteRequired"/> for a note missing, over 500 characters or with an e-mail address; 409
    /// <see cref="OnboardingAssistCodes.NotDone"/> for a request that is not done, 409 <see cref="OnboardingAssistCodes.AlreadyOpen"/> when another
    /// request of the same part, property and channel is open; 404 <see cref="OnboardingAssistCodes.NotFound"/> for a request of another org or of a
    /// property the scope does not reach.
    /// </summary>
    Task<AssistRequestView> ReopenAsync(HostScope scope, string userId, Guid requestId, string? note, CancellationToken ct);
}

/// <summary>
/// The check «Fatto» runs on the panel step of a «Do it for me» request (E20/F03/US01/T02): the verifier of the kind (<see cref="IPanelStepVerifier"/>,
/// the one with the highest order), for the org <b>of the request</b>. Staff works from the context of its own org, where a call to the channel manager
/// for another org is refused by design, so the implementation runs the verifier in a scope of its own under the org of the request.
/// </summary>
public interface IAssistPanelVerification
{
    /// <summary>What the panel says about the step; a read that failed and is worth a retry is thrown and decides nothing.</summary>
    Task<PanelVerificationOutcome> VerifyAsync(PanelStepContext context, CancellationToken ct);
}

/// <summary>What the host asks help with.</summary>
/// <param name="Part">The part of the activation plan.</param>
/// <param name="Context">One of <see cref="OnboardingAssistContexts"/>, consistent with the part (<see cref="OnboardingAssistRules.ContextFits"/>).</param>
/// <param name="PropertyId">The property the request is about; <c>null</c> for the whole org (needs a scope over the whole org).</param>
/// <param name="ChannelCode">The channel of a <see cref="OnboardingAssistContexts.PanelChannelAccess"/> request; refused for the others.</param>
public sealed record AssistRequestInput(OnboardingPartKey Part, string Context, Guid? PropertyId = null, string? ChannelCode = null);

/// <summary>
/// A request as the host reads it: codes, ids, instants and the note of support. No draft, no person. <see cref="ChannelCode"/> is the portal of a
/// channel access request (<c>null</c> for the other contexts), so each portal row of the wizard shows its own request. <see cref="Outcome"/> is what
/// «Fatto» recorded (<c>verified</c>: «Il collegamento è attivo»; <c>declared</c>: «Il supporto dichiara di aver collegato …»), <see cref="HostNote"/>
/// and <see cref="ReopenedAt"/> what the host wrote, and when, when it reopened the request.
/// </summary>
public sealed record AssistRequestView(
    Guid Id,
    OnboardingPartKey Part,
    string PartCode,
    Guid? PropertyId,
    string Context,
    OnboardingAssistStatus Status,
    DateTime RequestedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    string? StaffNote,
    Guid? ActivityId,
    string? ChannelCode,
    string? Outcome = null,
    string? HostNote = null,
    DateTime? ReopenedAt = null);

/// <summary>
/// A request as staff reads it in the queue: ids, context codes (with the portal of a channel access, <see cref="ChannelCode"/>), the property and
/// its comune. The reference to the draft is the type of the wizard and what it edits; <b>never the draft itself</b>, never a credential, never the
/// person who asked. <see cref="Outcome"/>, <see cref="HostNote"/> and <see cref="ReopenedAt"/>: what «Fatto» recorded and the host's «Non funziona».
/// </summary>
public sealed record AssistQueueItem(
    Guid Id,
    Guid OrgId,
    string PartCode,
    Guid? PropertyId,
    string? PropertyName,
    string? Comune,
    string Context,
    OnboardingAssistStatus Status,
    DateTime RequestedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    string? AssignedToUserId,
    string? StaffNote,
    WizardType? DraftWizardType,
    Guid? DraftEntityId,
    string? ChannelCode,
    string? Outcome = null,
    string? HostNote = null,
    DateTime? ReopenedAt = null);

/// <summary>One request as staff reads it (<see cref="IOnboardingAssistService.GetAsync"/>): the request and the checklist of what support needs.</summary>
public sealed record AssistRequestDetail(AssistQueueItem Request, IReadOnlyList<AssistChecklistProperty> Checklist);

/// <summary>
/// What support needs for one property of a request (E20/F03/US01/T02), or for the org itself (<see cref="PropertyId"/> <c>null</c>: the user of the
/// panel of the whole org and the steps of the whole org). Presence and dates only, never a value.
/// </summary>
/// <param name="PanelUser">The user of the channel manager panel of the structure in the support vault.</param>
/// <param name="PoliceCredentials">The Alloggiati Web credentials of the property; <c>null</c> for the org itself.</param>
/// <param name="IstatCredentials">The ISTAT credentials of the property (the host's form, else the vault) and whether the region needs them; <c>null</c> for the org itself.</param>
/// <param name="Sender">Who sends the police and ISTAT communications of the property, as stored; <c>null</c> when nothing is stored (the default of PO-02 applies) and for the org itself.</param>
/// <param name="Steps">The panel steps the request is about, with the key of their guide and their state.</param>
public sealed record AssistChecklistProperty(
    Guid? PropertyId,
    string? PropertyName,
    string? Comune,
    AssistCredentialPresence PanelUser,
    AssistCredentialPresence? PoliceCredentials,
    AssistIstatPresence? IstatCredentials,
    AssistSenderLine? Sender,
    IReadOnlyList<AssistChecklistStep> Steps);

/// <summary>Whether a credential is there and since when.</summary>
public sealed record AssistCredentialPresence(bool Present, DateTime? UpdatedAt);

/// <summary>Whether the ISTAT credentials are there, since when, and whether the region needs them (<see cref="AssistIstatRequirements"/>).</summary>
public sealed record AssistIstatPresence(bool Present, DateTime? UpdatedAt, string Requirement);

/// <summary>The stored sender of the police and ISTAT communications of a property and whether the host declared it active after its last change.</summary>
public sealed record AssistSenderLine(ComplianceSenderType Sender, DateTime ChangedAt, bool ActivationDeclared);

/// <summary>
/// One panel step of a checklist: the kind, the channel of a channel access, the key of its guide (the instructions the client shows, the same of the
/// host's guided step), its state (<see cref="AssistChecklistStates.NotStarted"/> or the code of <see cref="PanelStepStatus"/>), what its last check
/// found, and when it was completed.
/// </summary>
public sealed record AssistChecklistStep(
    PanelStepKind Kind,
    string? ChannelCode,
    string InstructionsKey,
    string State,
    string? LastVerificationOutcome,
    DateTime? CompletedAt);

/// <summary>The states of a step of a checklist besides the codes of <see cref="PanelStepStatus"/>.</summary>
public static class AssistChecklistStates
{
    /// <summary>The step has no row yet: nobody started it.</summary>
    public const string NotStarted = "notStarted";
}

/// <summary>Whether the region of a property needs ISTAT credentials (E20/F03/US01/T02).</summary>
public static class AssistIstatRequirements
{
    /// <summary>The request is about the ISTAT connection: support needs them.</summary>
    public const string Required = "required";

    /// <summary>The property is not in Italy: there is no ISTAT.</summary>
    public const string NotRequired = "notRequired";

    /// <summary>
    /// The region decides and the table of the regions is not there yet (E21/F01/US01/T01, decision O6): support checks the guide of the region.
    /// </summary>
    public const string ToVerify = "toVerify";
}

/// <summary>A page of the staff queue.</summary>
public sealed record AssistQueuePage(IReadOnlyList<AssistQueueItem> Items, int TotalCount, int Page, int PageSize);

/// <summary>The stable codes of the «Do it for me» requests: errors, the "To do" entity and its reason. Never rename one.</summary>
public static class OnboardingAssistCodes
{
    /// <summary>409: a request of the same part, property and channel is open.</summary>
    public const string AlreadyOpen = "assist_request_already_open";

    /// <summary>404: no such request.</summary>
    public const string NotFound = "assist_request_not_found";

    /// <summary>409: the request is done or cancelled already.</summary>
    public const string Closed = "assist_request_closed";

    /// <summary>422: the context is not one of the four, or does not fit the part, or the channel is wrong for it.</summary>
    public const string ContextInvalid = "assist_context_invalid";

    /// <summary>422: the status asked is not one staff can set (<c>inProgress</c>, <c>done</c>, <c>cancelled</c>).</summary>
    public const string StatusInvalid = "assist_status_invalid";

    /// <summary>422: the note is over 500 characters, or holds an e-mail address.</summary>
    public const string NoteInvalid = "assist_note_invalid";

    /// <summary>409: the panel step the request is about is done already: there is nothing to ask support.</summary>
    public const string StepAlreadyDone = "assist_step_already_done";

    /// <summary>409: «Fatto» refused, the panel reads the step as not valid (for the police, <c>policeAccount.valid</c> is false).</summary>
    public const string DoneNotValid = "assist_done_not_valid";

    /// <summary>409: only a request support marked done can be reopened.</summary>
    public const string NotDone = "assist_request_not_done";

    /// <summary>422: the host reopens a request with a note: it is missing, over 500 characters or holds an e-mail address.</summary>
    public const string ReopenNoteRequired = "assist_reopen_note_required";

    /// <summary>The entity type of the host's "To do" of a request (its id is the id of the <see cref="OnboardingAssistRequest"/>).</summary>
    public const string TodoEntityType = "OnboardingAssistRequest";

    /// <summary>The reason of the host's "To do" of a request.</summary>
    public const string ReasonRequested = "onboarding_assist_requested";
}

/// <summary>The limits of the staff queue.</summary>
public static class OnboardingAssistLimits
{
    /// <summary>The most requests of a page of the queue and of the host's list.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page size when none is given.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The most properties the checklist of a request about the whole org lists.</summary>
    public const int MaxChecklistProperties = 100;
}

/// <summary>The rules of a «Do it for me» request that do not need the database.</summary>
public static partial class OnboardingAssistRules
{
    /// <summary>The contexts a request can have, in the order of <see cref="OnboardingAssistContexts"/>.</summary>
    public static IReadOnlyList<string> Contexts { get; } =
    [
        OnboardingAssistContexts.PanelPolice,
        OnboardingAssistContexts.PanelIstat,
        OnboardingAssistContexts.PanelChannelAccess,
        OnboardingAssistContexts.Part,
    ];

    /// <summary>
    /// True when <paramref name="context"/> is a context that fits <paramref name="part"/>: the police and ISTAT connections belong to the
    /// compliance part, the access to a channel to the channels part, and a request for a whole part (<see cref="OnboardingAssistContexts.Part"/>)
    /// fits any.
    /// </summary>
    public static bool ContextFits(OnboardingPartKey part, string? context)
    {
        if (context == OnboardingAssistContexts.Part)
            return true;

        return PanelStepCatalog.TryOfContext(context, out var info) && info.Part == part;
    }

    /// <summary>The status code written in the log and spoken by the API for staff: <c>requested</c>, <c>inProgress</c>, <c>done</c>, <c>cancelled</c>.</summary>
    public static string StatusCode(OnboardingAssistStatus status) => PanelStepCatalog.CodeOf(status);

    /// <summary>
    /// The note of staff, trimmed; <c>null</c> when it is blank. False when it is longer than <see cref="OnboardingAssistRequest.StaffNoteMaxLength"/>
    /// characters or holds something that looks like an e-mail address: the note is shown to the host and kept in the request, and holds no personal data.
    /// </summary>
    public static bool TryNormalizeNote(string? note, out string? normalized)
    {
        normalized = null;
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;

        if (trimmed.Length > OnboardingAssistRequest.StaffNoteMaxLength || EmailLike().IsMatch(trimmed))
            return false;

        normalized = trimmed;
        return true;
    }

    /// <summary>Something before and after an at sign, with no space or at sign inside: an e-mail address, or close enough to refuse.</summary>
    [GeneratedRegex(@"[^\s@]+@[^\s@]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex EmailLike();
}
