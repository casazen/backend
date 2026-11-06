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
