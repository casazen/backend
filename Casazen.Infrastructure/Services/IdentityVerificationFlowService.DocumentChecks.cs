using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The document step of the self-hosted identity verification (E71/F01/US03/T01, NF-189, NF-190, NF-191, <c>Features:CheckInIdentitySelfHosted</c> together with
/// <c>Features:CheckInIdentityVerification</c>): on top of the reading of today (<see cref="IdentityVerificationFlowService.SubmitDocumentAsync(GuestCheckInSession, Stream, DocumentSide, CancellationToken)"/>)
/// every picture of the document is checked, after the consent and the reading of its MRZ, and only the pictures that pass are kept. With the flow off none of this runs and the step
/// reads exactly as before.
/// </summary>
/// <remarks>
/// <para><b>The format</b> comes from the MRZ as soon as one is read (<see cref="DocumentFormatRegistry.DetectFromMrz"/>) and is kept in <see cref="GuestCheckInSession.IdentityFormatId"/>;
/// an MRZ of a format the registry does not hold closes the verification with the outcome <c>format_unsupported</c> (a person decides, counted apart). Before an MRZ is read the format is
/// not known and the checks use the card the registry holds (<see cref="DocumentPictureAnalyzer.DefaultCardFormat"/>, the Italian electronic identity card, the only registered two-sided
/// ID-1 layout): a second ID-1 format needs the MRZ read first, which only the back of a card has, so its front is still checked as the default until its back comes.</para>
/// <para><b>A picture is good or it is refused.</b> A picture with any guidance code (the geometry of its card, the side it shows or a repeated side, the quality of the capture) is refused
/// with those codes: nothing of it is kept, it is not an attempt (<c>BiometricAttempts</c> never moves) and it counts as a refusal of its side (<c>StoredDocumentCheck.Refusals</c>). After
/// <c>policy.document.maxRecapturesPerSide</c> refusals of one side the next refusal of it answers <c>document_unreadable</c> and a person decides (<c>review</c>).</para>
/// <para><b>Fail closed.</b> A model that fails, a policy file or a registry that is not usable, a service that is not there: the verification goes to a person and nothing is approved.</para>
/// <para><b>Privacy</b>: the log names the session, the side, the codes and the counts; never a picture, a value read, an MRZ text or a hash. The values read live in the memory of the
/// request, are handed to the checker of the coherence and dropped; what is kept is flags, hashes and the names and results of the checks.</para>
/// </remarks>
public sealed partial class IdentityVerificationFlowService
{
    /// <summary>The services and the policy of the step, all there, or null: the step then cannot be checked.</summary>
    private sealed record DocumentStepServices(
        DocumentPictureAnalyzer Pictures,
        DocumentSideClassifier Sides,
        DocumentCoherenceChecker Coherence,
        DocumentFormatRegistry Formats,
        IdentityDecisionPolicy Policy);

    /// <summary>What a picture showed and passed: the format it was checked as and whether the MRZ detected it, the side it showed, its difference hash and the values read on its card.</summary>
    private sealed record DocumentPictureChecks(
        DocumentFormat Format,
        bool FormatDetected,
        string? ClassifiedSide,
        string DHash,
        IReadOnlyList<ZonedField> Fields);

    /// <summary>The answer of the checks of one picture: either the answer to the guest (a refusal, a hand-off) or the checks it passed.</summary>
    private sealed record DocumentPictureStep(DocumentStepResult? Answer, DocumentPictureChecks? Checks);

    /// <summary>The other side of a document read again from its kept picture: the values read by zone and the reading of its MRZ.</summary>
    private sealed record KeptSide(IReadOnlyList<ZonedField> Fields, MrzReading Mrz);

    /// <summary>
    /// The services of the document step are all there. The application registers them (the part <c>DocumentStep</c> and the ones it relies on); a flow built by hand without them (a
    /// tool, a test of another task) reads the document as it always did, and a policy or a registry that is registered but cannot be used is a person, never a pass. That the application
    /// does register them all is proved by <c>DocumentStepRegistrationTests</c>: a service that stopped being registered would turn the checks off in silence, and that test fails.
    /// </summary>
    internal bool DocumentChecksWired =>
        documentPictures is not null && sideClassifier is not null && coherenceChecker is not null && formats is not null && identityPolicy is not null;

    /// <summary>The checks of the self-hosted document step run: the flow is on (<see cref="SelfHostedOn"/>) and its services are there (<see cref="DocumentChecksWired"/>).</summary>
    private bool DocumentStepOn => SelfHostedOn && DocumentChecksWired;

    /// <summary>
    /// The document is complete: for a document of two sides (or of one) the face and the MRZ were found across its pictures (<see cref="IsComplete"/>), and with the checks of the
    /// self-hosted document step on (<see cref="DocumentStepOn"/>) every side of its format passed its checks and the format is known (a picture of the self-hosted flow that did not
    /// pass is never kept, so a kept picture has the geometry ok; one written before the flow was on has not).
    /// </summary>
    private bool DocumentComplete(GuestCheckInSession session, StoredDocumentCheck check)
    {
        if (!IsComplete(check))
            return false;

        if (!DocumentStepOn)
            return true;

        return check.Front?.GeometryOk == true
            && (!check.TwoSided || check.Back?.GeometryOk == true)
            && !string.IsNullOrEmpty(session.IdentityFormatId);
    }

    /// <summary>The guidance of a picture in which nothing of a document was read: the codes of the capture checks, then «show the whole document» and «not recognized».</summary>
    private static List<string> RejectionGuidance(CaptureAssessment? assessment, MrzReading mrzReading) =>
        (assessment?.GuidanceCodes ?? [])
            .Concat(CaptureGuidanceCodes.FromMrzQuality(mrzReading.Quality))
            .Append(CaptureGuidanceCodes.ShowWholeDocument)
            .Append("document_not_recognized")
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private DocumentStepServices? StepServices()
    {
        var snapshot = identityPolicy?.Snapshot;
        if (documentPictures is null || sideClassifier is null || coherenceChecker is null || formats is null || snapshot is null || !snapshot.IsReady)
            return null;

        return new DocumentStepServices(documentPictures, sideClassifier, coherenceChecker, formats, snapshot.Policy);
    }

    /// <summary>
    /// Checks one picture of the document (the geometry, the side and the format, the quality), reads its fields by zone and answers: a refusal with its codes, a hand-off to a person,
    /// or the checks the picture passed. Only called with the self-hosted flow on and after the consent; <paramref name="reading"/> and <paramref name="mrzReading"/> are the readings of today.
    /// </summary>
    private async Task<DocumentPictureStep> CheckDocumentPictureAsync(
        GuestCheckInSession session,
        FlowContext context,
        byte[] bytes,
        DocumentSide side,
        MrzReading mrzReading,
        DocumentReading reading,
        CaptureAssessment? assessment,
        CancellationToken ct)
    {
        var services = StepServices();
        if (services is null)
        {
            logger.LogWarning("Document step of session {SessionId}: the policy of the self-hosted flow is not usable; a person decides", session.Id);
            return Answer(await HandOffDocumentAsync(
                session, context, side, IdentityOutcomeCodes.Unavailable, VerificationAlertReasons.TechnicalError, IdentityGuidanceCodes.DocumentUnreadable, ct));
        }

        // The SHA-256 of this picture: it is what a refusal remembers (the same file refused again is not counted twice) and what the check of a pair of sides compares.
        var pictureSha256 = Sha256(bytes);
        var policy = services.Policy;
        Task<DocumentStepResult> Refuse(IReadOnlyList<string> codes) => RefuseDocumentAsync(session, context, side, codes, pictureSha256, policy, ct);

        // The format: from the MRZ as soon as one is read; before, the one of the attempt, else the card the registry holds.
        DocumentFormat? format;
        var detected = false;
        if (mrzReading.Result is { } machine)
        {
            format = services.Formats.DetectFromMrz(machine.DocumentCode, machine.IssuingState, machine.MrzType);
            if (format is null)
            {
                // Only an MRZ whose check digits all hold is evidence of a format the registry does not know. The document code and the issuing state of its first line are covered by
                // no check digit, so one character the OCR read wrong (an I read as 1) would close the verification for good and be counted as an unsupported format: a doubtful MRZ
                // is a picture to take again, refused with the guidance of an unreadable reading and counted as any refusal.
                if (!machine.CheckDigitsValid)
                {
                    logger.LogInformation("Document step of session {SessionId}: the MRZ is doubtful and its format is not in the registry; the picture is asked again", session.Id);
                    return Answer(await Refuse(RejectionGuidance(assessment, mrzReading)));
                }

                logger.LogInformation("Document step of session {SessionId}: the format of the document is not in the registry; a person decides", session.Id);
                return Answer(await HandOffDocumentAsync(
                    session, context, side, IdentityOutcomeCodes.FormatUnsupported, VerificationAlertReasons.Review, IdentityGuidanceCodes.DocumentFormatUnsupported, ct));
            }

            detected = true;
        }
        else
        {
            format = services.Formats.Get(session.IdentityFormatId) ?? DocumentPictureAnalyzer.DefaultCardFormat(services.Formats.All);
            if (format is null)
            {
                logger.LogWarning("Document step of session {SessionId}: the registry holds no format to check the document as; a person decides", session.Id);
                return Answer(await HandOffDocumentAsync(
                    session, context, side, IdentityOutcomeCodes.Unavailable, VerificationAlertReasons.TechnicalError, IdentityGuidanceCodes.DocumentUnreadable, ct));
            }
        }

        // A side the format does not have (a card of two sides sent as one picture, a page of one side sent as the front): the guest is asked for a side of the format.
        if (!format.Sides.Contains(DocumentSides.CodeOf(side), StringComparer.Ordinal))
            return Answer(await Refuse([IdentityGuidanceCodes.DocumentWrongSide]));

        DocumentPicture picture;
        try
        {
            picture = services.Pictures.Open(bytes);
        }
        catch (InvalidDataException)
        {
            // Not a picture that can be decoded: «retake the picture», the guidance of the capture checks.
            return Answer(await Refuse([CaptureGuidanceCodes.RetakePicture]));
        }

        using (picture)
        {
            DocumentGeometryReport report;
            ulong hash;
            try
            {
                report = picture.Inspect(format, side, ct);
                hash = picture.DHash();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A model that is not there or fails never approves (D8): the type only is logged, a person decides.
                logger.LogError("Document geometry of session {SessionId} failed ({ErrorType}); a person decides", session.Id, ex.GetType().Name);
                return Answer(await HandOffDocumentAsync(
                    session, context, side, IdentityOutcomeCodes.TechnicalError, VerificationAlertReasons.TechnicalError, IdentityGuidanceCodes.DocumentUnreadable, ct));
            }

            // The side the picture shows by its content, and whether it repeats the other side: the first code, since the guest is asked for the other side either way.
            var shown = services.Sides.Classify(report, mrzReading.Result is not null, format);
            var fingerprint = new DocumentPictureFingerprint(pictureSha256, hash, shown);
            var pair = services.Sides.CheckPair(
                side, fingerprint, OtherSideFingerprint(ReadCheck(session), side), services.Policy.Document.PerceptualDuplicateMaxDistance);

            var codes = new List<string>();
            if (pair is not null)
                codes.Add(pair);

            codes.AddRange(report.Codes);
            codes.AddRange(assessment?.GuidanceCodes ?? []);
            var refusal = codes.Distinct(StringComparer.Ordinal).ToList();
            if (refusal.Count > 0)
                return Answer(await Refuse(refusal));

            // Nothing of a document was read in a picture whose geometry is good: it is refused as it always was, and counted.
            if (reading.Method == DocumentReadingMethod.None)
                return Answer(await Refuse(RejectionGuidance(assessment, mrzReading)));

            var fields = await picture.ReadFieldsAsync(format, side, report, ct);
            return new DocumentPictureStep(
                null,
                new DocumentPictureChecks(format, detected, shown is { } showed ? DocumentSides.CodeOf(showed) : null, PerceptualHash.ToHex(hash), fields));
        }
    }

    private static DocumentPictureStep Answer(DocumentStepResult answer) => new(answer, null);

    /// <summary>
    /// The picture of the other side of the attempt as the check of a pair of sides needs it (its SHA-256, its difference hash and the side it showed), or null when the other side was
    /// not sent (or the picture is a single one). A hash the record has not, or cannot be read, is null and never a plain picture's 0.
    /// </summary>
    private static DocumentPictureFingerprint? OtherSideFingerprint(StoredDocumentCheck? stored, DocumentSide side)
    {
        if (stored is null || side == DocumentSide.Single)
            return null;

        var (other, sha) = side == DocumentSide.Front ? (stored.Back, stored.BackSha256) : (stored.Front, stored.PhotoSha256);
        if (other is null)
            return null;

        ulong? hash = PerceptualHash.TryParseHex(other.DHash, out var parsedHash) ? parsedHash : null;
        DocumentSide? shown = !string.IsNullOrWhiteSpace(other.ClassifiedSide) && DocumentSides.TryParse(other.ClassifiedSide, out var parsedSide) ? parsedSide : null;
        return new DocumentPictureFingerprint(sha ?? string.Empty, hash, shown);
    }

    /// <summary>
    /// Refuses a picture with its guidance <paramref name="codes"/>: nothing of it is kept and it is not an attempt, but it counts as a refusal of its side. The refusal that goes
    /// beyond <c>policy.document.maxRecapturesPerSide</c> answers <c>document_unreadable</c> instead, and a person decides (<c>review</c>). The same file refused again (the SHA-256
    /// <paramref name="pictureSha256"/> is the one of the picture last refused for this side: a request the network repeated, a double submit) is refused with the same codes and is not
    /// counted a second time. The record is read here, under the lock of the row of the session (<c>SubmitDocumentAsync</c>), so two refusals at the same time are two refusals.
    /// </summary>
    private async Task<DocumentStepResult> RefuseDocumentAsync(
        GuestCheckInSession session,
        FlowContext context,
        DocumentSide side,
        IReadOnlyList<string> codes,
        string pictureSha256,
        IdentityDecisionPolicy policy,
        CancellationToken ct)
    {
        var now = UtcNow();
        var sideCode = DocumentSides.CodeOf(side);
        var previous = ReadCheck(session) ?? NewDocumentCheck(session, context, now);
        if (previous.RefusedSha256 is { } lastRefused
            && lastRefused.TryGetValue(sideCode, out var lastSha)
            && string.Equals(lastSha, pictureSha256, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Document step of session {SessionId}: the same picture of the {Side} side was refused again ({Codes}), not counted twice",
                session.Id, sideCode, string.Join(',', codes));
            return new DocumentStepResult(
                DocumentReading.Nothing, false, codes, false, context.DocumentNotArchived, side, SidesOf(previous.TwoSided, previous.Front, previous.Back));
        }

        var refusals = new Dictionary<string, int>(previous.Refusals ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        refusals[sideCode] = refusals.GetValueOrDefault(sideCode) + 1;
        var refused = new Dictionary<string, string>(previous.RefusedSha256 ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [sideCode] = pictureSha256 };
        var kept = previous with { Refusals = refusals, RefusedSha256 = refused };
        session.DocumentCheckJson = JsonSerializer.Serialize(kept, Json);
        session.UpdatedAt = now;

        var sides = SidesOf(kept.TwoSided, kept.Front, kept.Back);
        if (refusals[sideCode] > policy.Document.MaxRecapturesPerSide)
        {
            logger.LogInformation(
                "Document step of session {SessionId}: the {Side} side was refused {Refusals} times; a person decides", session.Id, sideCode, refusals[sideCode]);
            CloseIdentitySession(session, IdentityOutcomeCodes.Review);
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, VerificationAlertReasons.Review, ct);
            return new DocumentStepResult(
                DocumentReading.Nothing, false, [IdentityGuidanceCodes.DocumentUnreadable], false, context.DocumentNotArchived, side, sides);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Document step of session {SessionId}: a picture of the {Side} side was refused ({Codes}), refusal {Refusals} of {Max}",
            session.Id, sideCode, string.Join(',', codes), refusals[sideCode], policy.Document.MaxRecapturesPerSide);
        return new DocumentStepResult(DocumentReading.Nothing, false, codes, false, context.DocumentNotArchived, side, sides);
    }

    /// <summary>
    /// Hands the verification to a person from the document step: it closes with <paramref name="outcomeCode"/> (a code of <see cref="IdentityOutcomeCodes"/>), the kept pictures
    /// go, the operators are alerted with <paramref name="alertReason"/> and the guest is answered with <paramref name="guidanceCode"/>, the one code the page needs to say that a person
    /// takes over (the state says the same: the step is the operator's and the verification is closed).
    /// </summary>
    private async Task<DocumentStepResult> HandOffDocumentAsync(
        GuestCheckInSession session,
        FlowContext context,
        DocumentSide side,
        string outcomeCode,
        string alertReason,
        string guidanceCode,
        CancellationToken ct)
    {
        CloseIdentitySession(session, outcomeCode);
        await DeleteStoredDocumentAsync(session, ct);
        await ToOperatorAsync(session, alertReason, ct);
        var existing = ReadCheck(session);
        return new DocumentStepResult(
            DocumentReading.Nothing,
            false,
            [guidanceCode],
            false,
            context.DocumentNotArchived,
            side,
            existing is null ? [] : SidesOf(existing.TwoSided, existing.Front, existing.Back));
    }

    /// <summary>
    /// The record of a document of which no picture was kept yet, that holds the refusals: of two sides, so it is never complete, and with no picture and no hash.
    /// </summary>
    private static StoredDocumentCheck NewDocumentCheck(GuestCheckInSession session, FlowContext context, DateTime now) =>
        new(
            new DocumentCheck(false, false, null, null, null, null, null, false),
            string.Empty,
            now,
            TwoSided: true,
            VerifyingGuestPosition: context.VerifyingGuestPosition,
            ProofKey: session.TokenHash);

    /// <summary>
    /// The entries of a record kept for each side (<c>front</c>, <c>back</c>, <c>single</c>) without the one of <paramref name="side"/>, null when none is left: a side that was accepted
    /// is forgiven what it cost, so the next refusals of it start from none.
    /// </summary>
    private static IReadOnlyDictionary<string, T>? WithoutSide<T>(IReadOnlyDictionary<string, T>? entries, DocumentSide side)
    {
        if (entries is null)
            return null;

        var sideCode = DocumentSides.CodeOf(side);
        var rest = entries
            .Where(entry => !string.Equals(entry.Key, sideCode, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return rest.Count == 0 ? null : rest;
    }

    /// <summary>
    /// The coherence of the sides (<see cref="DocumentCoherenceChecker"/>) as its entries, names and results only, once every side of the format is in; null before, and null when the
    /// values of a side cannot be had at the same time as the ones of this picture. The values of a picture are never kept, so the other side of a document of two sides is read again from
    /// its kept picture (outside Ticino, where it is kept encrypted): in Ticino nothing is kept, the two sides are never in memory together here and the checks across them stay unknown
    /// (never a pass) until the pictures come again. An identifying conflict, an invalid MRZ or an expired document do not stop the step: they are in the entries for the decision.
    /// </summary>
    private async Task<IReadOnlyList<DocumentCoherenceCheck>?> CheckCoherenceAsync(
        GuestCheckInSession session,
        FlowContext context,
        DocumentPictureChecks self,
        DocumentSide side,
        MrzReading mrzReading,
        StoredDocumentPicture? front,
        StoredDocumentPicture? back,
        CancellationToken ct)
    {
        var services = StepServices();
        if (services is null)
            return null;

        var format = self.Format;
        var fields = new List<ZonedField>(self.Fields);
        MrzReading? machine = mrzReading.Result is null ? null : mrzReading;
        if (format.Sides.Count > 1)
        {
            if (front is null || back is null)
                return null;

            var kept = await ReadKeptSideAsync(session, context, services, format, side == DocumentSide.Front ? DocumentSide.Back : DocumentSide.Front, ct);
            if (kept is null)
                return null;

            fields.AddRange(kept.Fields);
            machine ??= kept.Mrz;
        }
        else if (front is null)
        {
            return null;
        }

        var report = services.Coherence.Check(
            fields, machine, new DocumentCoherenceContext(DateOnly.FromDateTime(session.Booking.CheckInDate), context.Identity), format, services.Policy);
        return report.Checks;
    }

    /// <summary>
    /// Reads the kept picture of <paramref name="side"/> again: its MRZ with the check digits and its fields by zone. Null when nothing is kept (Ticino, a minor, a side never sent). A
    /// picture that cannot be read again is a side with no field: the checks that need it stay unknown. The storage and Data Protection errors propagate (the guest tries again), as
    /// they do when the selfie loads the kept picture.
    /// </summary>
    private async Task<KeptSide?> ReadKeptSideAsync(
        GuestCheckInSession session, FlowContext context, DocumentStepServices services, DocumentFormat format, DocumentSide side, CancellationToken ct)
    {
        if (context.DocumentNotArchived)
            return null;

        byte[]? bytes = null;
        try
        {
            bytes = await LoadStoredSideAsync(session, side, ct);
            if (bytes is null)
                return null;

            MrzReading machine;
            using (var stream = new MemoryStream(bytes, writable: false))
                machine = await mrz.ReadAsync(stream, ct);

            IReadOnlyList<ZonedField> fields = [];
            try
            {
                using var picture = services.Pictures.Open(bytes);
                var report = picture.Inspect(format, side, ct);
                fields = await picture.ReadFieldsAsync(format, side, report, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The type only: a message could carry what was being read.
                logger.LogWarning(
                    "Document step of session {SessionId}: the {Side} side kept could not be read again ({ErrorType})", session.Id, DocumentSides.CodeOf(side), ex.GetType().Name);
            }

            return new KeptSide(fields, machine);
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    /// <summary>The kept picture of one side of the document of the booking, decrypted; null when none is kept.</summary>
    private async Task<byte[]?> LoadStoredSideAsync(GuestCheckInSession session, DocumentSide side, CancellationToken ct)
    {
        var row = await db.DocumentImages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(i => i.OrgId == session.OrgId && i.BookingId == session.BookingId && i.DeletedAt == null)
            .OrderByDescending(i => i.UploadedAt)
            .Select(i => new { i.FrontImageUrl, i.BackImageUrl })
            .FirstOrDefaultAsync(ct);
        var key = side == DocumentSide.Back ? row?.BackImageUrl : row?.FrontImageUrl;
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
}
