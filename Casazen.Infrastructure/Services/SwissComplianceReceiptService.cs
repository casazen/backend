using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Receipts of the Swiss compliance files (NF-15, ADR-016): the host records what the cantonal portal gave back after a
/// police notification or an OTR report was submitted. Only the SHA-256 of the file and the reference are kept
/// (<see cref="SwissComplianceReceipt"/>); the file is read once to be hashed and is not stored.
/// </summary>
public sealed class SwissComplianceReceiptService(
    AppDbContext db,
    ITicinoNotificationService ticinoNotifications,
    ILogger<SwissComplianceReceiptService> logger,
    TimeProvider? timeProvider = null) : ISwissComplianceReceiptService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SwissComplianceReceipt> RecordAsync(
        Guid propertyId,
        SwissComplianceFileType type,
        DateTime reportDate,
        string reference,
        Stream receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(receipt);

        // The property decides the tenant (tenant query filter): the receipt takes its OrgId, never one the client names (AC2).
        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.Id, p.OrgId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PropertyNotFound(propertyId);

        if (!await ticinoNotifications.IsPropertyInTicinoAsync(propertyId))
            throw new DomainRuleException("property_not_in_ticino", "PropertyNotInTicino", propertyId);

        var fileHash = Convert.ToHexString(await SHA256.HashDataAsync(receipt, cancellationToken)).ToLowerInvariant();

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
        db.SwissComplianceReceipts.Add(stored);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Swiss compliance receipt {ReceiptId} ({Type}) recorded for property {PropertyId}",
            stored.Id, stored.Type, propertyId);
        return stored;
    }

    private static NotFoundException PropertyNotFound(Guid id) =>
        new($"Property {id} not found")
        {
            Code = "property_not_found",
            MessageKey = "PropertyNotFound"
        };
}
