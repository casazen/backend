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
/// <para><b>Lock code</b>: only on <c>ok</c>, through the gate of <see cref="IAccessCodeService"/> (channels <c>checkin-screen</c> and
/// <c>email</c>); a retry, a review, a refusal, a request for the operator or an error release and send nothing.</para>
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
            await ToOperatorAsync(session, VerificationAlertReasons.BiometricsDisabled, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        if (context.IsMinor)
        {
            await ToOperatorAsync(session, VerificationAlertReasons.MinorExcluded, ct);
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));
        }

        // Already with the operator (a refusal, a request, a review): the person decides, the biometric path is not opened again.
        if (InOperatorPath(session, context.Policy))
            return new BiometricConsentResult(BiometricConsentStatus.Operator, State(session, context));

        session.BiometricConsentAt = now;
        session.BiometricConsentVersion = version;
        session.BiometricConsentRefusedAt = null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Biometric consent recorded for session {SessionId}", session.Id);
        return new BiometricConsentResult(BiometricConsentStatus.Recorded, State(session, context));
    }

    public async Task<DocumentStepResult> SubmitDocumentAsync(GuestCheckInSession session, Stream image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(image);
        var context = await ContextAsync(session, ct);

        byte[]? bytes = null;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(image, MaxImageBytes, ct);
            var consented = context.Enabled && session.BiometricConsentAt is not null;

            // The face of the document is looked for only after the consent; the MRZ hints give the guidance of the reading in any case.
            CaptureAssessment? assessment = null;
            if (consented)
            {
                using var copy = new MemoryStream(bytes, writable: false);
                assessment = await agent.AssessCaptureAsync(copy, CaptureKind.Document, ct);
            }

            MrzReading mrzReading;
            using (var copy = new MemoryStream(bytes, writable: false))
                mrzReading = await mrz.ReadAsync(copy, ct);

            DocumentReading reading;
            if (mrzReading.Result is { } fromMrz)
            {
                reading = FromMrz(fromMrz);
            }
            else
            {
                using var copy = new MemoryStream(bytes, writable: false);
                var fromOcr = await ocr.ExtractAsync(copy, ct);
                reading = fromOcr is null ? DocumentReading.Nothing : FromOcr(fromOcr);
            }

            var guidance = (assessment?.GuidanceCodes ?? [])
                .Concat(CaptureGuidanceCodes.FromMrzQuality(mrzReading.Quality))
                .ToList();
            if (mrzReading.Result is null)
                guidance.Add(CaptureGuidanceCodes.ShowWholeDocument);

            var acceptable = mrzReading.Result is not null && (assessment is null || !assessment.Assessed || assessment.Acceptable);

            var check = Check(mrzReading.Result, context.Identity);
            var now = UtcNow();
            session.DocumentCheckJson = JsonSerializer.Serialize(new StoredDocumentCheck(check, Sha256(bytes), now), Json);
            session.UpdatedAt = now;

            var stored = false;
            if (!context.DocumentNotArchived
                && consented
                && !check.HolderIsMinor
                && session.BiometricOutcome != BiometricOutcome.Accept
                && !InOperatorPath(session, context.Policy))
            {
                await StoreDocumentAsync(session, bytes, now, ct);
                stored = true;
            }
            else
            {
                // A new picture replaces the old one: a kept picture of an earlier upload must not stay behind.
                await DeleteStoredDocumentAsync(session, ct);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Document of session {SessionId} read: method={Method} mrz={MrzRead} acceptable={Acceptable} stored={Stored}",
                session.Id, reading.Method, check.MrzRead, acceptable, stored);

            return new DocumentStepResult(reading, acceptable, guidance.Distinct(StringComparer.Ordinal).ToList(), stored, context.DocumentNotArchived);
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    public async Task<SelfieStepResult> SubmitSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? documentPhoto, CancellationToken ct)
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
            return Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), code);
        }

        if (InOperatorPath(session, policy))
            return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);

        if (!context.Enabled)
            return await OperatorAsync(session, VerificationAlertReasons.BiometricsDisabled, policy, ct);

        if (context.IsMinor)
            return await OperatorAsync(session, VerificationAlertReasons.MinorExcluded, policy, ct);

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

        if (session.BiometricAttempts >= policy.MaxAttempts)
            return await OperatorAsync(session, VerificationAlertReasons.AttemptsExhausted, policy, ct);

        var attempt = session.BiometricAttempts + 1;
        IdentityVerificationOutcome outcome;
        byte[]? documentBytes = null;
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

            using var documentStream = new MemoryStream(documentBytes, writable: false);
            outcome = await agent.VerifyAsync(new IdentityVerificationRequest(selfie, documentStream, stored.Check, policy, attempt), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document could not be loaded or the verification failed unexpectedly. The safe default is a person: the session says why,
            // the operators are alerted, the log keeps the type only (a message could carry what was being read).
            logger.LogError("Identity verification of session {SessionId} failed ({ErrorType}); it goes to the operator", session.Id, ex.GetType().Name);
            await DeleteStoredDocumentAsync(session, ct);
            return await OperatorAsync(session, VerificationAlertReasons.TechnicalError, policy, ct);
        }
        finally
        {
            ImageBytes.Zero(documentBytes);
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
                return Done(IdentityVerificationGuestResults.Ok, [], Remaining(session, policy), await ReleaseCodeAsync(session, ct));

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
            logger.LogInformation("Identity verification of session {SessionId} passed: the property has no lock code to release", session.Id);
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
    /// with the session). A later reason does not alert again.
    /// </summary>
    private async Task ToOperatorAsync(GuestCheckInSession session, string reason, CancellationToken ct)
    {
        if (session.OperatorRequestedAt is null)
        {
            var now = UtcNow();
            session.OperatorRequestedAt = now;
            session.UpdatedAt = now;
            await alerts.RaiseAsync(session.Id, reason, ct);
            logger.LogInformation("Identity verification of session {SessionId} handed to the operator ({Reason})", session.Id, reason);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<SelfieStepResult> OperatorAsync(GuestCheckInSession session, string reason, VerificationPolicy policy, CancellationToken ct)
    {
        await ToOperatorAsync(session, reason, ct);
        return Done(IdentityVerificationGuestResults.Operator, [], Remaining(session, policy), null);
    }

    private static SelfieStepResult Done(string result, IReadOnlyList<string> guidance, int remaining, string? lockCode) =>
        new(SelfieStepStatus.Done, result, guidance, remaining, lockCode);

    private static SelfieStepResult Refused(SelfieStepStatus status, GuestCheckInSession session, VerificationPolicy policy) =>
        new(status, IdentityVerificationGuestResults.Retry, [], Remaining(session, policy), null);

    private static int Remaining(GuestCheckInSession session, VerificationPolicy policy) =>
        Math.Max(0, policy.MaxAttempts - session.BiometricAttempts);

    /// <summary>The session is with the operator: a request, a refusal, a review, or the attempts used without a pass.</summary>
    private static bool InOperatorPath(GuestCheckInSession session, VerificationPolicy policy) =>
        session.OperatorRequestedAt is not null
        || session.BiometricConsentRefusedAt is not null
        || session.BiometricOutcome == BiometricOutcome.Escalate
        || (session.BiometricAttempts >= policy.MaxAttempts && session.BiometricOutcome != BiometricOutcome.Accept);

    private IdentityVerificationState State(GuestCheckInSession session, FlowContext context)
    {
        var check = ReadCheck(session);
        var minor = context.IsMinor || check?.Check.HolderIsMinor == true;
        var offered = context.Enabled && !minor && session.BiometricConsentRefusedAt is null;
        var operatorPath = InOperatorPath(session, context.Policy);
        var passed = session.BiometricOutcome == BiometricOutcome.Accept;

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
                    : check is null ? IdentityVerificationSteps.Document : IdentityVerificationSteps.Selfie;

        return new IdentityVerificationState(
            step,
            result,
            offered,
            session.BiometricConsentAt is not null,
            session.BiometricConsentRefusedAt is not null,
            check is not null,
            Remaining(session, context.Policy),
            session.OperatorRequestedAt is not null,
            context.DocumentNotArchived,
            context.VerifyingGuestPosition);
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
            await DocumentNotArchivedAsync(booking.Property, ct));
    }

    /// <summary>The settings of the org, or the default of ADR-013-R13 when it has none: enabled on the Agency plan only.</summary>
    private async Task<(bool Enabled, VerificationPolicy Policy)> PolicyAsync(Guid orgId, CancellationToken ct)
    {
        var settings = await db.BiometricSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
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

    /// <summary>Keeps the picture encrypted in the private bucket with its <see cref="DocumentImage"/> row, replacing the one kept before.</summary>
    private async Task StoreDocumentAsync(GuestCheckInSession session, byte[] bytes, DateTime now, CancellationToken ct)
    {
        await DeleteStoredDocumentAsync(session, ct);

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

        db.DocumentImages.Add(new DocumentImage
        {
            OrgId = session.OrgId,
            GuestId = session.Booking.GuestId,
            BookingId = session.BookingId,
            FrontImageUrl = key,
            UploadedAt = now,
            ProcessedAt = now,
        });
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

    /// <summary>Deletes the kept pictures of the booking (object and row marked deleted): the verification no longer needs them.</summary>
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
            await storage.DeleteAsync(StorageBucket.Private, image.FrontImageUrl, ct);
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
        bool DocumentNotArchived);
}

/// <summary>
/// The checks of the document as the session keeps them (<see cref="GuestCheckInSession.DocumentCheckJson"/>): flags, the SHA-256 of the picture
/// (to recognise it when it comes again with the selfie), when. No value read on the document.
/// </summary>
internal sealed record StoredDocumentCheck(DocumentCheck Check, string PhotoSha256, DateTime CheckedAt);
