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
    Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct);

    Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct);
}

public enum CaptureKind
{
    Selfie = 1,

    Document = 2,

    DocumentBack = 3,
}

/// <summary>
/// The assessment of one capture: <paramref name="Acceptable"/> when it can be compared, the <paramref name="GuidanceCodes"/> of what to change
/// otherwise, the static <paramref name="Tips"/> (glasses, masks: the models cannot detect them, so they are shown as advice, never as an
/// issue found), and <paramref name="Assessed"/> false when the checks could not run (models not available).
/// </summary>
public sealed record CaptureAssessment(bool Acceptable, IReadOnlyList<string> GuidanceCodes, IReadOnlyList<string> Tips, bool Assessed)
{
    public static CaptureAssessment NotAssessed { get; } = new(false, [], [], false);
}

public static class CaptureGuidanceCodes
{
    public const string ShowYourFace = "show_your_face";

    public const string OneFaceOnly = "one_face_only";

    public const string MoveCloser = "move_closer";

    public const string MoreLight = "more_light";

    public const string LessLight = "less_light";

    public const string HoldStill = "hold_still";

    public const string AvoidGlare = "avoid_glare";

    public const string ShowWholeDocument = "show_whole_document";

    public const string ShowDocumentPhoto = "show_document_photo";

    public const string RetakePicture = "retake_picture";

    public const string UseLiveCamera = "use_live_camera";

    public const string RetakeSelfie = "retake_selfie";

    public const string UploadDocumentAgain = "upload_document_again";

    public const string UseValidDocument = "use_valid_document";

    public const string TipRemoveGlasses = "tip_remove_glasses";

    public const string TipRemoveMask = "tip_remove_mask";

    public const string DocumentBackNeeded = "document_back_needed";

    public const string DocumentFrontNeeded = "document_front_needed";

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
    public bool Valid => MrzRead && CheckDigitsValid && Expired == false;

    public bool Consistent =>
        NamesConsistent == true && BirthDateConsistent != false && DocumentNumberConsistent != false && NationalityConsistent != false;
}

public sealed record VerificationPolicy(double AcceptThreshold, double EscalateThreshold, int MaxAttempts, bool Provisional);

public sealed record IdentityVerificationRequest(
    Stream Selfie,
    Stream DocumentPhoto,
    DocumentCheck Document,
    VerificationPolicy Policy,
    int Attempt);

public enum IdentityVerificationResult
{
    Ok = 1,

    Review = 2,

    NotOk = 3,

    Recapture = 4,
}

public static class IdentityVerificationGuestResults
{
    public const string Ok = "ok";
    public const string Retry = "retry";
    public const string Operator = "operator";
}

public sealed record VerificationThresholds(
    double Accept,
    double Escalate,
    double Liveness,
    int MaxAttempts,
    string CaptureRules,
    bool Provisional)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
    public bool OperatorPathOpen => true;

    public bool Provisional => Thresholds.Provisional;

    public bool TechnicalFailure => Reasons.Any(r => r.Check == VerificationReasons.Checks.Processing);

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
