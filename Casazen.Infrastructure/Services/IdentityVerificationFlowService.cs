using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IIdentityVerificationFlowService" />
/// <remarks>
/// <para><b>Who verifies</b>: the first guest of the stay who carries the document (single guest, head of family or head of group,
/// <see cref="AlloggiatiRecordRules.RequiresDocument"/>), or the booker when nobody is registered yet. A minor (by the check-in data or by the
/// MRZ) is never processed: the operator takes over (ADR-013-R9).</para>
/// <para><b>Settings</b>: the <see cref="BiometricSettings"/> row of the org, or the ADR-013-R13 default (enabled on the Agency plan only,
/// thresholds 0.85 / 0.70, 3 attempts, provisional). Switched off: no biometric processing, the operator takes over.</para>
/// <para><b>Document</b>: read once per upload (the MRZ, then the printed fields). Its checks against the check-in data are kept on the session
/// as flags and the SHA-256 of the picture (<see cref="GuestCheckInSession.DocumentCheckJson"/>), never a value read. Outside Ticino, after the
/// consent, the picture is kept encrypted (Data Protection, purpose <see cref="DocumentImageContentPurpose"/>) in the private bucket with a
/// <see cref="DocumentImage"/> row, and deleted as soon as the verification is over (passed, or with the operator) or replaced. In Ticino nothing
/// is kept (ADR-013-R6) and the photo comes again with the selfie, recognised by its hash.</para>
/// <para><b>Two sides</b> (E22/F01/US01/T02): the Italian electronic identity card and the other TD1 cards have the face on the front and the MRZ on the back,
/// so the document step takes up to two pictures per attempt (<see cref="DocumentSide"/>): the face must come from the front (or a single picture), the MRZ
/// from any side, and the step is complete when both were found across them; the guidance says which side is missing. Both pictures follow the rules of
/// one: encrypted in the private bucket until the verification is over and then deleted, none kept in Ticino (where both come again with the selfie).
/// A side taken again is not a new attempt: the attempts are the complete document and selfie tries.</para>
/// <para><b>Lock code</b>: only on <c>ok</c>, through the gate of <see cref="IAccessCodeService"/> (channels <c>checkin-screen</c> and
/// <c>email</c>); a retry, a review, a refusal, a request for the operator or an error release and send nothing.</para>
/// <para><b>Booking, not only session</b>: a booking whose verification is with an operator (rejected by one, or a hand-off still open,
/// <see cref="VerificationHandOff"/>) answers <c>operator</c> in <b>any</b> session of it, a new link included: the daily jobs and the host replace
/// the session, never the decision of a person. The operator opens the way again by asking for a new photo from the queue.</para>
/// <para><b>Privacy</b>: the logs say the session, the step and the codes; never a picture, a value of the document or a name.</para>
/// <para><b>Self-hosted session</b> (E70/F02/US01/T01, <c>Features:CheckInIdentitySelfHosted</c>, the part <c>IdentityVerificationFlowService.Session.cs</c>): the
/// verification is opened once, bound to one guest, records the choice and is closed by its first hand-off to a person. Off, nothing of it is read or written.</para>
/// <para><b>Self-hosted document step</b> (E71/F01/US03/T01, the part <c>IdentityVerificationFlowService.DocumentChecks.cs</c>): on top of the reading of today every picture of the
/// document is checked for its geometry, its side and its format, refused with a targeted code when it is not good (a refusal is not an attempt, and after
/// <c>policy.document.maxRecapturesPerSide</c> of them a person decides), and the coherence of the sides is kept as flags. Off, nothing of it runs.</para>
/// <para><b>Self-hosted presence and closure</b> (E73/F01/US01/T02, the part <c>IdentityVerificationFlowService.Sequence.cs</c>): with the flow on, the step after the document is
/// <c>presence</c> (a challenge and a sequence of frames, decided by the versioned policy in one lock and one save, idempotent by its key) and the two-frame selfie of today is closed
/// (its routes answer <c>presence_sequence_required</c>). Off, nothing of it runs and the selfie is the step, as before.</para>
/// <para><b>Evidence of the reviewer</b> (E73/F01/US02/T01, <c>Features:CheckInIdentityReviewEvidence</c>): a review of the closure keeps the crop of the portrait and the frames the
/// server chose, encrypted and for a short time, before the kept pictures of the document go (<see cref="IdentityReviewEvidenceStore"/>); never in Ticino, never on an approval.</para>
/// </remarks>
public sealed partial class IdentityVerificationFlowService(
    AppDbContext db,
    IIdentityVerificationAgent agent,
    IVerificationOperatorAlerts alerts,
    IAccessCodeService accessCodes,
    IEmailQueue emailQueue,
    IMrzExtractionService mrz,
    IDocumentOcrService ocr,
    IStayGuestService stayGuests,
    IFileStorage storage,
    IDataProtectionProvider dataProtection,
    TimeProvider clock,
    ILogger<IdentityVerificationFlowService> logger,
    IDomainEventRecorder? domainEvents = null,
    ICheckInLegalTexts? legalTexts = null,
    IPropertySwissPlaceResolver? swissPlaces = null,
    IGuestEmailBrandingProvider? emailBranding = null,
    IActivePoseVerificationService? activePose = null,
    IFeatureFlags? featureFlags = null,
    IdentityPolicyProvider? identityPolicy = null,
    IBiometricModelStore? biometricModels = null,
    IOptions<IdentityVerificationOptions>? identityOptions = null,
    PresenceChallengeIssuer? presenceIssuer = null,
    DocumentPictureAnalyzer? documentPictures = null,
    DocumentSideClassifier? sideClassifier = null,
    DocumentCoherenceChecker? coherenceChecker = null,
    DocumentFormatRegistry? formats = null,
    FrameSequenceReader? frameReader = null,
    IPresenceEvidenceCollector? evidenceCollector = null,
    IIdentitySegmentService? segments = null,
    IdentityDecisionEngine? decisionEngine = null,
    IdentityReviewEvidenceStore? reviewEvidence = null,
    IOptions<MrzOcrOptions>? mrzOptions = null) : IIdentityVerificationFlowService
{
    public const string DocumentImageContentPurpose = "Casazen.DocumentImage.Content";

    public static readonly VerificationPolicy DefaultPolicy = new(0.85, 0.70, 3, Provisional: true);

    internal const int MaxImageBytes = 12_000_000;

    private const string StoredContentType = "application/octet-stream";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IdentityVerificationState> GetStateAsync(GuestCheckInSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = await ContextAsync(session, ct);
        return State(session, context);
    }

    public Task<BiometricConsentResult> RecordConsentAsync(GuestCheckInSession session, bool consentGiven, string? consentVersion, CancellationToken ct)
    {
        return RecordConsentAsync(session, consentGiven, consentVersion, null, ct);
    }

    public async Task<BiometricConsentResult> RecordConsentAsync(
        GuestCheckInSession session,
        bool consentGiven,
        string? consentVersion,
        string? channel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = await ContextAsync(session, ct);

        if (session.BiometricOutcome == BiometricOutcome.Accept)
            return new BiometricConsentResult(BiometricConsentStatus.Recorded, State(session, context));

        var now = UtcNow();
        if (!consentGiven)
        {
            NoteChoice(session, context, IdentityChoices.Assisted, consentVersion, channel, now);
            session.BiometricConsentRefusedAt = now;
            session.BiometricConsentAt = null;
            session.BiometricConsentTextHash = null;
            session.UpdatedAt = now;
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.ConsentRefused, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Refused, State(session, context));
        }

        var version = consentVersion?.Trim();
        if (string.IsNullOrEmpty(version) || version.Length > BiometricConsentRules.VersionMaxLength)
            return new BiometricConsentResult(BiometricConsentStatus.VersionRequired, State(session, context));

        NoteChoice(session, context, IdentityChoices.Automatic, version, channel, now);

        if (!context.Enabled)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.BiometricsDisabled, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        if (context.IsMinor || ReadCheck(session)?.Check.HolderIsMinor == true)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.MinorExcluded, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        if (context.BoundGuestMissing)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.Review, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        if (!context.ConsentTextServed)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.LegalTextNotApproved, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        // A consent refers to the version of the text the registry serves now, never to a free string; it keeps the hash of that text with it (GDPR art. 7(1)).
        var served = context.ActiveConsentText;
        if (served is null || !string.Equals(served.Version, version, StringComparison.Ordinal))
            return new BiometricConsentResult(BiometricConsentStatus.VersionUnknown, State(session, context));

        if (InOperatorPath(session, context))
        {
            await CloseOperatorPathAsync(session, context, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        session.BiometricConsentAt = now;
        session.BiometricConsentVersion = version;
        session.BiometricConsentTextHash = served.TextHash;
        if (ReadCheck(session) is { Pose: not null } prior)
            session.DocumentCheckJson = JsonSerializer.Serialize(prior with { Pose = null }, Json);
        session.BiometricConsentRefusedAt = null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Biometric consent recorded for session {SessionId}", session.Id);
        return new BiometricConsentResult(BiometricConsentStatus.Recorded, State(session, context));
    }

    public Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, CancellationToken ct)
    {
        return SubmitDocumentAsync(session, image, DocumentSide.Single, ct);
    }

    public async Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, DocumentSide side, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(image);

        // E71/F01/US03/T01: the document step of the self-hosted flow reads the record of the session, counts a refusal or keeps a side, and writes the record back. Two pictures of one
        // session at the same time (a double submit, a request the network repeated) must not both read the same record and lose an update, so the step runs under the lock of the row of
        // the session, reloaded under it, as the presence and the pose challenges do. Off, or built without the services of the step, nothing of it changes.
        if (!DocumentStepOn)
            return await ReadDocumentAsync(session, image, side, ct);

        var unavailable = new DocumentStepResult(DocumentReading.Nothing, false, [], false, false, side, []);
        return await WithLockedPoseSessionAsync(session, unavailable, () => ReadDocumentAsync(session, image, side, ct), ct);
    }

    private async Task<DocumentStepResult> ReadDocumentAsync(GuestCheckInSession session, Stream image, DocumentSide side, CancellationToken ct)
    {
        var context = await ContextAsync(session, ct);

        byte[]? bytes = null;
        OpenedDocumentPicture? opened = null;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(image, MaxImageBytes, ct);

            var consented = context.Enabled && HasCurrentConsent(session, context) && !context.BoundGuestMissing;

            CaptureAssessment? assessment = null;
            if (consented)
            {
                using var copy = new MemoryStream(bytes, writable: false);
                assessment = await agent.AssessCaptureAsync(copy, side == DocumentSide.Back ? CaptureKind.DocumentBack : CaptureKind.Document, ct);
            }

            MrzReading mrzReading;
            (mrzReading, opened) = await ReadUploadMrzAsync(session, bytes, side, consented, ct);

            DocumentReading reading;
            if (mrzReading.Result is { } fromMrz)
            {
                reading = FromMrz(fromMrz);
            }
            else if (side == DocumentSide.Back)
            {
                reading = DocumentReading.Nothing;
            }
            else
            {
                using var copy = new MemoryStream(bytes, writable: false);
                var fromOcr = await ocr.ExtractAsync(copy, ct);
                reading = fromOcr is { HasDocumentEvidence: true } ? FromOcr(fromOcr) : DocumentReading.Nothing;
            }

            DocumentPictureChecks? selfChecks = null;
            if (DocumentStepOn && consented)
            {
                var step = await CheckDocumentPictureAsync(session, context, bytes, side, mrzReading, reading, assessment, opened, ct);
                opened?.Dispose();
                opened = null;
                if (step.Answer is { } answer)
                    return answer;

                selfChecks = step.Checks;
            }

            if (reading.Method == DocumentReadingMethod.None)
            {
                var existing = ReadCheck(session);
                return new DocumentStepResult(reading, false, RejectionGuidance(assessment, mrzReading),
                    false, context.DocumentNotArchived, side,
                    existing is null ? [] : SidesOf(existing.TwoSided, existing.Front, existing.Back));
            }

            var now = UtcNow();

            var previous = ReadCheck(session);
            var proofKey = previous?.ProofKey ?? session.TokenHash;
            var picture = new StoredDocumentPicture(
                FaceOf(assessment, side),
                QualityOf(assessment),
                mrzReading.Result is null ? null : Check(mrzReading.Result, context.Identity),
                DocumentNumberProof(proofKey, reading.Fields?.DocumentNumber),
                selfChecks is null ? null : true,
                selfChecks?.ClassifiedSide,
                selfChecks?.DHash);

            var kept = side == DocumentSide.Single ? null : previous;
            StoredDocumentPicture? front;
            StoredDocumentPicture? back;
            string frontHash;
            string? backHash;
            switch (side)
            {
                case DocumentSide.Back:
                    front = kept?.Front;
                    frontHash = kept?.Front is null ? string.Empty : kept.PhotoSha256;
                    back = picture;
                    backHash = Sha256(bytes);
                    break;
                case DocumentSide.Front:
                    front = picture;
                    frontHash = Sha256(bytes);
                    var previousNumber = kept?.Back?.NumberProof ?? kept?.Front?.NumberProof;
                    var changedDocument = kept?.Front is not null && previousNumber is not null
                        && picture.NumberProof is not null && previousNumber != picture.NumberProof;
                    back = changedDocument ? null : kept?.Back;
                    backHash = back is null ? null : kept!.BackSha256;
                    if (changedDocument && kept!.Back is not null)
                        await DeleteStoredDocumentAsync(session, ct);
                    break;
                default:
                    front = picture;
                    frontHash = Sha256(bytes);
                    back = null;
                    backHash = null;
                    break;
            }

            var check = CombineChecks(front?.Mrz, back?.Mrz, side);
            if (front?.NumberProof is { } frontNumber && back?.NumberProof is { } backNumber && frontNumber != backNumber)
                check = check with { DocumentNumberConsistent = false };
            var mrzRead = front?.Mrz is not null || back?.Mrz is not null;
            var frontGood = front is not null && front.Face != false && front.QualityOk != false;
            var backGood = back is null || back.Mrz is not null || back.QualityOk != false;
            var acceptable = mrzRead && frontGood && backGood;

            var guidance = (assessment?.GuidanceCodes ?? [])
                .Concat(CaptureGuidanceCodes.FromMrzQuality(mrzReading.Quality))
                .ToList();
            AddMissingSideGuidance(guidance, side, mrzReading.Result is not null, mrzRead, frontGood);

            IReadOnlyList<DocumentCoherenceCheck>? coherence = null;
            if (selfChecks is not null)
            {
                if (selfChecks.FormatDetected)
                    session.IdentityFormatId = selfChecks.Format.Id;

                coherence = await CheckCoherenceAsync(session, context, selfChecks, side, mrzReading, front, back, ct);
            }

            var storedCheck = new StoredDocumentCheck(
                check, frontHash, now, backHash, front, back, side != DocumentSide.Single, context.VerifyingGuestPosition, proofKey,
                Refusals: selfChecks is null ? previous?.Refusals : WithoutSide(previous?.Refusals, side),
                Coherence: coherence,
                RefusedSha256: selfChecks is null ? previous?.RefusedSha256 : WithoutSide(previous?.RefusedSha256, side));

            if (selfChecks is not null)
                acceptable = acceptable && DocumentComplete(session, storedCheck);

            session.DocumentCheckJson = JsonSerializer.Serialize(storedCheck, Json);
            session.UpdatedAt = now;

            var stored = false;
            if (!context.DocumentNotArchived
                && consented
                && !check.HolderIsMinor
                && session.BiometricOutcome != BiometricOutcome.Accept
                && !InOperatorPath(session, context))
            {
                await StoreDocumentSideAsync(session, side, bytes, now, ct);
                stored = true;
            }
            else
            {
                await DeleteStoredDocumentAsync(session, ct);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Document of session {SessionId} read: side={Side} method={Method} mrz={MrzRead} acceptable={Acceptable} stored={Stored}",
                session.Id, DocumentSides.CodeOf(side), reading.Method, mrzRead, acceptable, stored);

            return new DocumentStepResult(
                reading,
                acceptable,
                guidance.Distinct(StringComparer.Ordinal).ToList(),
                stored,
                context.DocumentNotArchived,
                side,
                SidesOf(side != DocumentSide.Single, front, back));
        }
        finally
        {
            opened?.Dispose();
            ImageBytes.Zero(bytes);
        }
    }

    public Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto, CancellationToken ct)
    {
        return SubmitSelfieAsync(session, selfie, documentPhoto, null, ct);
    }

    public Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto,
        Stream? documentPhotoBack, CancellationToken ct) =>
        SubmitChallengedSelfieAsync(session, selfie, null, null, documentPhoto, documentPhotoBack, ct);

    private async Task<SelfieStepResult> VerifySelfieAsync(GuestCheckInSession session, Stream selfie, Stream? turnedSelfie,
        string? challengeToken, Stream? documentPhoto, Stream? documentPhotoBack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selfie);
        var context = await ContextAsync(session, ct);
        var policy = context.Policy;

        if (session.BiometricOutcome == BiometricOutcome.Accept)
            return turnedSelfie is null && string.IsNullOrEmpty(challengeToken)
                ? Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), null)
                : Refused(SelfieStepStatus.ChallengeRequired, session, policy);

        if (InOperatorPath(session, context))
        {
            await CloseOperatorPathAsync(session, context, ct);
            return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);
        }

        if (!context.Enabled)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.BiometricsDisabled, policy, ct);
        }

        if (context.IsMinor)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.MinorExcluded, policy, ct);
        }

        if (context.BoundGuestMissing)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.Review, policy, ct);
        }

        if (!context.ConsentTextServed)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.LegalTextNotApproved, policy, ct);
        }

        var stored = ReadCheck(session);
        if (stored?.Check.HolderIsMinor == true)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.MinorExcluded, policy, ct);
        }
        if (!HasCurrentConsent(session, context))
            return Refused(SelfieStepStatus.ConsentRequired, session, policy);
        if (stored is null)
            return Refused(SelfieStepStatus.DocumentRequired, session, policy);

        if (!DocumentComplete(session, stored))
            return Refused(SelfieStepStatus.DocumentRequired, session, policy);

        if (session.BiometricAttempts >= policy.MaxAttempts)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.AttemptsExhausted, policy, ct);
        }

        if (turnedSelfie is null || !IsCurrentChallenge(session, stored, challengeToken))
            return Refused(SelfieStepStatus.ChallengeRequired, session, policy);

        var attempt = session.BiometricAttempts + 1;
        IdentityVerificationOutcome outcome;
        byte[]? neutralBytes = null;
        byte[]? documentBytes = null;
        byte[]? backBytes = null;
        try
        {
            if (documentPhoto is not null)
            {
                documentBytes = await ImageBytes.ReadAllAsync(documentPhoto, MaxImageBytes, ct);
                if (!string.Equals(Sha256(documentBytes), stored.PhotoSha256, StringComparison.Ordinal))
                    return Refused(SelfieStepStatus.DocumentPhotoMismatch, session, policy);
            }
            else if (context.DocumentNotArchived)
            {
                return Refused(SelfieStepStatus.DocumentPhotoRequired, session, policy);
            }
            else
            {
                documentBytes = await LoadStoredDocumentAsync(session, ct);
                if (documentBytes is null)
                    return Refused(SelfieStepStatus.DocumentRequired, session, policy);
            }

            if (context.DocumentNotArchived && stored.BackSha256 is not null)
            {
                if (documentPhotoBack is null)
                    return Refused(SelfieStepStatus.DocumentPhotoRequired, session, policy);

                backBytes = await ImageBytes.ReadAllAsync(documentPhotoBack, MaxImageBytes, ct);
                if (!string.Equals(Sha256(backBytes), stored.BackSha256, StringComparison.Ordinal))
                    return Refused(SelfieStepStatus.DocumentPhotoMismatch, session, policy);
            }

            using var documentStream = new MemoryStream(documentBytes, writable: false);
            try
            {
                var direction = stored.Pose!.NoseToRight;
                session.DocumentCheckJson = JsonSerializer.Serialize(stored with { Pose = null }, Json);
                await db.SaveChangesAsync(ct);
                if (activePose is null)
                {
                    await DeleteStoredDocumentAsync(session, ct);
                    return await OperatorAsync(session, VerificationAlertReasons.TechnicalError, policy, ct);
                }
                neutralBytes = await ImageBytes.ReadAllAsync(selfie, CheckInDocumentLimits.MaxImageBytes, ct);
                using var neutral = new MemoryStream(neutralBytes, writable: false);
                outcome = await agent.VerifyAsync(new IdentityVerificationRequest(neutral, documentStream, stored.Check, policy, attempt), ct);
                if (outcome.Result == IdentityVerificationResult.Ok)
                {
                    neutral.Position = 0;
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    var passed = await activePose.VerifyAsync(neutral, turnedSelfie, direction, policy.AcceptThreshold, ct);
                    var reason = new VerificationReason(VerificationReasons.Checks.ActivePose, passed ? VerificationReasons.Results.Passed : VerificationReasons.Results.Failed);
                    outcome = outcome with
                    {
                        Reasons = [.. outcome.Reasons, reason],
                        DurationMs = outcome.DurationMs + (int)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                        Result = passed ? outcome.Result : IdentityVerificationResult.NotOk,
                        GuidanceCodes = passed ? outcome.GuidanceCodes : ["active_pose_retry"],
                        LastAttempt = !passed && attempt >= policy.MaxAttempts,
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Identity verification of session {SessionId} failed ({ErrorType}); it goes to the operator", session.Id, ex.GetType().Name);
                await DeleteStoredDocumentAsync(session, ct);
                return await OperatorAsync(session, VerificationAlertReasons.TechnicalError, policy, ct);
            }
        }
        finally
        {
            ImageBytes.Zero(neutralBytes);
            ImageBytes.Zero(documentBytes);
            ImageBytes.Zero(backBytes);
        }

        return await ApplyAsync(session, outcome, attempt, policy, ct);
    }

    public async Task<IdentityVerificationState> RequestOperatorAsync(GuestCheckInSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = await ContextAsync(session, ct);
        if (session.BiometricOutcome != BiometricOutcome.Accept)
        {
            // E22/F01/US02/T02: a guest who is on the operator path because no text of the consent is served (production, nothing approved) is not the one who chose it: the host
            // is told the reason, legal_text_not_approved, not a request the guest did not make.
            var reason = context.Enabled && !context.IsMinor && !context.ConsentTextServed
                ? VerificationAlertReasons.LegalTextNotApproved
                : VerificationAlertReasons.GuestRequest;

            if (SelfHostedOn && session.IdentityOpenedAt is not null && session.IdentityClosedAt is null)
            {
                var now = UtcNow();
                session.IdentityChoice = IdentityChoices.Assisted;
                session.IdentityChoiceAt = now;
                session.UpdatedAt = now;
            }

            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, reason, ct);
        }

        return State(session, context);
    }

    private async Task<SelfieStepResult> ApplyAsync(
        GuestCheckInSession session,
        IdentityVerificationOutcome outcome,
        int attempt,
        VerificationPolicy policy,
        CancellationToken ct)
    {
        if (outcome.Result == IdentityVerificationResult.Recapture)
            return Done(IdentityVerificationGuestResults.Retry, outcome.GuidanceCodes, Remaining(session, policy), null);

        var now = UtcNow();
        session.BiometricAttempts = attempt;
        session.BiometricOutcome = outcome.BiometricOutcome;
        session.UpdatedAt = now;
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = session.OrgId,
            BookingId = session.BookingId,
            GuestId = session.Booking.GuestId,
            Outcome = outcome.BiometricOutcome ?? BiometricOutcome.Escalate,
            Method = VerificationMethod.Biometric,
            Attempts = attempt,
            ModelName = outcome.ModelName,
            ModelVersion = outcome.ModelVersion,
            ThresholdsJson = outcome.Thresholds.ToJson(),
            ReasonsJson = VerificationReasons.Serialize(outcome.Reasons),
            Provisional = outcome.Provisional,
            DurationMs = outcome.DurationMs,
            CreatedAt = now,
        });

        if (outcome.Result == IdentityVerificationResult.Ok)
        {
            domainEvents?.Record(
                DomainEventType.IdVerificationCompleted,
                session.BookingId,
                new IdVerificationCompletedPayload(session.BookingId, session.Booking.PropertyId, IdVerificationMethods.Biometric),
                orgId: session.OrgId);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Identity verification of session {SessionId}: result={Result} attempt={Attempt}/{MaxAttempts} durationMs={DurationMs}",
            session.Id, outcome.Result, attempt, policy.MaxAttempts, outcome.DurationMs);

        switch (outcome.Result)
        {
            case IdentityVerificationResult.Ok:
                await DeleteStoredDocumentAsync(session, ct);
                await db.SaveChangesAsync(ct);

                return Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), null);

            case IdentityVerificationResult.Review:
                await DeleteStoredDocumentAsync(session, ct);
                await ToOperatorAsync(
                    session,
                    outcome.TechnicalFailure ? VerificationAlertReasons.TechnicalError : VerificationAlertReasons.Review,
                    ct);
                return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);

            default:
                if (outcome.LastAttempt || attempt >= policy.MaxAttempts)
                {
                    await DeleteStoredDocumentAsync(session, ct);
                    await ToOperatorAsync(session, VerificationAlertReasons.AttemptsExhausted, ct);
                    return Done(IdentityVerificationGuestResults.Operator, [], 0, null);
                }

                return Done(IdentityVerificationGuestResults.Retry, outcome.GuidanceCodes, Remaining(session, policy), null);
        }
    }

    private async Task<string?> ReleaseCodeAsync(GuestCheckInSession session, CancellationToken ct)
    {
        var code = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.CheckInScreen, ct);
        if (code is null)
        {
            logger.LogInformation("Identity verification of session {SessionId} passed: no lock code was released (none set, or held back)", session.Id);
            return null;
        }

        var booking = session.Booking;
        var guest = booking.Guest;
        if (guest is null || string.IsNullOrWhiteSpace(guest.Email))
            return code;

        var emailed = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.Email, ct);
        if (emailed is null)
            return code;

        var branding = await emailBranding.ForOrgOrNullAsync(session.OrgId, ct);
        var email = EmailTemplates.GuestAccessCode(
            EmailTemplates.GuestAccessCodeCulture(session.Language),
            $"{guest.FirstName} {guest.LastName}".Trim(),
            booking.Property.Name,
            booking.CheckInDate,
            emailed,
            branding);
        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestAccessCode))
            logger.LogWarning("Access code e-mail of session {SessionId} not queued", session.Id);

        return code;
    }

    private async Task ToOperatorAsync(GuestCheckInSession session, string reason, CancellationToken ct)
    {
        CloseIdentitySession(session, IdentityOutcomeOf(reason));

        if (session.OperatorRequestedAt is null)
        {
            var alreadyTold = await VerificationHandOff.HasOpenHandOffAsync(db, session.BookingId, ct);
            var now = UtcNow();
            session.OperatorRequestedAt = now;
            session.UpdatedAt = now;
            if (!alreadyTold)
                await alerts.RaiseAsync(session.Id, reason, ct);

            logger.LogInformation(
                "Identity verification of session {SessionId} handed to the operator ({Reason}, alerted={Alerted})",
                session.Id, reason, !alreadyTold);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<SelfieStepResult> OperatorAsync(GuestCheckInSession session, string reason, VerificationPolicy policy, CancellationToken ct)
    {
        await ToOperatorAsync(session, reason, ct);
        return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);
    }

    /// <summary>
    /// The verification is on the operator path already (<see cref="InOperatorPath"/>): the pictures kept for the comparison go, both sides of a document, as on
    /// every other way to a person (an administrator of the organization can switch the face check off or lower the attempts while a guest is between the
    /// document and the selfie, E22/F01/US01/T03). And when that is only because the attempts already made reach a maximum that was lowered since
    /// (<see cref="OnlyTheAttemptsReachTheMax"/>), nobody was told yet: the hand-off is made now, so the alert, the "To do" and the queue of the operators exist
    /// (a request already open, a refusal, a review or a booking with an operator were told before and are not told again).
    /// </summary>
    private async Task CloseOperatorPathAsync(GuestCheckInSession session, FlowContext context, CancellationToken ct)
    {
        await DeleteStoredDocumentAsync(session, ct);

        CloseIdentitySession(session, IdentityOutcomeCodes.Review);
        if (OnlyTheAttemptsReachTheMax(session, context))
        {
            await ToOperatorAsync(session, VerificationAlertReasons.AttemptsExhausted, ct);
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The operator path is the verification's only because the attempts made reach the maximum in force: no request to an operator, no refused consent, no
    /// review and no hand-off of the booking, and the verification was not accepted. The maximum of the attempts is the organization's to change, so a session
    /// in the middle of its attempts can find itself there with no one told.
    /// </summary>
    private static bool OnlyTheAttemptsReachTheMax(GuestCheckInSession session, FlowContext context) =>
        !context.BookingWithOperator
        && session.OperatorRequestedAt is null
        && session.BiometricConsentRefusedAt is null
        && session.BiometricOutcome != BiometricOutcome.Escalate
        && session.BiometricOutcome != BiometricOutcome.Accept
        && session.BiometricAttempts >= context.Policy.MaxAttempts;

    private static SelfieStepResult Done(string result, IReadOnlyList<string> guidance, int remaining, string? lockCode, string? lockCodeHeld = null) =>
        new(SelfieStepStatus.Done, result, guidance, remaining, lockCode, lockCodeHeld);

    /// <summary>
    /// Why a passed verification came with no lock code although the property has one (E25/F01/US01/T01): the code is held back until the tourist tax is paid, and
    /// the guest is told so (<see cref="AccessCodeHoldReasons"/>) instead of seeing nothing. <c>null</c> when a code came or nothing holds it back.
    /// </summary>
    private async Task<string?> HoldReasonAsync(GuestCheckInSession session, string? releasedCode, CancellationToken ct) =>
        releasedCode is null ? await accessCodes.GetHoldReasonAsync(session.BookingId, ct) : null;

    private static SelfieStepResult Refused(SelfieStepStatus status, GuestCheckInSession session, VerificationPolicy policy) =>
        new(status, IdentityVerificationGuestResults.Retry, [], Remaining(session, policy), null);

    private static int Remaining(GuestCheckInSession session, VerificationPolicy policy) =>
        Math.Max(0, policy.MaxAttempts - session.BiometricAttempts);

    private static bool InOperatorPath(GuestCheckInSession session, FlowContext context) =>
        context.BookingWithOperator
        || session.OperatorRequestedAt is not null
        || session.BiometricConsentRefusedAt is not null
        || session.BiometricOutcome == BiometricOutcome.Escalate
        || (session.BiometricAttempts >= context.Policy.MaxAttempts && session.BiometricOutcome != BiometricOutcome.Accept);

    private static bool HasCurrentConsent(GuestCheckInSession session, FlowContext context) =>
        session.BiometricConsentAt is not null && context.ActiveConsentText is { } active
        && string.Equals(active.Version, session.BiometricConsentVersion, StringComparison.Ordinal)
        && string.Equals(active.TextHash, session.BiometricConsentTextHash, StringComparison.Ordinal);

    private IdentityVerificationState State(GuestCheckInSession session, FlowContext context)
    {
        var check = ReadCheck(session);
        var minor = context.IsMinor || check?.Check.HolderIsMinor == true;
        var offered = context.Enabled && context.ConsentTextServed && !minor && session.BiometricConsentRefusedAt is null && !context.BoundGuestMissing;
        var operatorPath = InOperatorPath(session, context);
        var passed = session.BiometricOutcome == BiometricOutcome.Accept;
        var consented = HasCurrentConsent(session, context);

        // A document of one picture counts as checked as soon as it was read (the agent says what is wrong at the selfie); a document of two sides only
        // when the face and the MRZ were found across the pictures, so a reloaded page asks for the missing side instead of the selfie.
        var documentChecked = check is not null && DocumentComplete(session, check);

        var selfHosted = SelfHostedOn;

        // E73/F01/US01/T02: on the self-hosted flow a guided retry is a retry the guest was offered, and the step after the document is the presence (the two-frame selfie is closed).
        string? result = passed
            ? IdentityVerificationGuestResults.Ok
            : operatorPath
                ? IdentityVerificationGuestResults.Operator
                : session.BiometricAttempts > 0 || (selfHosted && session.IdentityGuidedRetries > 0) ? IdentityVerificationGuestResults.Retry : null;
        var step = passed
            ? IdentityVerificationSteps.Done
            : operatorPath || !offered
                ? IdentityVerificationSteps.Operator
                : !consented
                    ? IdentityVerificationSteps.Consent
                    : documentChecked
                        ? selfHosted ? IdentityVerificationSteps.Presence : IdentityVerificationSteps.Selfie
                        : IdentityVerificationSteps.Document;

        var closed = selfHosted && session.IdentityClosedAt is not null;
        return new IdentityVerificationState(
            step,
            result,
            offered,
            consented,
            session.BiometricConsentRefusedAt is not null,
            documentChecked,
            Remaining(session, context.Policy),
            session.OperatorRequestedAt is not null || context.BookingWithOperator,
            context.DocumentNotArchived,
            context.VerifyingGuestPosition,
            check is null ? null : SidesOf(check.TwoSided, check.Front, check.Back),
            SideNeeded(check),
            SelfHosted: selfHosted,
            Opened: selfHosted && session.IdentityOpenedAt is not null,
            NoticeVersion: selfHosted ? session.IdentityNoticeVersion : null,
            Choice: selfHosted ? session.IdentityChoice : null,
            FormatId: selfHosted ? session.IdentityFormatId : null,
            GuidedRetryAvailable: selfHosted && !closed && !passed && !operatorPath && session.IdentityGuidedRetries < GuidedRetriesOfThePolicy(),
            Closed: closed,
            Outcome: closed ? GuestOutcomeOf(session.IdentityOutcomeCode, session.OperatorRequestedAt is not null) : null);
    }

    /// <summary>The guided retries the policy offers (<c>closure.guidedRetries</c>); none while the policy is not usable.</summary>
    private int GuidedRetriesOfThePolicy()
    {
        var snapshot = identityPolicy?.Snapshot;
        return snapshot is not null && snapshot.IsReady ? snapshot.Policy.Closure.GuidedRetries : 0;
    }

    private async Task<FlowContext> ContextAsync(GuestCheckInSession session, CancellationToken ct)
    {
        var (enabled, policy) = await PolicyAsync(session.OrgId, ct);

        var booking = session.Booking;
        var guests = await stayGuests.GetForBookingAsync(booking, ct);

        // E70/F02/US01/T01: a self-hosted verification bound to a guest reads that guest only, wherever an edit of the list put it; when that guest left the stay
        // (and is not the booker, whose data are read when nobody is registered) the verification is not offered any more. Otherwise, the rule of today.
        StayGuest? verifying;
        var boundGuestMissing = false;
        if (SelfHostedOn && session.IdentityGuestId is { } bound)
        {
            verifying = BoundGuestOf(guests, bound);
            boundGuestMissing = verifying is null && bound != booking.GuestId;
        }
        else
        {
            verifying = guests.FirstOrDefault(g => AlloggiatiRecordRules.RequiresDocument(g.Type)) ?? guests.FirstOrDefault();
        }

        var identity = verifying is null
            ? new CheckInIdentity(booking.Guest?.FirstName, booking.Guest?.LastName, DateOf(booking.Guest?.DateOfBirth), booking.Guest?.DocumentNumber, booking.Guest?.Nationality)
            : new CheckInIdentity(verifying.FirstName, verifying.LastName, DateOf(verifying.DateOfBirth), verifying.DocumentNumber, verifying.CitizenshipName);

        return new FlowContext(
            enabled,
            policy,
            identity,
            verifying?.Position ?? 0,
            IsMinor(identity.DateOfBirth),
            await DocumentNotArchivedAsync(booking.Property, ct),
            await VerificationHandOff.IsWithOperatorAsync(db, booking.Id, ct),
            legalTexts?.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent),
            BindingIdOf(verifying, booking),
            boundGuestMissing);
    }

    private async Task<(bool Enabled, VerificationPolicy Policy)> PolicyAsync(Guid orgId, CancellationToken ct)
    {
        var settings = await db.BiometricSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Enabled, s.AcceptThreshold, s.EscalateThreshold, s.MaxAttempts, s.CalibratedAt })
            .FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            return (settings.Enabled, new VerificationPolicy(
                (double)settings.AcceptThreshold,
                (double)settings.EscalateThreshold,
                Math.Clamp(settings.MaxAttempts, 1, 10),
                settings.CalibratedAt is null));
        }

        var tier = await db.Orgs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Id == orgId)
            .Select(o => (PlanTier?)o.PlanTier)
            .FirstOrDefaultAsync(ct);
        return (tier == PlanTier.Agency, DefaultPolicy);
    }

    /// <summary>
    /// The property keeps no document (ADR-013-R6, R15): it is in canton Ticino, or in Switzerland with a canton CasaZen does not know (the
    /// prudent reading of the zero retention). The canton is the one of the one resolver of the place of a Swiss property (E29/F01/US01/T02), and
    /// only a municipality the host confirmed (stored) outside Ticino lets a document be kept: a place derived from the postal code, or an
    /// ambiguous one, keeps nothing, as a property without a stored municipality always did.
    /// </summary>
    private async Task<bool> DocumentNotArchivedAsync(Property property, CancellationToken ct)
    {
        if (!SwissPlace.IsSwissCountry(property.Country))
            return false;

        var place = await (swissPlaces ?? new PropertySwissPlaceResolver(db)).ResolveAsync(property, ct);
        return place is not { IsKnown: true, Source: SwissPlaceSource.Stored } || place.IsInTicino;
    }

    private bool IsMinor(DateOnly? dateOfBirth)
    {
        if (dateOfBirth is not { } born)
            return false;

        var today = clock.TodayInRomeAsDateOnly();
        var age = today.Year - born.Year;
        if (born > today.AddYears(-age))
            age--;

        return age < 18;
    }

    private DocumentCheck Check(MrzExtractionResult? read, CheckInIdentity identity)
    {
        if (read is null)
            return new DocumentCheck(false, false, null, null, null, null, null, false);

        var consistency = MrzCheckInConsistency.Compare(read, identity);
        return new DocumentCheck(
            MrzRead: true,
            CheckDigitsValid: read.CheckDigitsValid,
            Expired: read.IsExpired,
            NamesConsistent: consistency.Names,
            BirthDateConsistent: consistency.BirthDate,
            DocumentNumberConsistent: consistency.DocumentNumber,
            NationalityConsistent: consistency.Nationality,
            HolderIsMinor: IsMinor(DateOf(read.DateOfBirth)));
    }

    /// <summary>
    /// The checks of the document across its pictures (E22/F01/US01/T02): the MRZ may come from any side. When both sides carry one (a card that repeats
    /// it) the one that is valid wins, otherwise the one just sent. A minor found on either side is a minor.
    /// </summary>
    private static DocumentCheck CombineChecks(DocumentCheck? front, DocumentCheck? back, DocumentSide latest)
    {
        if (front is null && back is null)
            return new DocumentCheck(false, false, null, null, null, null, null, false);

        DocumentCheck chosen;
        if (front is null)
            chosen = back!;
        else if (back is null)
            chosen = front;
        else if (front.Valid != back.Valid)
            chosen = front.Valid ? front : back;
        else
            chosen = latest == DocumentSide.Back ? back : front;

        var minor = front?.HolderIsMinor == true || back?.HolderIsMinor == true;
        return minor && !chosen.HolderIsMinor ? chosen with { HolderIsMinor = true } : chosen;
    }

    private static bool? FaceOf(CaptureAssessment? assessment, DocumentSide side)
    {
        if (side == DocumentSide.Back || assessment is not { Assessed: true })
            return null;

        return !assessment.GuidanceCodes.Contains(CaptureGuidanceCodes.ShowDocumentPhoto, StringComparer.Ordinal);
    }

    private static bool? QualityOf(CaptureAssessment? assessment)
    {
        if (assessment is not { Assessed: true })
            return null;

        return assessment.GuidanceCodes.All(code => string.Equals(code, CaptureGuidanceCodes.ShowDocumentPhoto, StringComparison.Ordinal));
    }

    /// <summary>
    /// Says which side is missing after a picture of a document of two sides (E22/F01/US01/T02): a good front without any MRZ asks for the back; an MRZ
    /// read on the back without a good front asks for the front; an MRZ that was not found on the back is shown again as the usual guidance. A single
    /// picture keeps the guidance it always had.
    /// </summary>
    private static void AddMissingSideGuidance(List<string> guidance, DocumentSide side, bool mrzFoundHere, bool mrzFoundAnywhere, bool frontGood)
    {
        switch (side)
        {
            case DocumentSide.Single:
                if (!mrzFoundHere)
                    guidance.Add(CaptureGuidanceCodes.ShowWholeDocument);

                break;
            case DocumentSide.Front:
                if (!mrzFoundAnywhere && frontGood)
                {
                    guidance.RemoveAll(code => string.Equals(code, CaptureGuidanceCodes.ShowWholeDocument, StringComparison.Ordinal));
                    guidance.Add(CaptureGuidanceCodes.DocumentBackNeeded);
                }

                break;
            default:
                if (!mrzFoundAnywhere)
                    guidance.Add(CaptureGuidanceCodes.ShowWholeDocument);
                else if (!frontGood)
                    guidance.Add(CaptureGuidanceCodes.DocumentFrontNeeded);

                break;
        }
    }

    private static bool IsComplete(StoredDocumentCheck check) =>
        !check.TwoSided
        || (check.Front is { } front && front.Face != false && (front.Mrz is not null || check.Back?.Mrz is not null));

    private static string? SideNeeded(StoredDocumentCheck? check)
    {
        if (check is null || !check.TwoSided)
            return null;

        if (check.Front is not { } front || front.Face == false)
            return DocumentSides.Front;

        return front.Mrz is null && check.Back?.Mrz is null ? DocumentSides.Back : null;
    }

    private static IReadOnlyList<string> SidesOf(bool twoSided, StoredDocumentPicture? front, StoredDocumentPicture? back)
    {
        if (!twoSided)
            return [DocumentSides.Single];

        var sides = new List<string>(2);
        if (front is not null)
            sides.Add(DocumentSides.Front);

        if (back is not null)
            sides.Add(DocumentSides.Back);

        return sides;
    }

    private static string? DocumentNumberProof(string key, string? number)
    {
        var normalized = AlloggiatiRecordRules.NormalizeDocumentNumber(number);
        return normalized.Length == 0 ? null : Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(normalized)));
    }

    internal static int? VerifiedNumberMismatch(GuestCheckInSession session, IReadOnlyList<StayGuestInput> guests)
    {
        if (session.BiometricOutcome != BiometricOutcome.Accept || ReadCheck(session) is not { } stored)
            return null;
        var proof = stored.Back is { Mrz.CheckDigitsValid: true } back ? back.NumberProof : stored.Front?.NumberProof;
        if (proof is null) return null;
        var position = stored.VerifyingGuestPosition;
        return DocumentNumberProof(stored.ProofKey ?? session.TokenHash, guests.ElementAtOrDefault(position)?.DocumentNumber) == proof ? null : position;
    }

    private static StoredDocumentCheck? ReadCheck(GuestCheckInSession session)
    {
        if (string.IsNullOrWhiteSpace(session.DocumentCheckJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoredDocumentCheck>(session.DocumentCheckJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the picture of one side encrypted in the private bucket, in the <see cref="DocumentImage"/> row of the attempt: the front (or the single
    /// picture) in <see cref="DocumentImage.FrontImageUrl"/>, the back in <see cref="DocumentImage.BackImageUrl"/> (E22/F01/US01/T02). A single picture
    /// replaces everything kept before, a front or a back replaces its own picture and leaves the other. The row of an attempt that started with the
    /// back has an empty front address until the front comes (the cleaners skip a blank address).
    /// </summary>
    private async Task StoreDocumentSideAsync(GuestCheckInSession session, DocumentSide side, byte[] bytes, DateTime now, CancellationToken ct)
    {
        DocumentImage? row = null;
        if (side == DocumentSide.Single)
        {
            await DeleteStoredDocumentAsync(session, ct);
        }
        else
        {
            var live = await db.DocumentImages
                .IgnoreQueryFilters()
                .Where(i => i.OrgId == session.OrgId && i.BookingId == session.BookingId && i.DeletedAt == null)
                .OrderByDescending(i => i.UploadedAt)
                .ToListAsync(ct);
            row = live.FirstOrDefault();

            foreach (var extra in live.Skip(1))
            {
                await DeleteObjectsAsync(extra, ct);
                extra.DeletedAt = now;
            }

            if (row is not null)
                await DeleteObjectAsync(side == DocumentSide.Front ? row.FrontImageUrl : row.BackImageUrl, ct);
        }

        var key = await PutEncryptedAsync(session, bytes, ct);
        if (row is null)
        {
            db.DocumentImages.Add(new DocumentImage
            {
                OrgId = session.OrgId,
                GuestId = session.Booking.GuestId,
                BookingId = session.BookingId,
                FrontImageUrl = side == DocumentSide.Back ? string.Empty : key,
                BackImageUrl = side == DocumentSide.Back ? key : null,
                UploadedAt = now,
                ProcessedAt = now,
            });
            return;
        }

        if (side == DocumentSide.Front)
            row.FrontImageUrl = key;
        else
            row.BackImageUrl = key;

        row.UploadedAt = now;
        row.ProcessedAt = now;
    }

    private async Task<string> PutEncryptedAsync(GuestCheckInSession session, byte[] bytes, CancellationToken ct)
    {
        var key = StorageKeys.GuestDocument(session.OrgId, session.Booking.GuestId, $"{Guid.NewGuid():N}.bin");
        var cipher = dataProtection.CreateProtector(DocumentImageContentPurpose).Protect(bytes);
        try
        {
            using var content = new MemoryStream(cipher, writable: false);
            await storage.PutAsync(StorageBucket.Private, key, content, StoredContentType, ct);
        }
        finally
        {
            Array.Clear(cipher);
        }

        return key;
    }

    private async Task DeleteObjectAsync(string? key, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key))
            await storage.DeleteAsync(StorageBucket.Private, key, ct);
    }

    private async Task DeleteObjectsAsync(DocumentImage image, CancellationToken ct)
    {
        await DeleteObjectAsync(image.FrontImageUrl, ct);
        await DeleteObjectAsync(image.BackImageUrl, ct);
    }

    private async Task<byte[]?> LoadStoredDocumentAsync(GuestCheckInSession session, CancellationToken ct)
    {
        var key = await db.DocumentImages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(i => i.OrgId == session.OrgId && i.BookingId == session.BookingId && i.DeletedAt == null)
            .OrderByDescending(i => i.UploadedAt)
            .Select(i => i.FrontImageUrl)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        await using var stream = await storage.OpenReadAsync(StorageBucket.Private, key, ct);
        if (stream is null)
            return null;

        var cipher = await ImageBytes.ReadAllAsync(stream, MaxImageBytes * 2, ct);
        try
        {
            return dataProtection.CreateProtector(DocumentImageContentPurpose).Unprotect(cipher);
        }
        finally
        {
            Array.Clear(cipher);
        }
    }

    private async Task DeleteStoredDocumentAsync(GuestCheckInSession session, CancellationToken ct)
    {
        var kept = await db.DocumentImages
            .IgnoreQueryFilters()
            .Where(i => i.OrgId == session.OrgId && i.BookingId == session.BookingId && i.DeletedAt == null)
            .ToListAsync(ct);
        if (kept.Count == 0)
            return;

        var now = UtcNow();
        foreach (var image in kept)
        {
            await DeleteObjectsAsync(image, ct);
            image.DeletedAt = now;
        }
    }

    private static DocumentReading FromMrz(MrzExtractionResult read) => new(
        DocumentReadingMethod.Mrz,
        read.Confidence,
        read.CheckDigitsValid,
        !read.CheckDigitsValid || read.Confidence < DocumentReading.ManualReviewBelow,
        new DocumentReadingFields(
            read.DocumentType,
            read.Surname,
            read.GivenNames,
            null,
            read.DocumentNumber,
            DateOf(read.DateOfBirth),
            read.Nationality,
            DateOf(read.ExpirationDate),
            read.Sex,
            read.IssuingState));

    private static DocumentReading FromOcr(DocumentOcrResult read) => new(
        DocumentReadingMethod.Ocr,
        read.Confidence,
        null,
        read.Confidence < DocumentReading.ManualReviewBelow,
        new DocumentReadingFields(
            read.DocumentKind,
            read.Surname,
            read.GivenNames,
            read.FullName,
            read.DocumentNumber,
            DateOf(read.DateOfBirth),
            read.Nationality,
            DateOf(read.ExpirationDate),
            read.Sex,
            null));

    private static DateOnly? DateOf(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    private sealed record FlowContext(
        bool Enabled,
        VerificationPolicy Policy,
        CheckInIdentity Identity,
        int VerifyingGuestPosition,
        bool IsMinor,
        bool DocumentNotArchived,
        bool BookingWithOperator,
        CheckInLegalVersion? ActiveConsentText,
        Guid VerifyingGuestId,
        bool BoundGuestMissing)
    {
        public bool ConsentTextServed => ActiveConsentText is not null;
    }
}

/// <summary>
/// One picture of the attempt as the session keeps it (E22/F01/US01/T02): whether a face was found on it (null: not looked for, as on a back, or the models
/// could not say), whether its light, glare and blur were good (null: not assessed) and, when its MRZ was read, the checks made on it. A session-scoped proof binds the number to the final submission.
/// <para>
/// The members of the self-hosted document step (E71/F01/US03/T01), optional and null on the legacy path and in every record written before them: <c>GeometryOk</c> is true when the
/// picture passed the geometry of its format, the side check and the quality checks (a picture that does not is refused and never stored, so a stored picture of the self-hosted flow
/// has it true); <c>ClassifiedSide</c> the side it showed by its content (<c>front</c>, <c>back</c> or <c>single</c>; null when it could not be told); <c>DHash</c> its difference
/// hash, 16 hexadecimal digits, which the check of a pair of sides compares. Flags and hashes: never a value read on the document.
/// </para>
/// </summary>
internal sealed record StoredDocumentPicture(
    bool? Face,
    bool? QualityOk,
    DocumentCheck? Mrz,
    string? NumberProof = null,
    bool? GeometryOk = null,
    string? ClassifiedSide = null,
    string? DHash = null);

/// <summary>
/// The checks of the document as the session keeps them (<see cref="GuestCheckInSession.DocumentCheckJson"/>): the flags across the pictures
/// (<see cref="Check"/>), the SHA-256 of the picture with the face (<see cref="PhotoSha256"/>, empty while only the back was sent) and of the back
/// (<see cref="BackSha256"/>), to recognise them when they come again with the selfie in Ticino, when. For a document of two sides
/// (<see cref="TwoSided"/>) what each picture showed. A record written before the two-sided document has none of the optional members and reads as a
/// single picture. No value read on the document. <c>ProofKey</c> is the key the proofs of the numbers (<see cref="StoredDocumentPicture.NumberProof"/>) were made with, the token hash the session
/// had when its first picture came: a session that gets a new token keeps it, so its proofs still verify; a record without it uses the token hash of the session, as before.
/// <c>Presence</c> is the presence challenge of the self-hosted flow (E70/F01/US01/T01, <see cref="StoredPresenceChallenge"/>): null on the legacy path and in every
/// record written before it, which reads exactly as before.
/// <para>
/// The members of the self-hosted document step (E71/F01/US03/T01), all optional and null in every record written before them: <c>Refusals</c> is how many pictures of each side
/// (<c>front</c>, <c>back</c>, <c>single</c>) were refused with a guidance code, the count that ends in <c>document_unreadable</c> after
/// <c>policy.document.maxRecapturesPerSide</c>; <c>Coherence</c> is the coherence report of the two sides as its entries (check, result and the NAME of the field: never a value),
/// null while a side is missing or cannot be read again (nothing kept in Ticino): an unknown is never a pass. <c>RefusedSha256</c> is the SHA-256 of the picture last refused for each side,
/// so the same file sent again (a request the network repeated, a double submit) is refused again and does not cost a second recapture; a side that is accepted forgets both its
/// refusals and that hash. A hash of the bytes, as the one of a kept picture: never a value read.
/// </para>
/// </summary>
internal sealed record StoredDocumentCheck(
    DocumentCheck Check,
    string PhotoSha256,
    DateTime CheckedAt,
    string? BackSha256 = null,
    StoredDocumentPicture? Front = null,
    StoredDocumentPicture? Back = null,
    bool TwoSided = false,
    int VerifyingGuestPosition = 0,
    string? ProofKey = null,
    StoredPoseChallenge? Pose = null,
    StoredPresenceChallenge? Presence = null,
    IReadOnlyDictionary<string, int>? Refusals = null,
    IReadOnlyList<DocumentCoherenceCheck>? Coherence = null,
    IReadOnlyDictionary<string, string>? RefusedSha256 = null);
