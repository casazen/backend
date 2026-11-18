using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Receipts of the Swiss compliance files (NF-15, ADR-016): the host records what the cantonal portal gave back after a
/// police notification or an OTR report was submitted. New originals are encrypted in the private bucket;
/// old metadata-only receipts remain distinguishable from downloadable evidence.
/// </summary>
public sealed class SwissComplianceReceiptService(
    AppDbContext db,
    ITicinoNotificationService ticinoNotifications,
    ILogger<SwissComplianceReceiptService> logger,
    IFileStorage storage,
    IDataProtectionProvider dataProtection,
    TimeProvider? timeProvider = null) : ISwissComplianceReceiptService
{
    public const string FileProtectionPurpose = "Casazen.SwissCompliance.Receipt";
    // The sealed payload also contains JSON metadata and base64-encoded receipt bytes.
    private const int MaxProtectedBytes = (int)(SwissComplianceReceipts.MaxFileBytes * 2);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IDataProtector _protector = dataProtection.CreateProtector(FileProtectionPurpose);

    public Task<SwissComplianceReceipt> RecordAsync(
        Guid propertyId,
        SwissComplianceFileType type,
        DateTime reportDate,
        string reference,
        Stream receipt,
        CancellationToken cancellationToken = default) =>
        RecordAsync(propertyId, type, reportDate, reference, receipt, "receipt.bin", "application/octet-stream", cancellationToken);

    public async Task<SwissComplianceReceipt> RecordAsync(Guid propertyId, SwissComplianceFileType type, DateTime reportDate,
        string reference, Stream receipt, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(receipt);

        // The property decides the tenant (tenant query filter): the receipt takes its OrgId, never one the client names (AC2).
        var property = await PropertyAsync(propertyId, cancellationToken);

        if (!await ticinoNotifications.IsPropertyInTicinoAsync(propertyId))
            throw new DomainRuleException("property_not_in_ticino", "PropertyNotInTicino", propertyId);

        var bytes = await ReadBoundedAsync(receipt, (int)SwissComplianceReceipts.MaxFileBytes, cancellationToken);
        if (bytes is null || bytes.Length == 0)
            throw new DomainRuleException("swiss_receipt_file_invalid", "SwissReceiptFileInvalid", SwissComplianceReceipts.MaxFileBytes / (1024 * 1024));
        var fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var stored = new SwissComplianceReceipt
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Type = SwissComplianceReceipts.StoredType(type),
            ReportDate = reportDate,
            Reference = reference.Trim(),
            UploadedFileHash = fileHash,
            UploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        var key = StorageKeys.SwissComplianceReceipt(stored.OrgId, stored.PropertyId, stored.Id);
        var payload = new StoredFile(DownloadName(fileName), DownloadType(contentType), bytes);
        await using var encrypted = new MemoryStream(_protector.CreateProtector(key).Protect(JsonSerializer.SerializeToUtf8Bytes(payload)), writable: false);
        try
        {
            await storage.PutAsync(StorageBucket.Private, key, encrypted, "application/octet-stream", cancellationToken);
            db.SwissComplianceReceipts.Add(stored);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            db.Entry(stored).State = EntityState.Detached;
            try
            {
                await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                logger.LogWarning(cleanupError, "Could not remove the orphan file of Swiss receipt {ReceiptId}", stored.Id);
            }
            throw;
        }

        logger.LogInformation(
            "Swiss compliance receipt {ReceiptId} ({Type}) recorded for property {PropertyId}",
            stored.Id, stored.Type, propertyId);
        return stored;
    }

    public async Task<SwissComplianceReceiptPage> ListAsync(Guid propertyId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            throw new DomainRuleException("validation_error", "ValidationError");
        var property = await PropertyAsync(propertyId, cancellationToken);
        var query = db.SwissComplianceReceipts.AsNoTracking().Where(r => r.OrgId == property.OrgId && r.PropertyId == propertyId);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.ReportDate).ThenByDescending(r => r.UploadedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = new List<SwissComplianceReceiptItem>();
        foreach (var row in rows)
            items.Add(new SwissComplianceReceiptItem(row, await storage.ExistsAsync(StorageBucket.Private,
                StorageKeys.SwissComplianceReceipt(row.OrgId, row.PropertyId, row.Id), cancellationToken)));
        return new SwissComplianceReceiptPage(items, page, pageSize, total);
    }

    public async Task<SwissComplianceReceiptFile> OpenFileAsync(Guid propertyId, Guid receiptId, CancellationToken cancellationToken = default)
    {
        var property = await PropertyAsync(propertyId, cancellationToken);
        var row = await db.SwissComplianceReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == receiptId && r.OrgId == property.OrgId && r.PropertyId == propertyId, cancellationToken)
            ?? throw ReceiptNotFound();
        var key = StorageKeys.SwissComplianceReceipt(row.OrgId, row.PropertyId, row.Id);
        await using var encrypted = await storage.OpenReadAsync(StorageBucket.Private, key, cancellationToken)
            ?? throw ReceiptNotFound();
        StoredFile payload;
        try
        {
            var bytes = await ReadBoundedAsync(encrypted, MaxProtectedBytes, cancellationToken)
                ?? throw new CryptographicException("Receipt payload exceeds its maximum size.");
            payload = JsonSerializer.Deserialize<StoredFile>(_protector.CreateProtector(key).Unprotect(bytes))
                ?? throw new CryptographicException("Missing receipt payload.");
            if (payload.Content is null || payload.Content.Length is 0 or > SwissComplianceReceipts.MaxFileBytes
                || !string.Equals(Convert.ToHexString(SHA256.HashData(payload.Content)), row.UploadedFileHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Receipt content hash mismatch.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException or NotSupportedException)
        {
            logger.LogWarning("The private file of Swiss receipt {ReceiptId} could not be verified", row.Id);
            throw ReceiptNotFound();
        }
        return new SwissComplianceReceiptFile(new MemoryStream(payload.Content, writable: false), DownloadName(payload.FileName), DownloadType(payload.ContentType));
    }

    private async Task<PropertyIdentity> PropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().Where(p => p.Id == propertyId)
            .Select(p => new PropertyIdentity(p.Id, p.OrgId)).FirstOrDefaultAsync(ct)
        ?? throw PropertyNotFound(propertyId);

    private static async Task<byte[]?> ReadBoundedAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string DownloadName(string? name)
    {
        var leaf = name?.Replace('\\', '/').Split('/')[^1].Trim();
        return leaf is { Length: > 0 and <= 200 } && !leaf.Any(char.IsControl) ? leaf : "receipt.bin";
    }

    private static string DownloadType(string? value) => value?.ToLowerInvariant() switch
    {
        "application/pdf" => "application/pdf",
        "image/png" => "image/png",
        "image/jpeg" => "image/jpeg",
        "text/plain" => "text/plain",
        _ => "application/octet-stream",
    };

    private sealed record PropertyIdentity(Guid Id, Guid OrgId);
    private sealed record StoredFile(string FileName, string ContentType, byte[] Content);

    private static NotFoundException ReceiptNotFound() => new("Swiss receipt file not found")
    {
        Code = "swiss_receipt_file_not_found",
        MessageKey = "NotFound",
    };

    private static NotFoundException PropertyNotFound(Guid id) =>
        new($"Property {id} not found")
        {
            Code = "property_not_found",
            MessageKey = "PropertyNotFound"
        };
}
