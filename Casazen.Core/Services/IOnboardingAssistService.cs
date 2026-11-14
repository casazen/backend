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
    /// (<see cref="OnboardingAssistStatus.Cancelled"/>) a request, with an optional note. <c>Done</c> on a panel context confirms the matching panel
    /// step (of the request's channel, for a channel access) with the staff user as who completed it and closes the host's "To do". Every update
    /// writes the line of the log in the org of the request.
    /// 422 <see cref="OnboardingAssistCodes.NoteInvalid"/> for a note over 500 characters or with an e-mail address; 409
    /// <see cref="OnboardingAssistCodes.Closed"/> for a request already done or cancelled; 404 <see cref="OnboardingAssistCodes.NotFound"/>.
    /// </summary>
    Task<AssistQueueItem> UpdateAsync(string staffUserId, Guid requestId, OnboardingAssistStatus status, string? note, CancellationToken ct);
}

/// <summary>What the host asks help with.</summary>
/// <param name="Part">The part of the activation plan.</param>
/// <param name="Context">One of <see cref="OnboardingAssistContexts"/>, consistent with the part (<see cref="OnboardingAssistRules.ContextFits"/>).</param>
/// <param name="PropertyId">The property the request is about; <c>null</c> for the whole org (needs a scope over the whole org).</param>
/// <param name="ChannelCode">The channel of a <see cref="OnboardingAssistContexts.PanelChannelAccess"/> request; refused for the others.</param>
public sealed record AssistRequestInput(OnboardingPartKey Part, string Context, Guid? PropertyId = null, string? ChannelCode = null);

/// <summary>
/// A request as the host reads it: codes, ids, instants and the note of support. No draft, no person. <see cref="ChannelCode"/> is the portal of a
/// channel access request (<c>null</c> for the other contexts), so each portal row of the wizard shows its own request.
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
    string? ChannelCode);

/// <summary>
/// A request as staff reads it in the queue: ids, context codes (with the portal of a channel access, <see cref="ChannelCode"/>), the property and
/// its comune. The reference to the draft is the type of the wizard and what it edits; <b>never the draft itself</b>, never a credential, never the
/// person who asked.
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
    string? ChannelCode);

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
