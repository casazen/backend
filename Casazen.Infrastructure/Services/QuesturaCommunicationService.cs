using System.Data;
using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Questura communication for an extra-EU tenant (LT-07, A7-08). See <see cref="IQuesturaCommunicationService"/> and
/// <see cref="QuesturaCommunicationDeadline"/>; runbook docs/runbooks/rli.md, "Questura communication (LT-07)".
/// </summary>
/// <remarks>
/// The declaration is one transaction under a row lock on the lease (<c>SELECT … FOR UPDATE</c>): two concurrent
/// declarations record one, the other gets 409. The receipt is stored first in the private bucket (FD-07) and removed
/// again when the database update fails.
/// </remarks>
public class QuesturaCommunicationService(
    AppDbContext db,
    IFileStorage storage,
    ILogger<QuesturaCommunicationService> logger,
    TimeProvider? timeProvider = null) : IQuesturaCommunicationService
{
    private const string PdfContentType = "application/pdf";
    private const string ClearedPayload = "cleared";

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task DeclareDeliveryDateAsync(
        Guid leaseId, string userId, DateTime? deliveryDate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        DateTime? stored = deliveryDate is { } date ? QuesturaCommunicationDeadline.ToStoredDate(date) : null;
        if (stored is { } delivery && RomeCalendar.DateInRome(delivery) > RomeCalendar.DateInRome(lease.EndDate))
        {
            throw new DomainRuleException(
                QuesturaCommunicationErrorCodes.DeliveryDateAfterEnd, "QuesturaDeliveryDateAfterEnd");
        }

        if (lease.PropertyDeliveryDate == stored)
            return;

        var now = UtcNow();
        lease.PropertyDeliveryDate = stored;
        lease.UpdatedAt = now;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.PropertyDeliveryDateDeclared,
            OccurredAt = now,
            Payload = stored is { } value ? DatePayload(value) : ClearedPayload,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Property delivery date declared. LeaseId={LeaseId} Cleared={Cleared}", lease.Id, stored is null);
    }

    public async Task MarkDoneAsync(
        Guid leaseId,
        string userId,
        QuesturaCommunicationDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(declaration);

        var communicationDate = QuesturaCommunicationDeadline.ToStoredDate(declaration.CommunicationDate);
        if (RomeCalendar.DateInRome(communicationDate) > _clock.TodayInRomeAsDateOnly())
        {
            throw new DomainRuleException(
                QuesturaCommunicationErrorCodes.DateInFuture, "QuesturaCommunicationDateInFuture");
        }

        MemoryStream? receipt = null;
        if (declaration.Receipt is not null)
        {
            receipt = await ReadPdfAsync(declaration.Receipt, declaration.ReceiptLength, cancellationToken)
                ?? throw new DomainRuleException(
                    QuesturaCommunicationErrorCodes.ReceiptInvalid,
                    "QuesturaReceiptInvalid",
                    QuesturaCommunicationLimits.MaxReceiptBytes / (1024 * 1024));
        }

        await using (receipt)
        {
            var lease = await LoadLeaseAsync(leaseId, cancellationToken);
            EnsureCanMarkDone(lease);

            string? receiptKey = null;
            if (receipt is not null)
            {
                receiptKey = StorageKeys.LeaseQuesturaReceipt(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
                await storage.PutAsync(StorageBucket.Private, receiptKey, receipt, PdfContentType, cancellationToken);
            }

            try
            {
                await using var transaction = await BeginTransactionAsync(cancellationToken);
                await LockLeaseAsync(lease.Id, cancellationToken);
                await ReloadAsync(lease, cancellationToken);
                EnsureCanMarkDone(lease);

                var now = UtcNow();
                lease.QuesturaCommunicationDate = communicationDate;
                lease.QuesturaCommunicationReceiptPath = receiptKey;
                lease.QuesturaCommunicationDeclaredByUserId = userId;
                lease.UpdatedAt = now;
                db.LeaseEvents.Add(new LeaseEvent
                {
                    LeaseContractId = lease.Id,
                    EventType = LeaseEventType.QuesturaCommunicationMarkedDone,
                    OccurredAt = now,
                    Payload = DatePayload(communicationDate),
                });

                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                if (receiptKey is not null)
                    await TryDeleteReceiptAsync(receiptKey);
                throw;
            }

            logger.LogInformation(
                "Questura communication declared. LeaseId={LeaseId} WithReceipt={WithReceipt}", lease.Id, receiptKey is not null);
        }
    }

    public async Task<QuesturaReceiptFile> OpenReceiptAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (lease.QuesturaCommunicationDate is null || !StorageKeys.IsValid(lease.QuesturaCommunicationReceiptPath))
        {
            throw new NotFoundException($"No Questura receipt for lease {leaseId}.")
            {
                Code = QuesturaCommunicationErrorCodes.ReceiptNotAvailable,
                MessageKey = "QuesturaReceiptNotAvailable",
            };
        }

        var content = await storage.OpenReadAsync(StorageBucket.Private, lease.QuesturaCommunicationReceiptPath!, cancellationToken);
        if (content is null)
        {
            logger.LogWarning("Stored Questura receipt missing. LeaseId={LeaseId}", leaseId);
            throw new NotFoundException($"Questura receipt file of lease {leaseId} is missing.")
            {
                Code = "document_file_missing",
                MessageKey = "DocumentFileMissing",
            };
        }

        return new QuesturaReceiptFile(content, $"ricevuta-questura-{lease.Id}.pdf");
    }

    /// <summary>Only for a lease with an extra-EU tenant (not rejected), and only once.</summary>
    private static void EnsureCanMarkDone(LeaseContract lease)
    {
        if (!QuesturaCommunicationDeadline.IsRequired(lease))
            throw new DomainRuleException(QuesturaCommunicationErrorCodes.NotRequired, "QuesturaNotRequired");

        if (lease.QuesturaCommunicationDate is not null)
            throw new DomainConflictException(QuesturaCommunicationErrorCodes.AlreadyMarkedDone, "QuesturaAlreadyMarkedDone");
    }

    private async Task<LeaseContract> LoadLeaseAsync(Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseContracts
            .Include(l => l.Parties)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
        ?? throw new NotFoundException($"Lease {leaseId} not found.")
        {
            Code = "lease_not_found",
            MessageKey = "LeaseNotFound",
        };

    private static string DatePayload(DateTime date) =>
        RomeCalendar.DateInRome(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The whole content when it is a PDF (<c>%PDF-</c> signature) of at most <see cref="QuesturaCommunicationLimits.MaxReceiptBytes"/>,
    /// positioned at the start; null otherwise. The declared length is not trusted: the content is counted while read.
    /// </summary>
    private static async Task<MemoryStream?> ReadPdfAsync(Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0 or > QuesturaCommunicationLimits.MaxReceiptBytes)
            return null;

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > QuesturaCommunicationLimits.MaxReceiptBytes)
            {
                await buffer.DisposeAsync();
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length < PdfSignature.Length || !buffer.GetBuffer().AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature))
        {
            await buffer.DisposeAsync();
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }

    private async Task TryDeleteReceiptAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete an orphan Questura receipt from the private bucket");
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>Row lock on the lease until the end of the transaction: concurrent declarations run one after the other.</summary>
    private async Task LockLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    private async Task ReloadAsync(LeaseContract lease, CancellationToken cancellationToken)
    {
        var entry = db.Entry(lease);
        if (entry.State is not (EntityState.Added or EntityState.Detached))
            await entry.ReloadAsync(cancellationToken);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
