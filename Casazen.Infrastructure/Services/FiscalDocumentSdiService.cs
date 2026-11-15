using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalDocumentSdiService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One document at a time.</b> A submission, an outcome and a declaration of the same document run under the transaction-scoped advisory lock
/// <see cref="PostgresAdvisoryLocks.Scope.FiscalDocumentSdi"/> of the document, so a double click, the webhook and the poll job never submit it twice or apply two outcomes at once; the state
/// is read again under the lock. The submission holds it across the call to the provider (the checkout of the plans does the same with Stripe).</item>
/// <item><b>The provider's answer is all there is.</b> «Submitted» needs an accepted answer with the id of the submission; a refusal keeps the document <c>to_send</c> with the code of the
/// refusal (<see cref="SdiSubmissionErrors"/>) and a short cleaned note, never the file. A failure of the transport is the adapter's refusal <c>provider_error</c>; any other exception is
/// not caught here.</item>
/// <item><b>Logs</b> carry ids, states and codes: never the XML, a name, a fiscal id or the text of a notification.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentSdiService(
    AppDbContext db,
    ISdiEInvoiceProvider provider,
    IDomainEventRecorder events,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentSdiService> logger,
    IFeatureFlags? featureFlags = null) : IFiscalDocumentSdiService
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<FiscalDocumentSdiState?> GetStateAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new StateSource(d.Source, d.Status, d.Purpose, d.SdiStatus, d.SdiErrorCode, d.SdiErrorDetail, d.SdiStatusUpdatedAt, false))
            .FirstOrDefaultAsync(ct);
        return document is null ? null : StateOf(documentId, document with { Credited = await HasIssuedCreditNoteAsync(documentId, ct) });
    }

    public async Task<SdiSubmitResult> SubmitAsync(Guid documentId, CancellationToken ct = default)
    {
        // The quick refusals first (a read, no lock): the document is not found, imported, or no electronic invoice.
        var head = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Source, d.Status, d.SdiStatus })
            .FirstOrDefaultAsync(ct)
            ?? throw NotFound(documentId);
        RequireElectronic(head.Source, head.Status, head.SdiStatus);

        // No channel (no provider, or the module is off): nothing is sent and nothing changes. The document stays «to send» and can be uploaded by hand and declared.
        if (!ChannelIsOpen)
        {
            logger.LogInformation("SDI submission of document {DocumentId}: no provider is configured, nothing is sent", documentId);
            return new SdiSubmitResult(SdiSubmitOutcome.NotConfigured, head.SdiStatus);
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.FiscalDocumentSdi, LockKey(documentId)));

        // The state again, under the lock: another request may have submitted it while this one waited.
        var document = await db.FiscalDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw NotFound(documentId);
        RequireElectronic(document.Source, document.Status, document.SdiStatus);
        if (document.SdiStatus == FiscalDocumentSdiStatuses.Submitted)
            return new SdiSubmitResult(SdiSubmitOutcome.AlreadySubmitted, document.SdiStatus);
        if (document.SdiStatus != FiscalDocumentSdiStatuses.ToSend)
            throw new DomainConflictException(FiscalDocumentSdiErrors.NotToSend, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotToSend));

        var submission = BuildSubmission(document);
        var answer = await provider.SubmitDocumentAsync(submission, ct);

        var now = UtcNow;
        if (answer.Accepted && !string.IsNullOrWhiteSpace(answer.TransmissionId))
        {
            document.SdiStatus = FiscalDocumentSdiStatuses.Submitted;
            document.SdiTransmissionId = Truncate(answer.TransmissionId.Trim(), FiscalDocument.SdiTransmissionIdMaxLength);
            document.SdiErrorCode = null;
            document.SdiErrorDetail = null;
            document.SdiStatusUpdatedAt = now;
            document.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} submitted to the SDI", document.Id, document.OrgId);
            return new SdiSubmitResult(SdiSubmitOutcome.Submitted, document.SdiStatus);
        }

        // A refusal (or an answer that accepted without the id of the submission, which is no acceptance): the document stays «to send», with why.
        var code = CodeOfRefusal(answer);
        document.SdiErrorCode = code;
        document.SdiErrorDetail = SdiRejectionReasons.Clean(answer.Detail);
        document.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogWarning("The SDI submission of fiscal document {DocumentId} of org {OrgId} was refused ({Code}): it stays to send", document.Id, document.OrgId, code);
        return new SdiSubmitResult(SdiSubmitOutcome.Refused, document.SdiStatus, code);
    }

    public async Task<SdiRecordResult> RecordOutcomeAsync(Guid documentId, SdiOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.FiscalDocumentSdi, LockKey(documentId)));
        var document = await db.FiscalDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw NotFound(documentId);
        RequireElectronic(document.Source, document.Status, document.SdiStatus);

        var result = Apply(document, outcome);
        if (result == SdiRecordResult.Recorded)
            await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return result;
    }

    public async Task<FiscalDocumentSdiState> DeclareManuallyAsync(Guid documentId, SdiDeclaration declaration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.FiscalDocumentSdi, LockKey(documentId)));
        var document = await db.FiscalDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw NotFound(documentId);
        RequireElectronic(document.Source, document.Status, document.SdiStatus);
        var today = timeProvider.TodayInRomeAsDateOnly();
        var invalid = InvalidFieldsOf(declaration, document, today);
        if (invalid.Count > 0)
        {
            throw new DomainRuleException(FiscalDocumentSdiErrors.DeclarationInvalid, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.DeclarationInvalid))
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = invalid },
            };
        }

        if (declaration.Kind == SdiDeclarationKind.Rejected)
        {
            // The same path as the provider's answers: the state, the event and the idempotence are one. Only a document the provider has no say on is declared rejected.
            RequireNotWithTheProvider(document);
            Apply(document, new SdiOutcome(SdiOutcomeKind.Rejected, declaration.ErrorCode, declaration.ErrorText));
        }
        else
        {
            RequireNotWithTheProvider(document);
            if (document.SdiStatus is FiscalDocumentSdiStatuses.Delivered or FiscalDocumentSdiStatuses.Rejected)
                throw StateFinal();

            document.SdiStatus = FiscalDocumentSdiStatuses.ManualDeclared;
            document.SdiTransmissionId = Truncate(declaration.Reference!.Trim(), FiscalDocument.SdiTransmissionIdMaxLength);
            document.SdiErrorCode = null;
            document.SdiErrorDetail = null;
            document.SdiStatusUpdatedAt = RomeCalendar.StartOfDayUtc(declaration.Date!.Value);
            document.UpdatedAt = UtcNow;
            logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} declared sent by hand", document.Id, document.OrgId);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var credited = await HasIssuedCreditNoteAsync(document.Id, ct);
        return StateOf(
            document.Id,
            new StateSource(document.Source, document.Status, document.Purpose, document.SdiStatus, document.SdiErrorCode, document.SdiErrorDetail, document.SdiStatusUpdatedAt, credited));
    }

    public async Task<IReadOnlyList<SdiSubmittedDocument>> SubmittedDocumentsAsync(int limit, CancellationToken ct = default) =>
        await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Source == FiscalDocumentSource.CasaZen
                        && d.Status == FiscalDocumentStatus.Issued
                        && d.SdiStatus == FiscalDocumentSdiStatuses.Submitted
                        && d.SdiTransmissionId != null)
            .OrderBy(d => d.SdiStatusUpdatedAt)
            .ThenBy(d => d.Id)
            .Take(Math.Max(1, limit))
            .Select(d => new SdiSubmittedDocument(d.OrgId, d.Id, d.SdiTransmissionId!))
            .ToListAsync(ct);

    public async Task<SdiSubmittedDocument?> FindByTransmissionAsync(string transmissionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transmissionId) || transmissionId.Length > FiscalDocument.SdiTransmissionIdMaxLength)
            return null;

        var id = transmissionId.Trim();

        // A document declared by hand keeps the reference its issuer typed in the same column: it is never one the provider knows.
        return await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Source == FiscalDocumentSource.CasaZen
                        && d.SdiTransmissionId == id
                        && d.SdiStatus != FiscalDocumentSdiStatuses.ManualDeclared)
            .Select(d => new SdiSubmittedDocument(d.OrgId, d.Id, d.SdiTransmissionId!))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<SdiIssuerRegistration>> IssuerRegistrationsAsync(HostScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        var organizationVat = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.PartitaIvaNumber).FirstOrDefaultAsync(ct);
        var profiles = await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.CountryCode == OrgFiscalIssuerProfile.DefaultCountryCode)
            .Select(p => new { p.Id, p.PropertyOwnerId, p.VatNumber })
            .ToListAsync(ct);

        // Only the issuers with a VAT number send electronic invoices to the SDI: the organization (its number is on its tax profile) and the owners that have one.
        var issuers = profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.PropertyOwnerId is null ? organizationVat : p.VatNumber))
            .ToList();

        // A caller who reaches only some properties sees the issuers of the documents of those properties (the rule of the list of the documents), never «every issuer of the org».
        if (!scope.IsOrgWide && issuers.Count > 0)
        {
            var bookings = db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).Where(b => b.OrgId == orgId).InScope(scope);
            var reachable = await db.FiscalDocuments
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(d => d.OrgId == orgId && d.IssuerProfileId != null && d.BookingId != null && bookings.Any(b => b.Id == d.BookingId))
                .Select(d => d.IssuerProfileId!.Value)
                .Distinct()
                .ToListAsync(ct);
            issuers = issuers.Where(p => reachable.Contains(p.Id)).ToList();
        }

        if (issuers.Count == 0)
            return [];

        // The last refusal of each issuer because its VAT number is not enabled (the document stays «to send»: its last change is the refusal) and the last submission the provider accepted.
        var documentsOfTheOrg = db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Source == FiscalDocumentSource.CasaZen && d.Status == FiscalDocumentStatus.Issued && d.IssuerProfileId != null);
        var refused = await documentsOfTheOrg
            .Where(d => d.SdiStatus == FiscalDocumentSdiStatuses.ToSend && d.SdiErrorCode == SdiSubmissionErrors.IssuerPending)
            .GroupBy(d => d.IssuerProfileId!.Value)
            .Select(g => new { Issuer = g.Key, Last = g.Max(d => d.UpdatedAt) })
            .ToDictionaryAsync(r => r.Issuer, r => r.Last, ct);
        var accepted = await documentsOfTheOrg
            .Where(d => d.SdiStatus == FiscalDocumentSdiStatuses.Submitted || d.SdiStatus == FiscalDocumentSdiStatuses.Delivered)
            .GroupBy(d => d.IssuerProfileId!.Value)
            .Select(g => new { Issuer = g.Key, Last = g.Max(d => d.SdiStatusUpdatedAt) })
            .ToDictionaryAsync(a => a.Issuer, a => a.Last, ct);

        return issuers
            .Select(p => new SdiIssuerRegistration(p.Id, p.PropertyOwnerId, RegistrationStateOf(p.Id, refused, accepted)))
            .ToList();
    }

    /// <summary>
    /// Where an issuer stands: <c>pending</c> when the provider refused a submission of it because its VAT number is not enabled and accepted none after that refusal; <c>registered</c> when it
    /// accepted one (a later acceptance clears an older refusal: the provider enabled the number since); <c>unknown</c> when nothing was submitted yet.
    /// </summary>
    private static string RegistrationStateOf(Guid issuer, IReadOnlyDictionary<Guid, DateTime> refused, IReadOnlyDictionary<Guid, DateTime?> accepted)
    {
        var hasAcceptance = accepted.TryGetValue(issuer, out var lastAccepted);
        if (refused.TryGetValue(issuer, out var lastRefused) && (!hasAcceptance || lastAccepted is null || lastAccepted < lastRefused))
            return SdiIssuerRegistrationStates.Pending;

        return hasAcceptance ? SdiIssuerRegistrationStates.Registered : SdiIssuerRegistrationStates.Unknown;
    }

    // ---------------------------------------------------------------- the state machine

    /// <summary>
    /// Applies an outcome to the document (the caller holds its lock and saves): «submitted» changes nothing; a delivery needs the document <c>submitted</c>; a rejection needs it
    /// not delivered; a state already reached is no change; a contradiction is ignored and logged. The event is recorded here, in the unit of work of the caller.
    /// </summary>
    private SdiRecordResult Apply(FiscalDocument document, SdiOutcome outcome)
    {
        var current = document.SdiStatus;
        var now = UtcNow;
        switch (outcome.Kind)
        {
            case SdiOutcomeKind.Submitted:
                return SdiRecordResult.AlreadyRecorded;

            case SdiOutcomeKind.Delivered:
                if (current == FiscalDocumentSdiStatuses.Delivered)
                    return SdiRecordResult.AlreadyRecorded;
                if (current != FiscalDocumentSdiStatuses.Submitted)
                    return Ignored(document, outcome.Kind);

                document.SdiStatus = FiscalDocumentSdiStatuses.Delivered;
                document.SdiErrorCode = null;
                document.SdiErrorDetail = null;
                document.SdiStatusUpdatedAt = outcome.OccurredAt ?? now;
                document.UpdatedAt = now;
                Record(DomainEventType.FiscalDocumentSdiDelivered, document, null);
                logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} delivered by the SDI", document.Id, document.OrgId);
                return SdiRecordResult.Recorded;

            case SdiOutcomeKind.Rejected:
                if (current == FiscalDocumentSdiStatuses.Rejected)
                    return SdiRecordResult.AlreadyRecorded;
                if (current == FiscalDocumentSdiStatuses.Delivered)
                    return Ignored(document, outcome.Kind);

                var code = SdiRejectionReasons.Normalize(outcome.ErrorCode);
                document.SdiStatus = FiscalDocumentSdiStatuses.Rejected;
                document.SdiErrorCode = code is null ? null : Truncate(code, FiscalDocument.SdiErrorCodeMaxLength);
                document.SdiErrorDetail = SdiRejectionReasons.Clean(outcome.ErrorDetail);
                document.SdiStatusUpdatedAt = outcome.OccurredAt ?? now;
                document.UpdatedAt = now;
                Record(DomainEventType.FiscalDocumentSdiRejected, document, document.SdiErrorCode);
                logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} rejected by the SDI ({Code})", document.Id, document.OrgId, document.SdiErrorCode ?? "none");
                return SdiRecordResult.Recorded;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "Not an outcome of the SDI.");
        }
    }

    private SdiRecordResult Ignored(FiscalDocument document, SdiOutcomeKind kind)
    {
        logger.LogWarning(
            "Outcome {Outcome} of fiscal document {DocumentId} of org {OrgId} ignored: it contradicts the state {Status}",
            kind,
            document.Id,
            document.OrgId,
            document.SdiStatus);
        return SdiRecordResult.Ignored;
    }

    private void Record(DomainEventType type, FiscalDocument document, string? errorCode) =>
        events.Record(
            type,
            document.Id,
            new FiscalDocumentSdiPayload(document.Id, document.BookingId, document.SdiStatus, errorCode),
            dedupKey: $"{(type == DomainEventType.FiscalDocumentSdiRejected ? "fiscal-document-sdi-rejected" : "fiscal-document-sdi-delivered")}:{document.Id:N}",
            orgId: document.OrgId);

    // ---------------------------------------------------------------- rules

    /// <summary>The provider is configured and the module is on: a submission is attempted.</summary>
    private bool ChannelIsOpen => provider.IsConfigured && (featureFlags is null || featureFlags.IsEnabled(FeatureFlags.SdiSubmission));

    /// <summary>Only an issued electronic invoice of CasaZen goes to the SDI: an imported document is read-only, a receipt, a Swiss document and a draft are no electronic invoice.</summary>
    private static void RequireElectronic(FiscalDocumentSource source, FiscalDocumentStatus status, string sdiStatus)
    {
        if (source != FiscalDocumentSource.CasaZen)
            throw new DomainRuleException(FiscalDocumentErrors.ReadOnly, "FiscalDocumentReadOnly");
        if (status != FiscalDocumentStatus.Issued || sdiStatus is FiscalDocumentSdiStatuses.NotApplicable or FiscalDocumentSdiStatuses.Unknown)
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotElectronic, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotElectronic));
    }

    /// <summary>A document the provider has (submitted) is decided by the provider's answer, never by a declaration.</summary>
    private static void RequireNotWithTheProvider(FiscalDocument document)
    {
        if (document.SdiStatus == FiscalDocumentSdiStatuses.Submitted)
            throw new DomainConflictException(FiscalDocumentSdiErrors.NotToSend, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotToSend));
        if (document.SdiStatus == FiscalDocumentSdiStatuses.Delivered)
            throw StateFinal();
    }

    private static DomainConflictException StateFinal() =>
        new(FiscalDocumentSdiErrors.StateFinal, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.StateFinal));

    /// <summary>The fields of a declaration that are missing or not valid (never a value): the sending needs its day and its reference, a rejection its code.</summary>
    private static List<string> InvalidFieldsOf(SdiDeclaration declaration, FiscalDocument document, DateOnly today)
    {
        var invalid = new List<string>();
        if (declaration.Kind == SdiDeclarationKind.Sent)
        {
            if (declaration.Date is not { } day || day > today || (document.IssueDate is { } issued && day < issued))
                invalid.Add("date");
            if (string.IsNullOrWhiteSpace(declaration.Reference) || declaration.Reference.Trim().Length > FiscalDocument.SdiTransmissionIdMaxLength)
                invalid.Add("reference");
        }
        else if (declaration.Kind == SdiDeclarationKind.Rejected)
        {
            var code = SdiRejectionReasons.Normalize(declaration.ErrorCode);
            if (code is null || code.Length > FiscalDocument.SdiErrorCodeMaxLength)
                invalid.Add("errorCode");
        }
        else
        {
            invalid.Add("kind");
        }

        return invalid;
    }

    // ---------------------------------------------------------------- the file

    /// <summary>
    /// What the provider is given: the XML as it was issued, the issuer it is transmitted for (its VAT number, else its fiscal code, from the snapshot of the issue) and the name of the
    /// file the specifications of the SDI give (<see cref="SdiFileNames"/>), made of the transmitter the XML itself names.
    /// </summary>
    private static SdiDocumentSubmission BuildSubmission(FiscalDocument document)
    {
        var issuer = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot);
        var fiscalId = FiscalIdOf(issuer);
        if (issuer is null || fiscalId is null || document.IssuerProfileId is not { } profileId || string.IsNullOrWhiteSpace(document.FatturaPaXml))
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotElectronic, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotElectronic));

        var country = string.IsNullOrWhiteSpace(issuer.CountryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : issuer.CountryCode.Trim().ToUpperInvariant();
        var (transmitterCountry, transmitterId) = TransmitterOf(document.FatturaPaXml) ?? (country, fiscalId);
        var name = issuer.LegalName ?? issuer.OrgName ?? fiscalId;
        return new SdiDocumentSubmission(
            document.OrgId,
            document.Id,
            profileId,
            fiscalId,
            name,
            SdiFileNames.Of(transmitterCountry, transmitterId, document.Id),
            document.FatturaPaXml);
    }

    /// <summary>The fiscal id the file is transmitted for: the VAT number without its country prefix, else the fiscal code.</summary>
    private static string? FiscalIdOf(FiscalIssuerSnapshot? issuer)
    {
        if (issuer is null)
            return null;

        var vat = issuer.VatNumber?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(vat))
            return vat.StartsWith("IT", StringComparison.Ordinal) ? vat[2..] : vat;

        return string.IsNullOrWhiteSpace(issuer.FiscalCode) ? null : issuer.FiscalCode.Trim().ToUpperInvariant();
    }

    /// <summary>The transmitter the XML names (<c>IdTrasmittente</c>: country and code); <c>null</c> when it has none or is not XML.</summary>
    internal static (string Country, string Code)? TransmitterOf(string xml)
    {
        try
        {
            var node = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "IdTrasmittente");
            var country = node?.Elements().FirstOrDefault(e => e.Name.LocalName == "IdPaese")?.Value.Trim();
            var code = node?.Elements().FirstOrDefault(e => e.Name.LocalName == "IdCodice")?.Value.Trim();
            return string.IsNullOrEmpty(country) || string.IsNullOrEmpty(code) ? null : (country, code);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- reads

    private FiscalDocumentSdiState StateOf(Guid documentId, StateSource d)
    {
        var electronic = d.Source == FiscalDocumentSource.CasaZen && d.Status == FiscalDocumentStatus.Issued
                         && d.SdiStatus is not (FiscalDocumentSdiStatuses.NotApplicable or FiscalDocumentSdiStatuses.Unknown);
        return new FiscalDocumentSdiState(
            documentId,
            d.SdiStatus,
            d.SdiErrorCode,
            d.SdiErrorDetail,
            d.SdiStatusUpdatedAt,
            ChannelIsOpen,
            CanSubmit: electronic && d.SdiStatus == FiscalDocumentSdiStatuses.ToSend,
            CanDeclare: electronic && d.SdiStatus is FiscalDocumentSdiStatuses.ToSend or FiscalDocumentSdiStatuses.ManualDeclared,
            CanCorrect: electronic && d.SdiStatus == FiscalDocumentSdiStatuses.Rejected && d.Purpose == FiscalDocumentPurpose.GuestStay && !d.Credited);
    }

    /// <summary>
    /// The document has an issued credit note: a rejected one is then not corrected (<see cref="FiscalDocumentCorrectionService"/> refuses it, the accountant decides). The draft of a credit note
    /// does not count: the correction discards it.
    /// </summary>
    private Task<bool> HasIssuedCreditNoteAsync(Guid documentId, CancellationToken ct) =>
        db.FiscalDocuments.AsNoTracking().AnyAsync(c => c.CreditedDocumentId == documentId && c.Status == FiscalDocumentStatus.Issued, ct);

    private sealed record StateSource(
        FiscalDocumentSource Source,
        FiscalDocumentStatus Status,
        FiscalDocumentPurpose Purpose,
        string SdiStatus,
        string? SdiErrorCode,
        string? SdiErrorDetail,
        DateTime? SdiStatusUpdatedAt,
        bool Credited);

    /// <summary>The code a refused submission is stored with: one of the channel's (<see cref="SdiSubmissionErrors"/>), the generic refusal for anything else (at most 20 characters).</summary>
    private static string CodeOfRefusal(SdiSubmissionResult answer) =>
        answer.Error is { } error && SdiSubmissionErrors.All.Contains(error, StringComparer.Ordinal)
            ? error
            : SdiSubmissionErrors.ProviderRefused;

    private static string LockKey(Guid documentId) => documentId.ToString("N");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static NotFoundException NotFound(Guid documentId) =>
        new($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
}
