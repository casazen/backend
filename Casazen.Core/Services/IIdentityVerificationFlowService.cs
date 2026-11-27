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
    /// <summary>Where the verification of the session stands (the page asks it with the session).</summary>
    Task<IdentityVerificationState> GetStateAsync(GuestCheckInSession session, CancellationToken ct);

    /// <summary>
    /// Records the guest's answer to the biometric consent: <paramref name="consentGiven"/> true records the time and the
    /// <paramref name="consentVersion"/> (required, at most <see cref="BiometricConsentRules.VersionMaxLength"/> characters, and the one the registry of the legal texts
    /// serves now, <see cref="ICheckInLegalTexts"/>: any other string is <see cref="BiometricConsentStatus.VersionUnknown"/>, E22/F01/US02/T02) together with the hash of that text;
    /// false records the refusal and hands the verification to an operator, who is alerted at once. When the biometric verification is not offered (switched off
    /// for the organization, the guest is a minor, or the registry serves no text of the consent: <c>legal_text_not_approved</c>) nothing is recorded and the operator takes over.
    /// </summary>
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

/// <summary>The limits of the biometric consent.</summary>
public static class BiometricConsentRules
{
    /// <summary>The longest version of the consent text (<c>GuestCheckInSession.BiometricConsentVersion</c>).</summary>
    public const int VersionMaxLength = 50;
}

/// <summary>The steps of the verification, as the page shows them.</summary>
public static class IdentityVerificationSteps
{
    /// <summary>The biometric consent is to be asked.</summary>
    public const string Consent = "consent";

    /// <summary>The document is to be photographed.</summary>
    public const string Document = "document";

    /// <summary>The selfie is to be taken.</summary>
    public const string Selfie = "selfie";

    /// <summary>
    /// The presence is to be proven (the self-hosted flow, <c>Features:CheckInIdentitySelfHosted</c>, E70-E74): a challenge and a sequence of frames take the
    /// place of the selfie.
    /// </summary>
    public const string Presence = "presence";

    /// <summary>The verification passed.</summary>
    public const string Done = "done";

    /// <summary>A person verifies the identity: the guest waits for the operator.</summary>
    public const string Operator = "operator";
}

/// <summary>
/// Which side of the identity document one picture is (E22/F01/US01/T02). A passport has the face and the MRZ on one page; the Italian electronic
/// identity card and the other TD1 cards have the face on the front and the MRZ on the back.
/// </summary>
public enum DocumentSide
{
    /// <summary>One picture holds the face and the MRZ, the behaviour before the two-sided document: a passport. The default of the endpoint.</summary>
    Single = 0,

    /// <summary>The front: the photo of the face, from which the selfie is compared (and the MRZ, when the document has it there).</summary>
    Front = 1,

    /// <summary>The back: the MRZ. No face is looked for on it.</summary>
    Back = 2,
}

/// <summary>The codes of <see cref="DocumentSide"/> the endpoint speaks (<c>side</c> of <c>POST api/public/checkin/{token}/document</c>).</summary>
public static class DocumentSides
{
    public const string Single = "single";
    public const string Front = "front";
    public const string Back = "back";

    /// <summary>The side a client named: nothing (or a blank value) is <see cref="DocumentSide.Single"/>; anything but <c>front</c>, <c>back</c> and <c>single</c> is not a side.</summary>
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

    /// <summary>The code of a side, as the endpoint answers it.</summary>
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
    /// <summary>The way to a person is always open.</summary>
    public bool OperatorPathOpen => true;
}

/// <summary>What the answer to the consent did.</summary>
public enum BiometricConsentStatus
{
    /// <summary>The consent is recorded with its version and time.</summary>
    Recorded = 1,

    /// <summary>The refusal is recorded; the operator takes over.</summary>
    Refused = 2,

    /// <summary>The biometric verification is not offered (switched off, a minor) or the verification is already with the operator.</summary>
    Operator = 3,

    /// <summary>A consent without its version: nothing recorded.</summary>
    VersionRequired = 4,

    /// <summary>
    /// A consent for a version that the registry of the legal texts does not serve now (a free string, a version of the past, a draft in production): nothing recorded
    /// (E22/F01/US02/T02, 422 <c>biometric_consent_version_unknown</c>). Never returned while the flow has no registry.
    /// </summary>
    VersionUnknown = 5,
}

/// <summary>The answer to the consent and the state after it.</summary>
public sealed record BiometricConsentResult(BiometricConsentStatus Status, IdentityVerificationState State);

/// <summary>What the opening of the identity step did (<see cref="IIdentityVerificationFlowService.OpenIdentityAsync"/>).</summary>
public enum IdentityOpenStatus
{
    /// <summary>The state answers: the step was opened now, or before (nothing changed), or not at all because the verification is over (see <see cref="IdentityVerificationState.Opened"/>).</summary>
    Answered = 1,

    /// <summary>The notice named is not the version of the biometric consent the registry serves now: nothing recorded (422 <c>notice_version_unknown</c>).</summary>
    NoticeVersionUnknown = 2,

    /// <summary>The verification of the session is closed: no new step (409 <c>identity_already_closed</c>, with the state).</summary>
    Closed = 3,

    /// <summary>The session expired or got a new link while the request waited for it: nothing recorded (404).</summary>
    SessionUnavailable = 4,
}

/// <summary>The opening of the identity step: what it did and the state after it (null only for <see cref="IdentityOpenStatus.SessionUnavailable"/>).</summary>
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

/// <summary>Why a selfie was not run.</summary>
public enum SelfieStepStatus
{
    /// <summary>The step answered: see <see cref="SelfieStepResult.Result"/>.</summary>
    Done = 1,

    /// <summary>No biometric consent recorded (409 <c>biometric_consent_required</c>).</summary>
    ConsentRequired = 2,

    /// <summary>No document checked yet, or its photo is no longer kept (409 <c>document_check_required</c>).</summary>
    DocumentRequired = 3,

    /// <summary>A property in Ticino keeps no document: its photo must come with the selfie (400).</summary>
    DocumentPhotoRequired = 4,

    /// <summary>The photo of the document sent with the selfie is not the one that was read (409).</summary>
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
    /// <summary>The way to a person is always open.</summary>
    public bool OperatorPathOpen => true;
}

/// <summary>The direction refers to the unmirrored image sent to the server.</summary>
public sealed record SelfieChallenge(string ChallengeToken, bool NoseToRight, DateTimeOffset ExpiresAt);
