using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// </remarks>
public sealed class IdentityVerificationFlowService(
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
    ILogger<IdentityVerificationFlowService> logger) : IIdentityVerificationFlowService
{
    /// <summary>Data Protection purpose of the encrypted picture of a document in the private bucket. Never change it: stored pictures would become unreadable.</summary>
    public const string DocumentImageContentPurpose = "Casazen.DocumentImage.Content";

    /// <summary>The thresholds of an organization without settings (ADR-013-R13), never calibrated.</summary>
    public static readonly VerificationPolicy DefaultPolicy = new(0.85, 0.70, 3, Provisional: true);

    /// <summary>Largest picture read, in bytes (the endpoint refuses more than 10 MB anyway).</summary>
    internal const int MaxImageBytes = 12_000_000;

    private const string TicinoCantonCode = "TI";
    private const string SwitzerlandCountryCode = "CH";
    private const string StoredContentType = "application/octet-stream";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IdentityVerificationState> GetStateAsync(GuestCheckInSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = await ContextAsync(session, ct);
        return State(session, context);
    }

    public async Task<BiometricConsentResult> RecordConsentAsync(
        GuestCheckInSession session,
        bool consentGiven,
        string? consentVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var context = await ContextAsync(session, ct);

        // A verification that passed is over: an answer changes nothing.
        if (session.BiometricOutcome == BiometricOutcome.Accept)
            return new BiometricConsentResult(BiometricConsentStatus.Recorded, State(session, context));

        var now = UtcNow();
        if (!consentGiven)
        {
            session.BiometricConsentRefusedAt = now;
            session.BiometricConsentAt = null;
            session.UpdatedAt = now;
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.ConsentRefused, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Refused, State(session, context));
        }

        var version = consentVersion?.Trim();
        if (string.IsNullOrEmpty(version) || version.Length > BiometricConsentRules.VersionMaxLength)
            return new BiometricConsentResult(BiometricConsentStatus.VersionRequired, State(session, context));

        if (!context.Enabled)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.BiometricsDisabled, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        if (context.IsMinor)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.MinorExcluded, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        // Already with the operator (a refusal, a request, a review): the person decides, the biometric path is not opened again.
        if (InOperatorPath(session, context))
        {
            await CloseOperatorPathAsync(session, context, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        session.BiometricConsentAt = now;
        session.BiometricConsentVersion = version;
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
        var context = await ContextAsync(session, ct);

        byte[]? bytes = null;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(image, MaxImageBytes, ct);
            var consented = context.Enabled && session.BiometricConsentAt is not null;

            // The face of the document is looked for only after the consent, and not on the back of a card (the photo is on the front); the MRZ
            // hints give the guidance of the reading in any case.
            CaptureAssessment? assessment = null;
            if (consented)
            {
                using var copy = new MemoryStream(bytes, writable: false);
                assessment = await agent.AssessCaptureAsync(copy, side == DocumentSide.Back ? CaptureKind.DocumentBack : CaptureKind.Document, ct);
            }

            MrzReading mrzReading;
            using (var copy = new MemoryStream(bytes, writable: false))
                mrzReading = await mrz.ReadAsync(copy, ct);

            DocumentReading reading;
            if (mrzReading.Result is { } fromMrz)
            {
                reading = FromMrz(fromMrz);
            }
            else if (side == DocumentSide.Back)
            {
                // The printed fields are read on the front: the back of a card is only asked for its MRZ.
                reading = DocumentReading.Nothing;
            }
            else
            {
                using var copy = new MemoryStream(bytes, writable: false);
                var fromOcr = await ocr.ExtractAsync(copy, ct);
                reading = fromOcr is null ? DocumentReading.Nothing : FromOcr(fromOcr);
            }

            var now = UtcNow();
            var picture = new StoredDocumentPicture(
                FaceOf(assessment, side),
                QualityOf(assessment),
                mrzReading.Result is null ? null : Check(mrzReading.Result, context.Identity));

            // The attempt keeps up to two pictures: a side sent again replaces its own and leaves the other, a single picture replaces both.
            var kept = side == DocumentSide.Single ? null : ReadCheck(session);
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
                    back = kept?.Back;
                    backHash = kept?.Back is null ? null : kept.BackSha256;
                    break;
                default:
                    front = picture;
                    frontHash = Sha256(bytes);
                    back = null;
                    backHash = null;
                    break;
            }

            // The face must come from the front (or the single picture), the MRZ from any side.
            var check = CombineChecks(front?.Mrz, back?.Mrz, side);
            var mrzRead = front?.Mrz is not null || back?.Mrz is not null;
            var frontGood = front is not null && front.Face != false && front.QualityOk != false;
            var backGood = back is null || back.Mrz is not null || back.QualityOk != false;
            var acceptable = mrzRead && frontGood && backGood;

            var guidance = (assessment?.GuidanceCodes ?? [])
                .Concat(CaptureGuidanceCodes.FromMrzQuality(mrzReading.Quality))
                .ToList();
            AddMissingSideGuidance(guidance, side, mrzReading.Result is not null, mrzRead, frontGood);

            session.DocumentCheckJson = JsonSerializer.Serialize(
                new StoredDocumentCheck(check, frontHash, now, backHash, front, back, side != DocumentSide.Single),
                Json);
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
                // A new picture replaces the old one: a kept picture of an earlier upload must not stay behind (a minor found on either side wipes both).
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
            ImageBytes.Zero(bytes);
        }
    }

    public Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto, CancellationToken ct)
    {
        return SubmitSelfieAsync(session, selfie, documentPhoto, null, ct);
    }

    public async Task<SelfieStepResult> SubmitSelfieAsync(
        GuestCheckInSession session,
        Stream selfie,
        Stream? documentPhoto,
        Stream? documentPhotoBack,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selfie);
        var context = await ContextAsync(session, ct);
        var policy = context.Policy;

        // Passed already: the code is shown again (a reload of the page), nothing is run or sent again.
        if (session.BiometricOutcome == BiometricOutcome.Accept)
        {
            await DeleteStoredDocumentAsync(session, ct);
            await db.SaveChangesAsync(ct);
            var code = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.CheckInScreen, ct);
            return Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), code, await HoldReasonAsync(session, code, ct));
        }

        // Also a new link of a booking that is with an operator (rejected, or a hand-off still open): the session is new, the decision is not.
        if (InOperatorPath(session, context))
        {
            await CloseOperatorPathAsync(session, context, ct);
            return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);
        }

        // Every way to the operator takes the pictures kept for the comparison with it (both sides of a document): an administrator of the organization can
        // switch the face check off, or lower the attempts, while a guest is between the document and the selfie (E22/F01/US01/T03).
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

        if (session.BiometricConsentAt is null)
            return Refused(SelfieStepStatus.ConsentRequired, session, policy);

        var stored = ReadCheck(session);
        if (stored is null)
            return Refused(SelfieStepStatus.DocumentRequired, session, policy);

        // The document says the holder is a minor: no biometric processing (ADR-013-R9).
        if (stored.Check.HolderIsMinor)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.MinorExcluded, policy, ct);
        }

        // A document of two sides that still lacks its face or its MRZ does not reach the comparison: the page asks for the missing side, and no
        // attempt is spent (the attempts are the complete document and selfie tries, E22/F01/US01/T02).
        if (!IsComplete(stored))
            return Refused(SelfieStepStatus.DocumentRequired, session, policy);

        if (session.BiometricAttempts >= policy.MaxAttempts)
        {
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.AttemptsExhausted, policy, ct);
        }

        var attempt = session.BiometricAttempts + 1;
        IdentityVerificationOutcome outcome;
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
                // The storage and Data Protection errors of this read propagate (500, the guest tries again): they say nothing about the guest
                // and must not hand the verification to a person for good.
                documentBytes = await LoadStoredDocumentAsync(session, ct);
                if (documentBytes is null)
                    return Refused(SelfieStepStatus.DocumentRequired, session, policy);
            }

            // A property in Ticino keeps nothing: the back of a two-sided document comes again too, and must be the one that was read.
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
                outcome = await agent.VerifyAsync(new IdentityVerificationRequest(selfie, documentStream, stored.Check, policy, attempt), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The verification itself failed unexpectedly. The safe default is a person: the session says why, the operators are alerted, the
                // log keeps the type only (a message could carry what was being read).
                logger.LogError("Identity verification of session {SessionId} failed ({ErrorType}); it goes to the operator", session.Id, ex.GetType().Name);
                await DeleteStoredDocumentAsync(session, ct);
                return await OperatorAsync(session, VerificationAlertReasons.TechnicalError, policy, ct);
            }
        }
        finally
        {
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
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.GuestRequest, ct);
        }

        return State(session, context);
    }

    /// <summary>Records one attempt that reached a decision and acts on it: the code on <c>ok</c>, the operator on a review or the last failure.</summary>
    private async Task<SelfieStepResult> ApplyAsync(
        GuestCheckInSession session,
        IdentityVerificationOutcome outcome,
        int attempt,
        VerificationPolicy policy,
        CancellationToken ct)
    {
        // A capture that is not good enough: nothing compared, the attempt does not count, nothing is logged.
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
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Identity verification of session {SessionId}: result={Result} attempt={Attempt}/{MaxAttempts} durationMs={DurationMs}",
            session.Id, outcome.Result, attempt, policy.MaxAttempts, outcome.DurationMs);

        switch (outcome.Result)
        {
            case IdentityVerificationResult.Ok:
                await DeleteStoredDocumentAsync(session, ct);
                await db.SaveChangesAsync(ct);

                // The verification is over: the "To do" of an earlier hand-off of the booking (the operator asked for a new photo, and it passed
                // on its own) has nothing left to wait for.
                await alerts.ResolveAsync(session.OrgId, session.BookingId, ct);
                var released = await ReleaseCodeAsync(session, ct);
                return Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), released, await HoldReasonAsync(session, released, ct));

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

    /// <summary>
    /// The lock code for the screen (channel <c>checkin-screen</c>) and, when the guest has an address, the same code e-mailed at once (channel
    /// <c>email</c>, template <c>GuestAccessCode</c>). Both reads go through the gate of <see cref="IAccessCodeService"/>.
    /// </summary>
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

        var email = EmailTemplates.GuestAccessCode(
            EmailTemplates.GuestAccessCodeCulture(session.Language),
            $"{guest.FirstName} {guest.LastName}".Trim(),
            booking.Property.Name,
            booking.CheckInDate,
            emailed);
        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestAccessCode))
            logger.LogWarning("Access code e-mail of session {SessionId} not queued", session.Id);

        return code;
    }

    /// <summary>
    /// Hands the verification to an operator: the time of the request on the session and, the first time only, the alert (the "To do" is saved
    /// with the session). A later reason does not alert again, and neither does a new session of a booking whose hand-off is still open: the
    /// operators were told and nobody decided yet, and the booking has one "To do". After a decision a new request alerts again.
    /// </summary>
    private async Task ToOperatorAsync(GuestCheckInSession session, string reason, CancellationToken ct)
    {
        if (session.OperatorRequestedAt is null)
        {
            // Read before the request of this session is saved: it says what the other sessions and the log already hold.
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
    /// document and the selfie, E22/F01/US01/T03).
    /// </summary>
    private async Task CloseOperatorPathAsync(GuestCheckInSession session, FlowContext context, CancellationToken ct)
    {
        await DeleteStoredDocumentAsync(session, ct);
        await db.SaveChangesAsync(ct);
    }

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

    /// <summary>
    /// The verification is with the operator: this session asked for a person, refused the consent, was reviewed, or used its attempts without a
    /// pass; or the <b>booking</b> is with an operator through another session (rejected by one, or a hand-off still open: a new link does not
    /// start the verification again).
    /// </summary>
    private static bool InOperatorPath(GuestCheckInSession session, FlowContext context) =>
        context.BookingWithOperator
        || session.OperatorRequestedAt is not null
        || session.BiometricConsentRefusedAt is not null
        || session.BiometricOutcome == BiometricOutcome.Escalate
        || (session.BiometricAttempts >= context.Policy.MaxAttempts && session.BiometricOutcome != BiometricOutcome.Accept);

    private IdentityVerificationState State(GuestCheckInSession session, FlowContext context)
    {
        var check = ReadCheck(session);
        var minor = context.IsMinor || check?.Check.HolderIsMinor == true;
        var offered = context.Enabled && !minor && session.BiometricConsentRefusedAt is null;
        var operatorPath = InOperatorPath(session, context);
        var passed = session.BiometricOutcome == BiometricOutcome.Accept;

        // A document of one picture counts as checked as soon as it was read (the agent says what is wrong at the selfie); a document of two sides only
        // when the face and the MRZ were found across the pictures, so a reloaded page asks for the missing side instead of the selfie.
        var documentChecked = check is not null && IsComplete(check);

        string? result = passed
            ? IdentityVerificationGuestResults.Ok
            : operatorPath
                ? IdentityVerificationGuestResults.Operator
                : session.BiometricAttempts > 0 ? IdentityVerificationGuestResults.Retry : null;
        var step = passed
            ? IdentityVerificationSteps.Done
            : operatorPath || !offered
                ? IdentityVerificationSteps.Operator
                : session.BiometricConsentAt is null
                    ? IdentityVerificationSteps.Consent
                    : documentChecked ? IdentityVerificationSteps.Selfie : IdentityVerificationSteps.Document;

        return new IdentityVerificationState(
            step,
            result,
            offered,
            session.BiometricConsentAt is not null,
            session.BiometricConsentRefusedAt is not null,
            documentChecked,
            Remaining(session, context.Policy),
            session.OperatorRequestedAt is not null || context.BookingWithOperator,
            context.DocumentNotArchived,
            context.VerifyingGuestPosition,
            check is null ? null : SidesOf(check.TwoSided, check.Front, check.Back),
            SideNeeded(check));
    }

    /// <summary>What every step needs: the settings of the org, the guest who verifies and whether the property keeps no document.</summary>
    private async Task<FlowContext> ContextAsync(GuestCheckInSession session, CancellationToken ct)
    {
        var (enabled, policy) = await PolicyAsync(session.OrgId, ct);

        var booking = session.Booking;
        var guests = await stayGuests.GetForBookingAsync(booking, ct);
        var verifying = guests.FirstOrDefault(g => AlloggiatiRecordRules.RequiresDocument(g.Type)) ?? guests.FirstOrDefault();
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
            await VerificationHandOff.IsWithOperatorAsync(db, booking.Id, ct));
    }

    /// <summary>The settings of the org, or the default of ADR-013-R13 when it has none: enabled on the Agency plan only.</summary>
    private async Task<(bool Enabled, VerificationPolicy Policy)> PolicyAsync(Guid orgId, CancellationToken ct)
    {
        var settings = await db.BiometricSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
            // The oldest row, the one BiometricSettingsService reads and writes (the table has no unique index on the org).
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
    /// prudent reading of the zero retention).
    /// </summary>
    private async Task<bool> DocumentNotArchivedAsync(Property property, CancellationToken ct)
    {
        if (!string.Equals(property.Country, SwitzerlandCountryCode, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(property.SwissMunicipalityCode))
            return true;

        var canton = await db.SwissMunicipalities
            .AsNoTracking()
            .Where(m => m.MunicipalityCode == property.SwissMunicipalityCode)
            .Select(m => m.CantonCode)
            .FirstOrDefaultAsync(ct);
        return canton is null || string.Equals(canton, TicinoCantonCode, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Whether a face was found on a picture where one is looked for (null: not looked for on the back, or the models could not say).</summary>
    private static bool? FaceOf(CaptureAssessment? assessment, DocumentSide side)
    {
        if (side == DocumentSide.Back || assessment is not { Assessed: true })
            return null;

        return !assessment.GuidanceCodes.Contains(CaptureGuidanceCodes.ShowDocumentPhoto, StringComparer.Ordinal);
    }

    /// <summary>Whether the light, the glare and the blur of a picture were good (the face apart); null when they were not assessed.</summary>
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
                    // The MRZ is not on the front of a card: «show the whole document», which the reading of this picture gave, would send the guest
                    // after something that is not there. The back is what is missing.
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

    /// <summary>
    /// A document of one picture is complete as soon as it was read, as it always was (the agent says what is wrong at the selfie); a document of two
    /// sides only when its front is there with a face (or no way to look for one) and the MRZ was read on a side.
    /// </summary>
    private static bool IsComplete(StoredDocumentCheck check) =>
        !check.TwoSided
        || (check.Front is { } front && front.Face != false && (front.Mrz is not null || check.Back?.Mrz is not null));

    /// <summary>The side still to photograph for a document of two sides: the front when it is missing or has no face, else the back when no MRZ was read.</summary>
    private static string? SideNeeded(StoredDocumentCheck? check)
    {
        if (check is null || !check.TwoSided)
            return null;

        if (check.Front is not { } front || front.Face == false)
            return DocumentSides.Front;

        return front.Mrz is null && check.Back?.Mrz is null ? DocumentSides.Back : null;
    }

    /// <summary>The pictures of the attempt as the page names them: <c>single</c>, or <c>front</c> and <c>back</c> for those that are there.</summary>
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
            // A value that is not one of ours: the document is read again.
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

            // One live row per booking is what this flow writes; should there be more, only the newest stays.
            foreach (var extra in live.Skip(1))
            {
                await DeleteObjectsAsync(extra, ct);
                extra.DeletedAt = now;
            }

            // The old picture of this side goes before the new one is written, as the single picture always did, so a failure leaves nothing behind.
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

    /// <summary>Encrypts a picture (Data Protection, <see cref="DocumentImageContentPurpose"/>) and writes it to the private bucket; the key it has there.</summary>
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

    /// <summary>Deletes one object of the private bucket; a blank address (a side that was never sent) is nothing to delete.</summary>
    private async Task DeleteObjectAsync(string? key, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key))
            await storage.DeleteAsync(StorageBucket.Private, key, ct);
    }

    /// <summary>Deletes the objects of both sides of a kept document.</summary>
    private async Task DeleteObjectsAsync(DocumentImage image, CancellationToken ct)
    {
        await DeleteObjectAsync(image.FrontImageUrl, ct);
        await DeleteObjectAsync(image.BackImageUrl, ct);
    }

    /// <summary>The kept picture of the document of the booking, decrypted; null when none is kept.</summary>
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

    /// <summary>Deletes the kept pictures of the booking, both sides (the objects, and the rows marked deleted): the verification no longer needs them.</summary>
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
            null,
            null,
            null,
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
        bool BookingWithOperator);
}

/// <summary>
/// One picture of the attempt as the session keeps it (E22/F01/US01/T02): whether a face was found on it (null: not looked for, as on a back, or the models
/// could not say), whether its light, glare and blur were good (null: not assessed) and, when its MRZ was read, the checks made on it. Flags only.
/// </summary>
internal sealed record StoredDocumentPicture(bool? Face, bool? QualityOk, DocumentCheck? Mrz);

/// <summary>
/// The checks of the document as the session keeps them (<see cref="GuestCheckInSession.DocumentCheckJson"/>): the flags across the pictures
/// (<see cref="Check"/>), the SHA-256 of the picture with the face (<see cref="PhotoSha256"/>, empty while only the back was sent) and of the back
/// (<see cref="BackSha256"/>), to recognise them when they come again with the selfie in Ticino, when. For a document of two sides
/// (<see cref="TwoSided"/>) what each picture showed. A record written before the two-sided document has none of the optional members and reads as a
/// single picture. No value read on the document.
/// </summary>
internal sealed record StoredDocumentCheck(
    DocumentCheck Check,
    string PhotoSha256,
    DateTime CheckedAt,
    string? BackSha256 = null,
    StoredDocumentPicture? Front = null,
    StoredDocumentPicture? Back = null,
    bool TwoSided = false);
