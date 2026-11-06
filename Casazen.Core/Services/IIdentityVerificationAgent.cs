using System.Text.Json;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The identity verification agent of the guest check-in (CHK-14, E03/F04/US02/T02, NF-51): on top of the ADR-013 pipeline, it guides the
/// captures, checks their quality, runs the checks of the models and of the MRZ and evaluates one verifiable decision. Deterministic and
/// rule-based with versioned thresholds: it calls no language model and no AI provider (PO-09), so no picture and no personal data can reach
/// one; it writes nothing (no log, no queue, no storage). Its texts are stable codes the clients translate.
/// </summary>
public interface IIdentityVerificationAgent
{
    /// <summary>
    /// The live guidance of one capture (AC-NF51-1, AC-NF51-2): whether it is good enough to be compared and, when it is not, what to change
    /// (<see cref="CaptureGuidanceCodes"/>). The picture is read in memory and zeroed before returning. When the face models are not available
    /// the assessment is <see cref="CaptureAssessment.Assessed"/> false.
    /// </summary>
    Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct);

    /// <summary>
    /// The decision on one attempt (AC-NF51-3 to AC-NF51-14): quality, liveness, validity of the document, consistency of the MRZ with the
    /// check-in data, similarity, in this order. <see cref="IdentityVerificationResult.Ok"/> only when every check passed.
    /// </summary>
    Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct);
}

/// <summary>What a capture is.</summary>
public enum CaptureKind
{
    /// <summary>The face of the guest, from the camera.</summary>
    Selfie = 1,

    /// <summary>The identity document, the side with the photo and the MRZ.</summary>
    Document = 2,
}

/// <summary>
/// The assessment of one capture: <paramref name="Acceptable"/> when it can be compared, the <paramref name="GuidanceCodes"/> of what to change
/// otherwise, the static <paramref name="Tips"/> (glasses, masks: the models cannot detect them, so they are shown as advice, never as an
/// issue found), and <paramref name="Assessed"/> false when the checks could not run (models not available).
/// </summary>
public sealed record CaptureAssessment(bool Acceptable, IReadOnlyList<string> GuidanceCodes, IReadOnlyList<string> Tips, bool Assessed)
{
    /// <summary>Nothing could be checked: the face models are not available.</summary>
    public static CaptureAssessment NotAssessed { get; } = new(false, [], [], false);
}

/// <summary>The guidance codes of a capture (codes only; the page translates them).</summary>
public static class CaptureGuidanceCodes
{
    /// <summary>No face found in the selfie.</summary>
    public const string ShowYourFace = "show_your_face";

    /// <summary>More than one face in the selfie.</summary>
    public const string OneFaceOnly = "one_face_only";

    /// <summary>The face covers too little of the picture.</summary>
    public const string MoveCloser = "move_closer";

    /// <summary>The picture (or the face) is too dark.</summary>
    public const string MoreLight = "more_light";

    /// <summary>The picture (or the face) is too bright.</summary>
    public const string LessLight = "less_light";

    /// <summary>The picture is blurred: the hand moved or the focus is off.</summary>
    public const string HoldStill = "hold_still";

    /// <summary>A reflection hides part of the document.</summary>
    public const string AvoidGlare = "avoid_glare";

    /// <summary>The MRZ of the document is not in the picture: show the whole side with the photo.</summary>
    public const string ShowWholeDocument = "show_whole_document";

    /// <summary>The photo of the face on the document is not found.</summary>
    public const string ShowDocumentPhoto = "show_document_photo";

    /// <summary>The picture cannot be read (not a JPEG or PNG, damaged, too large).</summary>
    public const string RetakePicture = "retake_picture";

    /// <summary>The selfie does not look like a live capture: take it with the camera, not from a photo or a screen.</summary>
    public const string UseLiveCamera = "use_live_camera";

    /// <summary>The selfie and the document photo do not match well enough: take the selfie again, facing the camera.</summary>
    public const string RetakeSelfie = "retake_selfie";

    /// <summary>The document could not be read or its check digits are wrong: photograph it again.</summary>
    public const string UploadDocumentAgain = "upload_document_again";

    /// <summary>The document is expired: use a valid one.</summary>
    public const string UseValidDocument = "use_valid_document";

    /// <summary>Static tip of the selfie: take off the glasses (not detected by the models).</summary>
    public const string TipRemoveGlasses = "tip_remove_glasses";

    /// <summary>Static tip of the selfie: take off a mask or anything that covers the face (not detected by the models).</summary>
    public const string TipRemoveMask = "tip_remove_mask";

    /// <summary>The guidance of the quality hints of an MRZ reading (<see cref="MrzQualityCodes"/>).</summary>
    public static IReadOnlyList<string> FromMrzQuality(IEnumerable<string> hints) =>
        hints.Select(hint => hint switch
            {
                MrzQualityCodes.MrzNotFound => ShowWholeDocument,
                MrzQualityCodes.Glare => AvoidGlare,
                MrzQualityCodes.Blur => HoldStill,
                _ => null,
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// The checks of the identity document, as they were made when it was uploaded (E03/F04/US02/T04): whether the MRZ was read, its check digits,
/// its expiry, the consistency of each field with the check-in data (null = not compared: the check-in has no value for it) and whether its
/// holder is a minor. Flags only, never a value read on the document.
/// </summary>
public sealed record DocumentCheck(
    bool MrzRead,
    bool CheckDigitsValid,
    bool? Expired,
    bool? NamesConsistent,
    bool? BirthDateConsistent,
    bool? DocumentNumberConsistent,
    bool? NationalityConsistent,
    bool HolderIsMinor)
{
    /// <summary>Read from its MRZ, check digits valid, not expired (an unknown expiry is not valid).</summary>
    public bool Valid => MrzRead && CheckDigitsValid && Expired == false;

    /// <summary>The names agree, and no compared field disagrees (a field not compared does not count).</summary>
    public bool Consistent =>
        NamesConsistent == true && BirthDateConsistent != false && DocumentNumberConsistent != false && NationalityConsistent != false;
}

/// <summary>
/// The thresholds in force for an organization (<c>BiometricSettings</c>, or its defaults): accept and escalate thresholds of the similarity, the
/// attempts, and <paramref name="Provisional"/> when they were never calibrated (<c>CalibratedAt</c> null).
/// </summary>
public sealed record VerificationPolicy(double AcceptThreshold, double EscalateThreshold, int MaxAttempts, bool Provisional);

/// <summary>
/// One attempt: the selfie, the photo of the document (the one checked at its upload), the checks of that document, the policy and the number
/// of this attempt (1-based). The streams belong to the caller and are not kept.
/// </summary>
public sealed record IdentityVerificationRequest(
    Stream Selfie,
    Stream DocumentPhoto,
    DocumentCheck Document,
    VerificationPolicy Policy,
    int Attempt);

/// <summary>The decision of one attempt.</summary>
public enum IdentityVerificationResult
{
    /// <summary>Every check passed (<see cref="BiometricOutcome.Accept"/>).</summary>
    Ok = 1,

    /// <summary>A person decides (<see cref="BiometricOutcome.Escalate"/>).</summary>
    Review = 2,

    /// <summary>Not passed: a new attempt, the operator after the last one (<see cref="BiometricOutcome.Reject"/>).</summary>
    NotOk = 3,

    /// <summary>The selfie is not good enough to be compared: take it again; nothing was compared and the attempt does not count.</summary>
    Recapture = 4,
}

/// <summary>The plain results the guest sees.</summary>
public static class IdentityVerificationGuestResults
{
    public const string Ok = "ok";
    public const string Retry = "retry";
    public const string Operator = "operator";
}

/// <summary>The thresholds an outcome was decided with, as they are kept in <c>BiometricVerificationLog.ThresholdsJson</c>.</summary>
public sealed record VerificationThresholds(
    double Accept,
    double Escalate,
    double Liveness,
    int MaxAttempts,
    string CaptureRules,
    bool Provisional)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON object of the log.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// The outcome of one attempt (AC-NF51-4, AC-NF51-5, AC-NF51-7, AC-NF51-8): the result, its mapping to the three-level outcome of ADR-013, the
/// reasons for the operator (codes, values, thresholds), the guidance codes for the guest, whether this was the last attempt, the model and its
/// version, the thresholds in force and whether they are provisional, the duration. <see cref="OperatorPathOpen"/> is always true: a
/// <c>NotOk</c> or a <c>Review</c> never closes the way to a person (GDPR art. 22, AC-NF51-11).
/// </summary>
public sealed record IdentityVerificationOutcome(
    IdentityVerificationResult Result,
    IReadOnlyList<VerificationReason> Reasons,
    IReadOnlyList<string> GuidanceCodes,
    bool LastAttempt,
    string ModelName,
    string ModelVersion,
    VerificationThresholds Thresholds,
    int DurationMs)
{
    /// <summary>The way to a person is never closed by the agent.</summary>
    public bool OperatorPathOpen => true;

    /// <summary>The thresholds were not calibrated.</summary>
    public bool Provisional => Thresholds.Provisional;

    /// <summary>The verification could not run (models not available, timeout, error): the operator decides.</summary>
    public bool TechnicalFailure => Reasons.Any(r => r.Check == VerificationReasons.Checks.Processing);

    /// <summary>The three-level outcome of ADR-013-R4: Accept, Escalate, Reject; none for a recapture (nothing was compared).</summary>
    public BiometricOutcome? BiometricOutcome => Result switch
    {
        IdentityVerificationResult.Ok => Entities.Enums.BiometricOutcome.Accept,
        IdentityVerificationResult.Review => Entities.Enums.BiometricOutcome.Escalate,
        IdentityVerificationResult.NotOk => Entities.Enums.BiometricOutcome.Reject,
        _ => null,
    };

    /// <summary>What the guest is told: <c>ok</c>, <c>retry</c> (a new attempt or a new capture) or <c>operator</c>.</summary>
    public string GuestResult => Result switch
    {
        IdentityVerificationResult.Ok => IdentityVerificationGuestResults.Ok,
        IdentityVerificationResult.Review => IdentityVerificationGuestResults.Operator,
        IdentityVerificationResult.NotOk when LastAttempt => IdentityVerificationGuestResults.Operator,
        _ => IdentityVerificationGuestResults.Retry,
    };
}
