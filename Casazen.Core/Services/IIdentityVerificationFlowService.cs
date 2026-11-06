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
    /// <paramref name="consentVersion"/> (required, at most <see cref="BiometricConsentRules.VersionMaxLength"/> characters); false records the
    /// refusal and hands the verification to an operator, who is alerted at once. When the biometric verification is not offered (switched off
    /// for the organization, or the guest is a minor) nothing is recorded and the operator takes over.
    /// </summary>
    Task<BiometricConsentResult> RecordConsentAsync(GuestCheckInSession session, bool consentGiven, string? consentVersion, CancellationToken ct);

    /// <summary>
    /// Reads the document (the MRZ, then the printed fields) with the capture guidance of the agent, checks it against the check-in data of the
    /// guest who must verify (flags only, kept on the session) and, outside Ticino and after the consent, keeps its photo encrypted so the selfie
    /// can be compared without sending it again.
    /// </summary>
    Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, CancellationToken ct);

    /// <summary>
    /// Runs the agent on the selfie, in the request (ADR-013-R1), logs the outcome and updates the session; on <c>ok</c> releases the lock code
    /// (shown and e-mailed), on a review, the last failed attempt or an error hands the verification to an operator. For a property in Ticino the
    /// photo of the document comes again with the selfie (<paramref name="documentPhoto"/>) and must be the one read.
    /// </summary>
    Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto, CancellationToken ct);

    /// <summary>The guest asks for a person, at any step, also after a retry or a failed attempt (AC-NF51-11): the operators are alerted at once.</summary>
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

    /// <summary>The verification passed.</summary>
    public const string Done = "done";

    /// <summary>A person verifies the identity: the guest waits for the operator.</summary>
    public const string Operator = "operator";
}

/// <summary>
/// Where the verification of a session stands: the <paramref name="Step"/> of <see cref="IdentityVerificationSteps"/>, the last
/// <paramref name="Result"/> (<c>ok</c>, <c>retry</c>, <c>operator</c>, or null before any), whether the biometric verification is offered,
/// the consent, whether the document was checked, the attempts left, whether an operator was asked, whether the property keeps no document
/// (Ticino: <paramref name="DocumentNotArchived"/>, ADR-013-R15) and the position of the guest who must verify (the others skip the step).
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
    int VerifyingGuestPosition)
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
}

/// <summary>The answer to the consent and the state after it.</summary>
public sealed record BiometricConsentResult(BiometricConsentStatus Status, IdentityVerificationState State);

/// <summary>
/// The reading of the document (the fields for the guest to confirm), whether the capture is good enough, the guidance codes, whether its photo
/// was kept (encrypted) for the comparison and whether the property keeps no document (Ticino).
/// </summary>
public sealed record DocumentStepResult(
    DocumentReading Reading,
    bool Acceptable,
    IReadOnlyList<string> GuidanceCodes,
    bool DocumentStored,
    bool DocumentNotArchived);

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
}

/// <summary>
/// The answer of the selfie step (AC-NF50-1, AC-NF50-2): <c>ok</c>, <c>retry</c> or <c>operator</c>, the guidance codes, the attempts left and,
/// only on <c>ok</c>, the lock code of the property (null when none is set).
/// </summary>
public sealed record SelfieStepResult(
    SelfieStepStatus Status,
    string Result,
    IReadOnlyList<string> GuidanceCodes,
    int AttemptsRemaining,
    string? LockCode)
{
    /// <summary>The way to a person is always open.</summary>
    public bool OperatorPathOpen => true;
}
