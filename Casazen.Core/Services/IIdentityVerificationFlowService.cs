using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The identity verification of one check-in session (CHK-11, E03/F04/US02/T04, NF-50): who must verify (only the guest who leaves the document:
/// the single guest, the head of family or the head of group of the stay; the other adults skip the step), the biometric consent (explicit,
/// separate, refusable: ADR-013-R9), the document, the attempts, the call to <see cref="IIdentityVerificationAgent"/>, the log row, the session
/// and the release of the lock code the moment the verification passes. Every way to a person stays open (GDPR art. 22).
/// </summary>
/// <remarks>
/// The session is the one the public endpoint found by its token (tracked, with its booking, property and booker). Nothing of a selfie is kept:
/// it is read in the request, compared and zeroed (ADR-013-R2). The photo of the document is kept encrypted (<c>DocumentImage</c>) only for a
/// property outside Ticino, after the consent, and only until the verification is over (ADR-013-R6).
/// </remarks>
public interface IIdentityVerificationFlowService
{
    Task<IdentityVerificationState> GetStateAsync(GuestCheckInSession session, CancellationToken ct);

    Task<BiometricConsentResult> RecordConsentAsync(GuestCheckInSession session, bool consentGiven, string? consentVersion, CancellationToken ct);

    /// <summary>
    /// <see cref="RecordConsentAsync(GuestCheckInSession, bool, string?, CancellationToken)"/> on the self-hosted flow (E70/F02/US01/T01, NF-188,
    /// <c>Features:CheckInIdentitySelfHosted</c> with <c>Features:CheckInIdentityVerification</c>): the answer is also the <b>choice</b> of the guest,
    /// <c>automatic</c> for a consent given to the served version, <c>assisted</c> for a refusal (<c>IdentityChoice</c>, <c>IdentityChoiceAt</c>), and a session whose
    /// identity step was not opened (<see cref="OpenIdentityAsync"/>) is opened by it, with the version of the consent the registry serves as the notice and
    /// <paramref name="channel"/> (a code of <c>IdentityChannels</c>, the family of the browser: never the User-Agent) as its channel. A consent for another version is
    /// still <see cref="BiometricConsentStatus.VersionUnknown"/> and records nothing. With the self-hosted flow off it is the four-argument method, unchanged.
    /// </summary>
    Task<BiometricConsentResult> RecordConsentAsync(GuestCheckInSession session, bool consentGiven, string? consentVersion, string? channel, CancellationToken ct);

    /// <summary>
    /// Opens the identity step of the self-hosted flow (E70/F02/US01/T01, NF-188, AC-NF188-1..2), once: the first call records when (<c>IdentityOpenedAt</c>), the
    /// notice shown, which must be the version of the biometric consent the registry of the legal texts serves now (<c>IdentityNoticeVersion</c> and the SHA-256 of
    /// its reference text, <c>IdentityNoticeTextHash</c>, as the consent keeps it), the <paramref name="channel"/> (a code of <c>IdentityChannels</c>; anything else is
    /// <c>web:other</c>) and binds the verification to ONE guest, the one the flow verifies (<c>IdentityGuestId</c>, see the remarks of the service). Later calls change
    /// nothing and answer the state. A closed verification answers <see cref="IdentityOpenStatus.Closed"/>, another version of the notice
    /// <see cref="IdentityOpenStatus.NoticeVersionUnknown"/>; a verification that already passed or is already with a person is not opened (the state says so).
    /// Serialized on the session row like the presence challenge. With the self-hosted flow off nothing is recorded.
    /// </summary>
    Task<IdentityOpenResult> OpenIdentityAsync(GuestCheckInSession session, string? noticeVersion, string? channel, CancellationToken ct);

    /// <summary>
    /// Reads the document (the MRZ, then the printed fields) with the capture guidance of the agent, checks it against the check-in data of the
    /// guest who must verify (flags only, kept on the session) and, outside Ticino and after the consent, keeps its photo encrypted so the selfie
    /// can be compared without sending it again. One picture with the face and the MRZ (a passport): <see cref="DocumentSide.Single"/>.
    /// </summary>
    Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, CancellationToken ct);

    /// <summary>
    /// <see cref="SubmitDocumentAsync(GuestCheckInSession, Stream, CancellationToken)"/> for one <paramref name="side"/> of a document that has two
    /// (E22/F01/US01/T02): the Italian electronic identity card and the other TD1 cards have the face on the front and the MRZ on the back. The face must
    /// come from the front (or a single picture), the MRZ from any side; the step is acceptable when both were found across the pictures of the
    /// attempt. A side sent again replaces its own picture and leaves the other; a single picture replaces both. The guidance codes say which side is
    /// missing (<see cref="CaptureGuidanceCodes.DocumentBackNeeded"/>, <see cref="CaptureGuidanceCodes.DocumentFrontNeeded"/>). Re-taking a side never
    /// counts as an attempt: the attempts are the complete document and selfie tries. The rules of what is kept do not change: both pictures encrypted
    /// in the private bucket until the verification is over, nothing kept for a property in Ticino.
    /// </summary>
    Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, DocumentSide side, CancellationToken ct);

    /// <summary>
    /// Runs the agent on the selfie, in the request (ADR-013-R1), logs the outcome and updates the session; on <c>ok</c> releases the lock code
    /// (shown and e-mailed), on a review, the last failed attempt or an error hands the verification to an operator. For a property in Ticino the
    /// photo of the document comes again with the selfie (<paramref name="documentPhoto"/>) and must be the one read.
    /// </summary>
    Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto, CancellationToken ct);

    /// <summary>
    /// <see cref="SubmitSelfieAsync(GuestCheckInSession, Stream, Stream?, CancellationToken)"/> for a document of two sides (E22/F01/US01/T02): for a property
    /// in Ticino, where nothing is kept, <paramref name="documentPhoto"/> is the front (the face) and <paramref name="documentPhotoBack"/> the back, both
    /// the pictures that were read (compared by their SHA-256); elsewhere they are not needed, the encrypted copies are used. A document of which the face
    /// or the MRZ is still missing does not reach the comparison (<see cref="SelfieStepStatus.DocumentRequired"/>, no attempt is spent).
    /// </summary>
    Task<SelfieStepResult> SubmitSelfieAsync(
        GuestCheckInSession session,
        Stream selfie,
        Stream? documentPhoto,
        Stream? documentPhotoBack,
        CancellationToken ct);

    Task<SelfieChallenge?> IssueSelfieChallengeAsync(GuestCheckInSession session, CancellationToken ct);

    Task<SelfieStepResult> SubmitChallengedSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? turnedSelfie,
        string? challengeToken, Stream? documentPhoto, Stream? documentPhotoBack, CancellationToken ct);

    /// <summary>
    /// The guest asks for a person, at any step, also after a retry or a failed attempt (AC-NF51-11): the operators are alerted at once. On the self-hosted flow an
    /// opened verification that is not closed records the switch to the assisted path (<c>IdentityChoice</c> <c>assisted</c>) and is closed by the hand-off; the
    /// request stays available after the closure.
    /// </summary>
    Task<IdentityVerificationState> RequestOperatorAsync(GuestCheckInSession session, CancellationToken ct);
}

public static class BiometricConsentRules
{
    public const int VersionMaxLength = 50;
}

public static class IdentityVerificationSteps
{
    public const string Consent = "consent";

    public const string Document = "document";

    public const string Selfie = "selfie";

    public const string Presence = "presence";

    public const string Done = "done";

    public const string Operator = "operator";
}

public enum DocumentSide
{
    Single = 0,

    Front = 1,

    Back = 2,
}

public static class DocumentSides
{
    public const string Single = "single";
    public const string Front = "front";
    public const string Back = "back";

    public static bool TryParse(string? value, out DocumentSide side)
    {
        side = DocumentSide.Single;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case Single:
                return true;
            case Front:
                side = DocumentSide.Front;
                return true;
            case Back:
                side = DocumentSide.Back;
                return true;
            default:
                return false;
        }
    }

    public static string CodeOf(DocumentSide side) => side switch
    {
        DocumentSide.Front => Front,
        DocumentSide.Back => Back,
        _ => Single,
    };
}

/// <summary>
/// Where the verification of a session stands: the <paramref name="Step"/> of <see cref="IdentityVerificationSteps"/>, the last
/// <paramref name="Result"/> (<c>ok</c>, <c>retry</c>, <c>operator</c>, or null before any), whether the biometric verification is offered,
/// the consent, whether the document was checked, the attempts left, whether an operator was asked, whether the property keeps no document
/// (Ticino: <paramref name="DocumentNotArchived"/>, ADR-013-R15) and the position of the guest who must verify (the others skip the step).
/// For a document of two sides (E22/F01/US01/T02) <paramref name="DocumentSides"/> are the pictures of the attempt (<c>front</c>, <c>back</c>, or
/// <c>single</c>) and <paramref name="DocumentSideNeeded"/> the side still to photograph (<c>front</c> or <c>back</c>), so a reloaded page asks for it; the
/// document counts as checked, and the step moves to the selfie, only when the face and the MRZ were found across the pictures.
/// <para>
/// The self-hosted flow (E70-E74, <c>Features:CheckInIdentitySelfHosted</c>) adds the members after <paramref name="DocumentSideNeeded"/>, false or null until
/// its tasks fill them, so the legacy path reads exactly as before: <paramref name="SelfHosted"/> (the session follows the self-hosted flow),
/// <paramref name="Opened"/> (the step was opened and bound to one guest), <paramref name="NoticeVersion"/> (the version of the notice recorded at the opening),
/// <paramref name="Choice"/> (a code of <c>IdentityChoices</c>), <paramref name="FormatId"/> (the format of the registry the document was read as),
/// <paramref name="GuidedRetryAvailable"/> (the one guided retry is still available), <paramref name="Closed"/> (the verification is closed: no new attempt) and
/// <paramref name="Outcome"/> (what the guest sees of the closed verification: <c>ok</c>, <c>retry</c> or <c>operator</c>).
/// </para>
/// </summary>
public sealed record IdentityVerificationState(
    string Step,
    string? Result,
    bool BiometricOffered,
    bool ConsentRecorded,
    bool ConsentRefused,
    bool DocumentChecked,
    int AttemptsRemaining,
    bool OperatorRequested,
    bool DocumentNotArchived,
    int VerifyingGuestPosition,
    IReadOnlyList<string>? DocumentSides = null,
    string? DocumentSideNeeded = null,
    bool SelfHosted = false,
    bool Opened = false,
    string? NoticeVersion = null,
    string? Choice = null,
    string? FormatId = null,
    bool GuidedRetryAvailable = false,
    bool Closed = false,
    string? Outcome = null)
{
    public bool OperatorPathOpen => true;
}

public enum BiometricConsentStatus
{
    Recorded = 1,

    Refused = 2,

    Operator = 3,

    VersionRequired = 4,

    VersionUnknown = 5,
}

public sealed record BiometricConsentResult(BiometricConsentStatus Status, IdentityVerificationState State);

public enum IdentityOpenStatus
{
    /// <summary>The state answers: the step was opened now, or before (nothing changed), or not at all because the verification is over (see <see cref="IdentityVerificationState.Opened"/>).</summary>
    Answered = 1,

    NoticeVersionUnknown = 2,

    Closed = 3,

    SessionUnavailable = 4,
}

public sealed record IdentityOpenResult(IdentityOpenStatus Status, IdentityVerificationState? State);

/// <summary>
/// The reading of the document (the fields for the guest to confirm), whether the capture is good enough, the guidance codes, whether its photo
/// was kept (encrypted) for the comparison and whether the property keeps no document (Ticino). For a document of two sides (E22/F01/US01/T02)
/// <paramref name="Side"/> is the one just processed and <paramref name="SidesReceived"/> the pictures of the attempt; <paramref name="Acceptable"/> then
/// means that the face and the MRZ were found across them, and the guidance says which side is missing.
/// </summary>
public sealed record DocumentStepResult(
    DocumentReading Reading,
    bool Acceptable,
    IReadOnlyList<string> GuidanceCodes,
    bool DocumentStored,
    bool DocumentNotArchived,
    DocumentSide Side = DocumentSide.Single,
    IReadOnlyList<string>? SidesReceived = null);

public enum SelfieStepStatus
{
    Done = 1,

    ConsentRequired = 2,

    DocumentRequired = 3,

    DocumentPhotoRequired = 4,

    DocumentPhotoMismatch = 5,
    ChallengeRequired = 6,
}

/// <summary>
/// The answer of the selfie step (AC-NF50-1, AC-NF50-2): <c>ok</c>, <c>retry</c> or <c>operator</c>, the guidance codes, the attempts left and,
/// only on <c>ok</c>, the lock code of the property (null when none is set or when it is held back). <c>LockCodeHeld</c> is why an <c>ok</c> came with no lock
/// code although the property has one (<see cref="AccessCodeHoldReasons"/>, E25/F01/US01/T01): the clear state the page shows instead of the code; null otherwise.
/// </summary>
public sealed record SelfieStepResult(
    SelfieStepStatus Status,
    string Result,
    IReadOnlyList<string> GuidanceCodes,
    int AttemptsRemaining,
    string? LockCode,
    string? LockCodeHeld = null)
{
    public bool OperatorPathOpen => true;
}

public sealed record SelfieChallenge(string ChallengeToken, bool NoseToRight, DateTimeOffset ExpiresAt);
