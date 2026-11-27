using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

// The sources of the document archive (NF-99, E07/F03/US03/T01): one class for each table the archive reads. Each one keeps the reach of the
// caller in ONE place (InReach), used by the list (Query) and by the file (OpenAsync), so a document that is not in the list cannot be opened
// either. Every query names the org of the scope explicitly, on top of the tenant filter of the context, and every property it joins too.
//
// What is NOT a source, on purpose: the identity documents and the selfies of the guests (Guest.DocumentScanUrl, the images of the stay
// guests). They are guest data of the police and identity flows, read only through their own authenticated endpoints, and the archive has no
// way to reach them: a new source is a class here, registered by AddDocumentArchive, and the test of the registration lists them all.
// The proofs of the compliance duties the host uploaded (the Alloggiati receipts and the ISTAT proofs) are sources: they are the host's own
// archive of what proves a communication (E20/F02/US01/T01), read with guest.read.
//
// Time zone: the day of an upload is the day in Europe/Rome. On PostgreSQL the provider turns TimeZoneInfo.ConvertTimeBySystemTimeZoneId into AT TIME ZONE,
// so the server works the day out and the filter by days and the order are made in SQL on the same day the host sees; the InMemory provider of the
// unit tests evaluates the very same expression with .NET.

/// <summary>The documents of the properties (APE, licence, CIN certificate, safety, ...): the private bucket, <c>PropertyDocument.StorageUrl</c> is the key.</summary>
public sealed class PropertyDocumentArchiveSource(AppDbContext db, IFileStorage storage) : IDocumentArchiveSource
{
    public ArchiveDocumentType Type => ArchiveDocumentType.PropertyDocument;

    /// <summary>The property core is read by a landlord of either rental context (<c>SharedPropertyRead</c>).</summary>
    public string ContextKey => $"{OrgRoleCatalog.ShortRent}|{OrgRoleCatalog.LongRent}";

    public string PermissionKey => "property.read";

    public IQueryable<ArchiveRow> Query(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return InReach(scope).Select(d => new ArchiveRow
        {
            Type = ArchiveDocumentType.PropertyDocument,
            Id = d.Id,
            Title = d.FileName,
            DocumentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(d.UploadedAt, RomeCalendar.TimeZoneId)),
            PropertyId = d.PropertyId,
            PropertyName = d.Property.Name,
            BookingId = null,
            BookingCode = null,
            ContentType = string.Empty,
            SizeBytes = null,
        });
    }

    public async Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var document = await InReach(scope)
            .Where(d => d.Id == id)
            .Select(d => new { d.FileName, d.StorageUrl })
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null || !StorageKeys.IsValid(document.StorageUrl))
            return null;

        var content = await storage.OpenReadAsync(StorageBucket.Private, document.StorageUrl, cancellationToken);
        return content is null ? null : new ArchiveFile(content, document.FileName, StorageKeys.ContentTypeFor(document.FileName));
    }

    private IQueryable<PropertyDocument> InReach(HostScope scope)
    {
        var orgId = scope.OrgId;
        return db.PropertyDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Property.OrgId == orgId)
            .InScope(scope, d => d.Property);
    }
}

/// <summary>The files attached to the leases (the contract, the attestation, the registration receipt): the private bucket, <c>LeaseDocument.StorageKey</c>.</summary>
public sealed class LeaseDocumentArchiveSource(AppDbContext db, IFileStorage storage) : IDocumentArchiveSource
{
    private const string PdfContentType = "application/pdf";

    public ArchiveDocumentType Type => ArchiveDocumentType.Contract;

    public string ContextKey => OrgRoleCatalog.LongRent;

    public string PermissionKey => "lease.read";

    public IQueryable<ArchiveRow> Query(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return InReach(scope).Select(d => new ArchiveRow
        {
            Type = ArchiveDocumentType.Contract,
            Id = d.Id,
            Title = d.FileName,
            DocumentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(d.UploadedAt, RomeCalendar.TimeZoneId)),
            PropertyId = d.LeaseContract.PropertyId,
            PropertyName = d.LeaseContract.Property.Name,
            BookingId = null,
            BookingCode = null,
            ContentType = PdfContentType,
            SizeBytes = d.SizeBytes,
        });
    }

    public async Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var document = await InReach(scope)
            .Where(d => d.Id == id)
            .Select(d => new { d.FileName, d.StorageKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null || !StorageKeys.IsValid(document.StorageKey))
            return null;

        var content = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, cancellationToken);
        return content is null ? null : new ArchiveFile(content, document.FileName, PdfContentType);
    }

    private IQueryable<LeaseDocument> InReach(HostScope scope)
    {
        var orgId = scope.OrgId;
        return db.LeaseDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.LeaseContract.OrgId == orgId && d.LeaseContract.Property.OrgId == orgId)
            .InScope(scope, d => d.LeaseContract.Property);
    }
}

/// <summary>
/// The receipts of the tourist tax payments (NF-100): the private bucket, <c>TouristTaxRemittance.ReceiptStorageKey</c>. A payment belongs to the
/// org and to no property, so only a scope that reaches the whole org sees it (as the tourist tax summary and its receipt endpoint do). A payment
/// recorded without a receipt has no document and is not a row.
/// </summary>
public sealed class TouristTaxRemittanceArchiveSource(AppDbContext db, IFileStorage storage) : IDocumentArchiveSource
{
    public ArchiveDocumentType Type => ArchiveDocumentType.TouristTax;

    public string ContextKey => OrgRoleCatalog.ShortRent;

    public string PermissionKey => "payment.read";

    public IQueryable<ArchiveRow> Query(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return InReach(scope).Select(r => new ArchiveRow
        {
            Type = ArchiveDocumentType.TouristTax,
            Id = r.Id,
            Title = r.Comune,
            DocumentDate = r.PaidOn,
            PropertyId = null,
            PropertyName = null,
            BookingId = null,
            BookingCode = null,
            ContentType = r.ReceiptContentType ?? string.Empty,
            SizeBytes = r.ReceiptSizeBytes,
        });
    }

    public async Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var receipt = await InReach(scope)
            .Where(r => r.Id == id)
            .Select(r => new { r.ReceiptStorageKey, r.ReceiptFileName, r.ReceiptContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (receipt?.ReceiptStorageKey is not { } key || !StorageKeys.IsValid(key))
            return null;

        var content = await storage.OpenReadAsync(StorageBucket.Private, key, cancellationToken);
        return content is null
            ? null
            : new ArchiveFile(content, receipt.ReceiptFileName ?? Path.GetFileName(key), receipt.ReceiptContentType ?? StorageKeys.ContentTypeFor(key));
    }

    private IQueryable<TouristTaxRemittance> InReach(HostScope scope)
    {
        var orgId = scope.OrgId;
        var receipts = db.TouristTaxRemittances.AsNoTracking().Where(r => r.OrgId == orgId && r.ReceiptStorageKey != null);

        // No property to narrow by: a scope limited to some properties reaches none of these rows, decided in the same statement.
        return scope.IsOrgWide ? receipts : receipts.Where(_ => false);
    }
}

/// <summary>
/// The proofs of the compliance duties the host uploaded, «Prove di adempimento» (E20/F02/US01/T01, NF-47, CHK-08): the private bucket,
/// <see cref="ManualComplianceProof.FileKey"/>. One class for each kind of the archive that has such a file (<see cref="AlloggiatiReceiptArchiveSource"/>,
/// <see cref="IstatProofArchiveSource"/>), sharing what a proof is: the reach, the file and the permission. The receipts of a tourist tax declaration or payment
/// are records of money with their own permission and are not here.
/// </summary>
/// <remarks>
/// <para><b>Who may read it.</b> A proof carries data about guests (the receipt of the communication lists them), so the sources ask <c>guest.read</c>, which the
/// Accountant does not hold (<see cref="ArchiveDocumentTypes.IsComplianceProof"/>: a download is written in the log).</para>
/// <para><b>The reach.</b> The one of the compliance archive (<c>InScope</c> of a proof): the proofs whose every flat the caller reaches, the flat they were filed
/// under and every flat a receipt covers, so a collaborator «Solo alcuni» never opens the file of a receipt that is also about the guests of a flat it does not
/// reach, nor of one that also covers a flat deleted since (the lines of the receipt are counted against the flats still there, see <see cref="InReach"/>). Like every other source of the archive it leaves out what belongs to a flat deleted since (the soft-delete filter of the model applies, the tenant filter
/// too): <b>no source of this class may lift a filter of the model</b>, because EF applies <c>IgnoreQueryFilters</c> to the whole statement and the list, the
/// count and the export concatenate all the sources in ONE statement, so a filter lifted here would be lifted for the documents of the properties and the leases too.
/// The history of the proofs of a flat deleted since stays in the dedicated archive (<c>api/compliance/archive</c>), which reads them with its own statement. The
/// row names the flat the receipt was filed under: a filter by property of the archive finds a receipt by that flat, the dedicated archive by any flat it covers.</para>
/// </remarks>
public abstract class ComplianceProofArchiveSource(AppDbContext db, IFileStorage storage, ManualProofKind kind) : IDocumentArchiveSource
{
    public abstract ArchiveDocumentType Type { get; }

    public string ContextKey => OrgRoleCatalog.ShortRent;

    public string PermissionKey => "guest.read";

    public abstract IQueryable<ArchiveRow> Query(HostScope scope);

    public async Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var proof = await InReach(scope)
            .Where(p => p.Id == id)
            .Select(p => new { p.Kind, p.Day, p.FileKey, p.ContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (proof is null || !StorageKeys.IsValid(proof.FileKey))
            return null;

        var content = await storage.OpenReadAsync(StorageBucket.Private, proof.FileKey, cancellationToken);
        return content is null
            ? null
            : new ArchiveFile(content, ComplianceArchiveRules.DownloadName(proof.Kind, proof.Day, proof.ContentType), proof.ContentType);
    }

    /// <summary>
    /// The proofs of this kind of the caller's org in its reach, those of a flat deleted since left out like the documents of every other source. No filter of the
    /// model is lifted here (see the remarks of the class): the statement of the list is shared with the other sources.
    /// </summary>
    /// <remarks>
    /// A scope limited to some flats reads a receipt of several flats only when <b>every</b> flat it covers is in its reach, the ones deleted since included, and when in
    /// doubt it does not read it (the answer of the dedicated archive). <c>InScope</c> judges the flats that are still there; the soft-delete filter, which applies to the
    /// whole statement of the list, takes a deleted flat out of the join it checks, so such a flat would not fail the check. The lines of the receipt are therefore counted
    /// against the lines whose flat still exists (and is the org's), and a receipt that covers a flat gone stays out of a limited scope; the whole org reads it. It is one
    /// statement all the same, and no filter is lifted.
    /// </remarks>
    protected IQueryable<ManualComplianceProof> InReach(HostScope scope)
    {
        var orgId = scope.OrgId;
        var proofs = db.ManualComplianceProofs.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Property.OrgId == orgId && p.Kind == kind)
            .InScope(scope);

        return scope.IsOrgWide
            ? proofs
            : proofs.Where(p => p.CoveredProperties.Count() == p.CoveredProperties.Count(line => line.Property.OrgId == orgId));
    }
}

/// <summary>The receipts of the Alloggiati communication of a day the host uploaded (<see cref="ManualProofKind.AlloggiatiReceipt"/>).</summary>
public sealed class AlloggiatiReceiptArchiveSource(AppDbContext db, IFileStorage storage)
    : ComplianceProofArchiveSource(db, storage, ManualProofKind.AlloggiatiReceipt)
{
    public override ArchiveDocumentType Type => ArchiveDocumentType.AlloggiatiReceipt;

    public override IQueryable<ArchiveRow> Query(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The title is the word the file name is made of (ComplianceArchiveRules.SlugOf); the day is the one the proof is about, the arrival day of the stays.
        return InReach(scope).Select(p => new ArchiveRow
        {
            Type = ArchiveDocumentType.AlloggiatiReceipt,
            Id = p.Id,
            Title = "alloggiati",
            DocumentDate = p.Day,
            PropertyId = p.PropertyId,
            PropertyName = p.Property.Name,
            BookingId = null,
            BookingCode = null,
            ContentType = p.ContentType,
            SizeBytes = null,
        });
    }
}

/// <summary>The proofs of an ISTAT communication of a period the host uploaded (<see cref="ManualProofKind.IstatProof"/>), filed under the first day of the month.</summary>
public sealed class IstatProofArchiveSource(AppDbContext db, IFileStorage storage)
    : ComplianceProofArchiveSource(db, storage, ManualProofKind.IstatProof)
{
    public override ArchiveDocumentType Type => ArchiveDocumentType.IstatProof;

    public override IQueryable<ArchiveRow> Query(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return InReach(scope).Select(p => new ArchiveRow
        {
            Type = ArchiveDocumentType.IstatProof,
            Id = p.Id,
            Title = "istat",
            DocumentDate = p.Day,
            PropertyId = p.PropertyId,
            PropertyName = p.Property.Name,
            BookingId = null,
            BookingCode = null,
            ContentType = p.ContentType,
            SizeBytes = null,
        });
    }
}
