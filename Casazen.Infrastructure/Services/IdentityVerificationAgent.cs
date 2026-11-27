using System.Diagnostics;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IIdentityVerificationAgent" />
/// <remarks>
/// <para><b>Order of <see cref="VerifyAsync"/></b> (CHK-14, NF-51): the quality of the selfie, before any comparison (a selfie that is not good
/// enough is a <see cref="IdentityVerificationResult.Recapture"/>: nothing compared, the attempt does not count); then the ADR-013 pipeline
/// (<see cref="IBiometricVerificationService"/>: faces, anti-spoofing, embedding, similarity) against the photo of the document; then the checks of
/// the document made at its upload (<see cref="DocumentCheck"/>: MRZ read, check digits, expiry, consistency with the check-in data).</para>
/// <para><b>Decision</b>: <c>Ok</c> only when every check passed. <c>Review</c> (a person decides) when the similarity is between the thresholds,
/// the MRZ disagrees with the check-in data, the document is still unreadable at the last attempt, or the models could not run. <c>NotOk</c> (a
/// new attempt, the operator after the last one) when the selfie is not live, the similarity is at or under the escalate threshold, the document
/// is expired, or it is unreadable before the last attempt. A review wins over a failed attempt: a new attempt would not fix it.</para>
/// <para><b>Privacy</b>: no logger, no database, no storage, no queue, no AI provider among the dependencies. The selfie is read into one array
/// that is zeroed in a <c>finally</c>, its decoded picture is disposed (zeroed) right after the quality check, and the outcome carries codes,
/// numbers and thresholds only.</para>
/// </remarks>
public sealed class IdentityVerificationAgent(
    IBiometricVerificationService biometrics,
    CaptureQualityAssessor assessor,
    IOptions<BiometricModelOptions> options) : IIdentityVerificationAgent
{
    public async Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var settings = options.Value;
        byte[]? bytes = null;
        RasterImage? picture = null;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(image, settings.MaxImageBytes, ct);
            try
            {
                picture = RasterImage.Decode(bytes, settings.MaxImagePixels);
            }
            catch (InvalidDataException)
            {
                return new CaptureAssessment(false, [CaptureGuidanceCodes.RetakePicture], [], true);
            }

            ImageBytes.Zero(bytes);
            return assessor.Assess(picture, kind, ct);
        }
        catch (InvalidDataException)
        {
            // Larger than the limit: it cannot be read.
            return new CaptureAssessment(false, [CaptureGuidanceCodes.RetakePicture], [], true);
        }
        finally
        {
            ImageBytes.Zero(bytes);
            picture?.Dispose();
        }
    }

    public async Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var settings = options.Value;
        var policy = request.Policy;
        var thresholds = new VerificationThresholds(
            policy.AcceptThreshold,
            policy.EscalateThreshold,
            settings.LivenessThreshold,
            policy.MaxAttempts,
            CaptureQualityRules.Version,
            policy.Provisional);
        var lastAttempt = request.Attempt >= policy.MaxAttempts;
        var model = ModelOf(settings);

        // ADR-013-R9: a minor is never processed; the request should not even have come here.
        if (request.Document.HolderIsMinor)
        {
            return new IdentityVerificationOutcome(
                IdentityVerificationResult.Review,
                [new VerificationReason(VerificationReasons.Checks.DocumentValidity, VerificationReasons.Results.Failed)],
                [],
                lastAttempt,
                model.Name,
                model.Version,
                thresholds,
                Elapsed(started));
        }

        byte[]? selfieBytes = null;
        try
        {
            // 1. The quality of the selfie, before any comparison (AC-NF51-2).
            CaptureAssessment quality;
            try
            {
                selfieBytes = await ImageBytes.ReadAllAsync(request.Selfie, settings.MaxImageBytes, ct);
                using var selfie = RasterImage.Decode(selfieBytes, settings.MaxImagePixels);
                quality = assessor.Assess(selfie, CaptureKind.Selfie, ct);
            }
            catch (InvalidDataException)
            {
                quality = new CaptureAssessment(false, [CaptureGuidanceCodes.RetakePicture], [], true);
            }

            if (!quality.Assessed)
            {
                return Outcome(
                    IdentityVerificationResult.Review,
                    [Processing(VerificationReasons.Results.Unavailable)],
                    [],
                    lastAttempt,
                    model,
                    thresholds,
                    started);
            }

            if (!quality.Acceptable || selfieBytes is null)
            {
                return Outcome(
                    IdentityVerificationResult.Recapture,
                    [new VerificationReason(VerificationReasons.Checks.Quality, VerificationReasons.Results.Failed)],
                    quality.GuidanceCodes,
                    lastAttempt,
                    model,
                    thresholds,
                    started);
            }

            // 2. The ADR-013 pipeline against the photo of the document: liveness and similarity, in memory, zeroed by the service.
            BiometricVerificationResult face;
            using (var copy = new MemoryStream(selfieBytes, writable: false))
            {
                face = await biometrics.VerifyAsync(copy, request.DocumentPhoto, policy.AcceptThreshold, policy.EscalateThreshold, ct);
            }

            ImageBytes.Zero(selfieBytes);
            return Decide(face, request.Document, policy, thresholds, lastAttempt, started);
        }
        finally
        {
            ImageBytes.Zero(selfieBytes);
        }
    }

    private static IdentityVerificationOutcome Decide(
        BiometricVerificationResult face,
        DocumentCheck document,
        VerificationPolicy policy,
        VerificationThresholds thresholds,
        bool lastAttempt,
        long started)
    {
        var model = (Name: face.ModelName, Version: face.ModelVersion);
        var reasons = new List<VerificationReason> { new(VerificationReasons.Checks.Quality, VerificationReasons.Results.Passed) };
        var guidance = new List<string>();
        var code = face.ReasonCode;

        // The models could not run: the safe default is a person (as the pipeline itself does).
        if (code is BiometricReasonCodes.Unavailable or BiometricReasonCodes.Timeout or BiometricReasonCodes.Error
            or BiometricReasonCodes.ThresholdsInvalid)
        {
            reasons.Add(Processing(code switch
            {
                BiometricReasonCodes.Timeout => VerificationReasons.Results.Timeout,
                BiometricReasonCodes.Unavailable => VerificationReasons.Results.Unavailable,
                _ => VerificationReasons.Results.Error,
            }));
            AddDocumentReasons(reasons, document);
            return Outcome(IdentityVerificationResult.Review, reasons, [], lastAttempt, model, thresholds, started);
        }

        // A face the pipeline did not find, or a picture it could not read: nothing was compared, the capture is taken again.
        if (code is BiometricReasonCodes.NoFace or BiometricReasonCodes.MultipleFaces or BiometricReasonCodes.FaceTooSmall
            or BiometricReasonCodes.ImageUnreadable)
        {
            reasons[0] = new VerificationReason(VerificationReasons.Checks.Quality, code);
            guidance.Add(code switch
            {
                BiometricReasonCodes.NoFace when !face.FaceInDocument => CaptureGuidanceCodes.UploadDocumentAgain,
                BiometricReasonCodes.NoFace => CaptureGuidanceCodes.ShowYourFace,
                BiometricReasonCodes.MultipleFaces => CaptureGuidanceCodes.OneFaceOnly,
                BiometricReasonCodes.FaceTooSmall => CaptureGuidanceCodes.MoveCloser,
                _ => CaptureGuidanceCodes.RetakePicture,
            });
            return Outcome(IdentityVerificationResult.Recapture, reasons, guidance, lastAttempt, model, thresholds, started);
        }

        var notOk = false;
        var review = false;

        // Liveness (AC-NF51-4): the score of the real-face class against Biometrics:LivenessThreshold.
        var live = face.IsLive && code != BiometricReasonCodes.NotLive;
        reasons.Add(new VerificationReason(
            VerificationReasons.Checks.Liveness,
            live ? VerificationReasons.Results.Passed : VerificationReasons.Results.Failed,
            Round(face.LivenessScore),
            thresholds.Liveness));
        if (!live)
        {
            notOk = true;
            guidance.Add(CaptureGuidanceCodes.UseLiveCamera);
        }

        // The document: read, valid and not expired.
        var documentResult = DocumentValidity(document);
        reasons.Add(new VerificationReason(VerificationReasons.Checks.DocumentValidity, documentResult));
        if (documentResult == VerificationReasons.Results.Expired)
        {
            notOk = true;
            guidance.Add(CaptureGuidanceCodes.UseValidDocument);
        }
        else if (documentResult != VerificationReasons.Results.Passed)
        {
            // Unreadable or wrong check digits: a new photo of the document can fix it, until the last attempt.
            if (lastAttempt)
            {
                review = true;
            }
            else
            {
                notOk = true;
                guidance.Add(CaptureGuidanceCodes.UploadDocumentAgain);
            }
        }

        // The MRZ against the check-in data: a disagreement is for a person, a new selfie would not change it.
        var mismatches = Mismatches(document);
        if (!document.MrzRead)
            reasons.Add(new VerificationReason(VerificationReasons.Checks.MrzConsistency, VerificationReasons.Results.NotRun));
        else if (mismatches.Count == 0)
            reasons.Add(new VerificationReason(VerificationReasons.Checks.MrzConsistency, VerificationReasons.Results.Passed));
        else
        {
            review = true;
            reasons.AddRange(mismatches.Select(result => new VerificationReason(VerificationReasons.Checks.MrzConsistency, result)));
        }

        // The similarity against the thresholds of the organization (ADR-013-R4).
        if (!live)
        {
            reasons.Add(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.NotRun, Threshold: policy.AcceptThreshold));
        }
        else
        {
            var similarity = Round(face.Similarity);
            if (face.Similarity >= policy.AcceptThreshold)
            {
                reasons.Add(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Passed, similarity, policy.AcceptThreshold));
            }
            else if (face.Similarity > policy.EscalateThreshold)
            {
                review = true;
                reasons.Add(new VerificationReason(
                    VerificationReasons.Checks.Similarity, VerificationReasons.Results.BetweenThresholds, similarity, policy.AcceptThreshold));
            }
            else
            {
                notOk = true;
                guidance.Add(CaptureGuidanceCodes.RetakeSelfie);
                reasons.Add(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, similarity, policy.EscalateThreshold));
            }
        }

        var result = review
            ? IdentityVerificationResult.Review
            : notOk ? IdentityVerificationResult.NotOk : IdentityVerificationResult.Ok;

        // Ok only with every check passed: a defence against a future check that forgets to set a flag.
        if (result == IdentityVerificationResult.Ok && reasons.Any(r => r.Result != VerificationReasons.Results.Passed))
            result = IdentityVerificationResult.Review;

        return Outcome(result, reasons, result == IdentityVerificationResult.NotOk ? guidance : [], lastAttempt, model, thresholds, started);
    }

    private static string DocumentValidity(DocumentCheck document)
    {
        if (!document.MrzRead)
            return VerificationReasons.Results.Unreadable;
        if (!document.CheckDigitsValid)
            return VerificationReasons.Results.CheckDigitsInvalid;
        return document.Expired switch
        {
            true => VerificationReasons.Results.Expired,
            false => VerificationReasons.Results.Passed,

            // An expiry the MRZ did not give reliably: the document is not proved valid.
            null => VerificationReasons.Results.Unreadable,
        };
    }

    private static List<string> Mismatches(DocumentCheck document)
    {
        var mismatches = new List<string>();
        if (document.NamesConsistent != true)
            mismatches.Add(VerificationReasons.Results.NamesMismatch);
        if (document.BirthDateConsistent == false)
            mismatches.Add(VerificationReasons.Results.BirthDateMismatch);
        if (document.DocumentNumberConsistent == false)
            mismatches.Add(VerificationReasons.Results.DocumentNumberMismatch);
        if (document.NationalityConsistent == false)
            mismatches.Add(VerificationReasons.Results.NationalityMismatch);
        return mismatches;
    }

    private static void AddDocumentReasons(List<VerificationReason> reasons, DocumentCheck document)
    {
        reasons.Add(new VerificationReason(VerificationReasons.Checks.DocumentValidity, DocumentValidity(document)));
        var mismatches = Mismatches(document);
        if (!document.MrzRead)
            reasons.Add(new VerificationReason(VerificationReasons.Checks.MrzConsistency, VerificationReasons.Results.NotRun));
        else if (mismatches.Count == 0)
            reasons.Add(new VerificationReason(VerificationReasons.Checks.MrzConsistency, VerificationReasons.Results.Passed));
        else
            reasons.AddRange(mismatches.Select(result => new VerificationReason(VerificationReasons.Checks.MrzConsistency, result)));
    }

    private static VerificationReason Processing(string result) => new(VerificationReasons.Checks.Processing, result);

    private static IdentityVerificationOutcome Outcome(
        IdentityVerificationResult result,
        IReadOnlyList<VerificationReason> reasons,
        IReadOnlyList<string> guidance,
        bool lastAttempt,
        (string Name, string Version) model,
        VerificationThresholds thresholds,
        long started) =>
        new(
            result,
            reasons,
            guidance.Distinct(StringComparer.Ordinal).ToList(),
            lastAttempt,
            string.IsNullOrWhiteSpace(model.Name) ? "unknown" : model.Name,
            string.IsNullOrWhiteSpace(model.Version) ? "unknown" : model.Version,
            thresholds,
            Elapsed(started));

    private static (string Name, string Version) ModelOf(BiometricModelOptions settings) =>
        (settings.FaceRecognition.Name, settings.FaceRecognition.Version);

    private static int Elapsed(long started) => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static double Round(double value) => Math.Round(value, 4);
}
