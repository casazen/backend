using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// RLI registration (LT-01: A7-01, A7-21; decision D15). See <see cref="IRliRegistrationService"/> and docs/runbooks/rli.md.
/// </summary>
/// <remarks>
/// <para>Atomicity (A7-21): each state change is one transaction under a row lock on the lease
/// (<c>SELECT … FOR UPDATE</c>), so the delega, the registration row, the lease status and the timeline event are
/// written together or not at all, and a concurrent request sees the new state. The provider is called between two
/// transactions, never inside one:</para>
/// <list type="number">
/// <item>reserve: delega + registration Pending + lease RegistrationPending + <c>RegistrationAuthorized</c>;</item>
/// <item>provider call;</item>
/// <item>success: registration SentToProvider + lease SentToProvider + <c>RegistrationSubmitted</c>; failure:
/// registration Failed (<see cref="LeaseRegistration.FailureCode"/>) + lease back to Signed + <c>RegistrationFailed</c>,
/// so a retry or the manual path is possible.</item>
/// </list>
/// A reservation left Pending by a crash between 1 and 3 is failed by the polling job after
/// <see cref="StaleSubmissionAfter"/> with <see cref="RliRegistrationFailureCodes.OutcomeUnknown"/>.
/// </remarks>
public class RliRegistrationService(
    AppDbContext db,
    ILeaseRegistrationProvider provider,
    IFeatureFlags featureFlags,
    IFileStorage storage,
    IApeComplianceService apeCompliance,
    IOptions<RliOptions> rliOptions,
    ILogger<RliRegistrationService> logger,
    TimeProvider? timeProvider = null) : IRliRegistrationService
{
    /// <summary>A provider reservation still Pending after this long has lost its outcome.</summary>
    public static readonly TimeSpan StaleSubmissionAfter = TimeSpan.FromMinutes(15);

    private const string PdfContentType = "application/pdf";
    private const string ManualEventPayload = "manual";
    private const string ProviderEventPayload = "provider";

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public bool IsProviderFilingAvailable => RliProviderFiling.IsAvailable(featureFlags, provider);

    public async Task<LeaseRegistration> SubmitToProviderAsync(
        Guid leaseId,
        string ownerId,
        RegistrationAuthorizationRequest delega,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(delega);

        // Flag off or no configured provider: nothing is recorded and nothing reaches a provider.
        if (!IsProviderFilingAvailable)
            throw new DomainConflictException(RliRegistrationErrorCodes.ProviderUnavailable, "RliProviderUnavailable");

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);

        // The delega is the owner's: only the property owner can authorize a filing in their name.
        if (lease.Property is null || lease.Property.OwnerId != ownerId)
            throw new UnauthorizedAccessException("Only the property owner can give the RLI delega.");

        EnsureCanRegister(lease, lease.Registration);

        if (string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath))
            throw new DomainRuleException(RliRegistrationErrorCodes.SignedPdfMissing, "RliSignedPdfMissing");

        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);

        if (!delega.AttestationAccepted
            || !string.Equals(delega.TosVersion, rliOptions.Value.TosVersion, StringComparison.Ordinal))
        {
            throw new DomainRuleException(RliRegistrationErrorCodes.DelegaRequired, "RliDelegaRequired");
        }

        await apeCompliance.EnsurePropertyHasValidApeAsync(lease.PropertyId);

        var registration = await ReserveProviderSubmissionAsync(lease, ownerId, delega.TosVersion, cancellationToken);

        // From here on the outcome is always recorded: a client that disconnects must not leave the reservation behind.
        string externalId;
        try
        {
            externalId = await provider.SubmitAsync(lease, CancellationToken.None);
        }
        catch (LeaseRegistrationProviderException ex)
        {
            await RecordFailureAfterProviderErrorAsync(registration, lease.Id, ex.FailureCode, ex);
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAfterProviderErrorAsync(registration, lease.Id, RliRegistrationFailureCodes.ProviderError, ex);
            throw new LeaseRegistrationProviderException(
                RliRegistrationFailureCodes.ProviderError, "The RLI provider submission failed.", ex);
        }

        await CompleteProviderSubmissionAsync(registration, lease.Id, externalId, CancellationToken.None);
        logger.LogInformation("RLI submitted to the provider. LeaseId={LeaseId} RegistrationId={RegistrationId}",
            lease.Id, registration.Id);
        return registration;
    }

    public async Task<LeaseRegistration> DeclareManualRegistrationAsync(
        Guid leaseId,
        string userId,
        ManualRegistrationDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(declaration);

        var code = NormalizeRegistrationCode(declaration.RegistrationCode);
        var registrationDate = declaration.RegistrationDate.Date;
        if (registrationDate > _clock.TodayInRome())
            throw new DomainRuleException(RliRegistrationErrorCodes.RegistrationDateInFuture, "RliRegistrationDateInFuture");

        await using var receipt = await ReadPdfAsync(declaration.Receipt, declaration.ReceiptLength, cancellationToken)
            ?? throw new DomainRuleException(RliRegistrationErrorCodes.ReceiptInvalid, "RliReceiptInvalid", RliRegistrationLimits.MaxReceiptBytes / (1024 * 1024));

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureCanRegister(lease, lease.Registration);

        // Receipt first, in the private bucket: a failed upload changes nothing; a failed database update removes it.
        var receiptKey = StorageKeys.LeaseRegistrationReceipt(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        await storage.PutAsync(StorageBucket.Private, receiptKey, receipt, PdfContentType, cancellationToken);

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            await LockLeaseAsync(lease.Id, cancellationToken);
            await ReloadAsync(lease, cancellationToken);
            var registration = await ReloadRegistrationAsync(lease.Id, cancellationToken);
            EnsureCanRegister(lease, registration);

            var now = UtcNow();
            if (registration is null)
            {
                registration = new LeaseRegistration { LeaseContractId = lease.Id };
                db.LeaseRegistrations.Add(registration);
            }

            registration.Status = RegistrationStatus.Registered;
            registration.Channel = RegistrationChannel.Manual;
            registration.RegistrationCode = code;
            registration.RegistrationDate = registrationDate;
            registration.ReceiptStoragePath = receiptKey;
            registration.FailureCode = null;
            registration.ConfirmedAt = now;
            registration.DeclaredByUserId = userId;

            lease.Status = LeaseStatus.Registered;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.RegistrationConfirmed,
                OccurredAt = now,
                Payload = ManualEventPayload,
            });

            await SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("RLI registration declared manually. LeaseId={LeaseId} RegistrationId={RegistrationId}",
                lease.Id, registration.Id);
            return registration;
        }
        catch
        {
            await TryDeleteReceiptAsync(receiptKey);
            throw;
        }
    }

    public async Task<RegistrationReceiptFile> OpenReceiptAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (lease.Registration is not { Status: RegistrationStatus.Registered, ReceiptStoragePath: { } key }
            || string.IsNullOrWhiteSpace(key))
        {
            throw new NotFoundException($"No RLI receipt for lease {leaseId}.")
            {
                Code = RliRegistrationErrorCodes.ReceiptNotAvailable,
                MessageKey = "RliReceiptNotAvailable",
            };
        }

        var content = await storage.OpenReadAsync(StorageBucket.Private, key, cancellationToken);
        if (content is null)
        {
            logger.LogWarning("Stored RLI receipt missing. LeaseId={LeaseId}", leaseId);
            throw new NotFoundException($"RLI receipt file of lease {leaseId} is missing.")
            {
                Code = "document_file_missing",
                MessageKey = "DocumentFileMissing",
            };
        }

        return new RegistrationReceiptFile(content, $"ricevuta-rli-{lease.Id}.pdf");
    }

    public async Task<ProviderSyncResult> SyncProviderRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsProviderFilingAvailable)
        {
            logger.LogInformation("RLI provider sync skipped: the provider path is not available");
            return new ProviderSyncResult(0, 0, 0, 0);
        }

        int registered = 0, failed = 0, inProgress = 0, errors = 0;

        var staleBefore = UtcNow() - StaleSubmissionAfter;
        var stale = await db.LeaseRegistrations
            .AsNoTracking()
            .Where(r => r.Channel == RegistrationChannel.Provider
                && r.Status == RegistrationStatus.Pending
                && (r.RequestedAt == null || r.RequestedAt < staleBefore))
            .Select(r => new { r.Id, r.LeaseContractId })
            .ToListAsync(cancellationToken);
        foreach (var reservation in stale)
        {
            try
            {
                if (await RecordProviderFailureAsync(
                        reservation.Id, reservation.LeaseContractId, RliRegistrationFailureCodes.OutcomeUnknown, cancellationToken))
                {
                    failed++;
                    logger.LogWarning("RLI provider reservation without outcome failed. LeaseId={LeaseId}", reservation.LeaseContractId);
                }
            }
            catch (Exception ex)
            {
                errors++;
                logger.LogError(ex, "Could not fail the stale RLI reservation of LeaseId={LeaseId}", reservation.LeaseContractId);
            }
        }

        var submitted = await db.LeaseRegistrations
            .AsNoTracking()
            .Where(r => r.Channel == RegistrationChannel.Provider
                && r.Status == RegistrationStatus.SentToProvider
                && r.ExternalRegistrationId != null)
            .Select(r => new { r.Id, r.LeaseContractId, r.ExternalRegistrationId, r.LeaseContract.OrgId })
            .ToListAsync(cancellationToken);
        foreach (var request in submitted)
        {
            try
            {
                var status = await provider.GetStatusAsync(request.ExternalRegistrationId!, cancellationToken);
                switch (status.State)
                {
                    case ProviderRegistrationState.Registered:
                        if (await ConfirmProviderRegistrationAsync(
                                request.Id, request.LeaseContractId, request.OrgId, request.ExternalRegistrationId!,
                                status.RegistrationCode, cancellationToken))
                        {
                            registered++;
                        }
                        break;
                    case ProviderRegistrationState.Failed:
                        if (await RecordProviderFailureAsync(
                                request.Id, request.LeaseContractId,
                                string.IsNullOrWhiteSpace(status.FailureCode)
                                    ? RliRegistrationFailureCodes.ProviderRejected
                                    : status.FailureCode,
                                cancellationToken))
                        {
                            failed++;
                        }
                        break;
                    default:
                        inProgress++;
                        break;
                }
            }
            catch (Exception ex)
            {
                // The registration stays SentToProvider and is polled again at the next run.
                errors++;
                logger.LogError(ex, "RLI provider sync failed. LeaseId={LeaseId}", request.LeaseContractId);
            }
        }

        return new ProviderSyncResult(registered, failed, inProgress, errors);
    }

    /// <summary>A registration can start (provider or manual) only on a Signed lease with nothing in progress or done.</summary>
    private static void EnsureCanRegister(LeaseContract lease, LeaseRegistration? registration)
    {
        if (lease.Status == LeaseStatus.Registered || registration?.Status == RegistrationStatus.Registered)
            throw new DomainConflictException(RliRegistrationErrorCodes.AlreadyRegistered, "RliAlreadyRegistered");

        if (lease.Status is LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider
            || registration?.Status is RegistrationStatus.Pending or RegistrationStatus.SentToProvider)
        {
            throw new DomainConflictException(RliRegistrationErrorCodes.InProgress, "RliRegistrationInProgress");
        }

        if (lease.Status != LeaseStatus.Signed)
            throw new DomainRuleException(RliRegistrationErrorCodes.LeaseNotSigned, "RliLeaseNotSigned");
    }

    private async Task<LeaseRegistration> ReserveProviderSubmissionAsync(
        LeaseContract lease, string ownerId, string tosVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockLeaseAsync(lease.Id, cancellationToken);
        await ReloadAsync(lease, cancellationToken);
        var registration = await ReloadRegistrationAsync(lease.Id, cancellationToken);
        EnsureCanRegister(lease, registration);

        var now = UtcNow();
        if (registration is null)
        {
            registration = new LeaseRegistration { LeaseContractId = lease.Id };
            db.LeaseRegistrations.Add(registration);
        }

        // A Failed row is reused: one registration per lease (unique index), its previous attempt stays in the timeline.
        registration.Status = RegistrationStatus.Pending;
        registration.Channel = RegistrationChannel.Provider;
        registration.ExternalRegistrationId = null;
        registration.RegistrationCode = null;
        registration.RegistrationDate = null;
        registration.ReceiptStoragePath = null;
        registration.FailureCode = null;
        registration.RequestedAt = now;
        registration.SubmittedAt = null;
        registration.ConfirmedAt = null;
        registration.DeclaredByUserId = null;

        lease.Status = LeaseStatus.RegistrationPending;
        lease.UpdatedAt = now;

        db.LeaseRegistrationAuthorizations.Add(new LeaseRegistrationAuthorization
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            AuthorizerUserId = ownerId,
            AuthorizedAt = now,
            TosVersion = tosVersion,
            AttestationAccepted = true,
            Scope = "rli-filing",
        });
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.RegistrationAuthorized,
            OccurredAt = now,
            Payload = tosVersion,
        });

        await SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return registration;
    }

    private async Task CompleteProviderSubmissionAsync(
        LeaseRegistration registration, Guid leaseId, string externalId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockLeaseAsync(leaseId, cancellationToken);
        var lease = await ReloadLeaseAsync(leaseId, cancellationToken);
        await ReloadAsync(registration, cancellationToken);

        var now = UtcNow();
        registration.Status = RegistrationStatus.SentToProvider;
        registration.ExternalRegistrationId = externalId;
        registration.SubmittedAt = now;
        if (lease is not null)
        {
            lease.Status = LeaseStatus.SentToProvider;
            lease.UpdatedAt = now;
        }

        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.RegistrationSubmitted,
            OccurredAt = now,
        });

        await SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordFailureAfterProviderErrorAsync(
        LeaseRegistration registration, Guid leaseId, string failureCode, Exception providerError)
    {
        logger.LogWarning(providerError, "RLI provider submission failed. LeaseId={LeaseId} FailureCode={FailureCode}",
            leaseId, failureCode);
        await RecordProviderFailureAsync(registration.Id, leaseId, failureCode, CancellationToken.None);
    }

    /// <summary>Registration Failed, lease back to Signed, event; false when the registration is no longer in progress.</summary>
    private async Task<bool> RecordProviderFailureAsync(
        Guid registrationId, Guid leaseId, string failureCode, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockLeaseAsync(leaseId, cancellationToken);
        var lease = await ReloadLeaseAsync(leaseId, cancellationToken);
        var registration = await ReloadRegistrationAsync(leaseId, cancellationToken);
        if (registration is null
            || registration.Id != registrationId
            || registration.Status is not (RegistrationStatus.Pending or RegistrationStatus.SentToProvider))
        {
            return false;
        }

        var now = UtcNow();
        registration.Status = RegistrationStatus.Failed;
        registration.FailureCode = failureCode;
        if (lease is { Status: LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider })
        {
            lease.Status = LeaseStatus.Signed;
            lease.UpdatedAt = now;
        }

        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.RegistrationFailed,
            OccurredAt = now,
            Payload = failureCode,
        });

        await SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Copies the provider's receipt into the private bucket (its download URL expires), then marks registration and
    /// lease Registered. Without a valid PDF receipt nothing changes and the request is polled again.
    /// </summary>
    private async Task<bool> ConfirmProviderRegistrationAsync(
        Guid registrationId,
        Guid leaseId,
        Guid orgId,
        string externalId,
        string? registrationCode,
        CancellationToken cancellationToken)
    {
        MemoryStream? receipt;
        await using (var download = await provider.DownloadReceiptAsync(externalId, cancellationToken))
        {
            receipt = await ReadPdfAsync(download, declaredLength: null, cancellationToken);
        }

        if (receipt is null)
        {
            logger.LogWarning("RLI provider returned no valid PDF receipt. LeaseId={LeaseId}", leaseId);
            return false;
        }

        var receiptKey = StorageKeys.LeaseRegistrationReceipt(orgId, leaseId, StorageKeys.NewFileName(".pdf"));
        await using (receipt)
        {
            await storage.PutAsync(StorageBucket.Private, receiptKey, receipt, PdfContentType, cancellationToken);
        }

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            await LockLeaseAsync(leaseId, cancellationToken);
            var lease = await ReloadLeaseAsync(leaseId, cancellationToken);
            var registration = await ReloadRegistrationAsync(leaseId, cancellationToken);
            if (registration is null || registration.Id != registrationId || registration.Status != RegistrationStatus.SentToProvider)
            {
                await TryDeleteReceiptAsync(receiptKey);
                return false;
            }

            var now = UtcNow();
            registration.Status = RegistrationStatus.Registered;
            registration.RegistrationCode = string.IsNullOrWhiteSpace(registrationCode) ? null : registrationCode.Trim();
            registration.ReceiptStoragePath = receiptKey;
            registration.FailureCode = null;
            registration.ConfirmedAt = now;
            if (lease is not null)
            {
                lease.Status = LeaseStatus.Registered;
                lease.UpdatedAt = now;
            }

            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = leaseId,
                EventType = LeaseEventType.RegistrationConfirmed,
                OccurredAt = now,
                Payload = ProviderEventPayload,
            });

            await SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("RLI registration confirmed by the provider. LeaseId={LeaseId}", leaseId);
            return true;
        }
        catch
        {
            await TryDeleteReceiptAsync(receiptKey);
            throw;
        }
    }

    private async Task<LeaseContract> LoadLeaseAsync(Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Registration)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
        ?? throw new NotFoundException($"Lease {leaseId} not found.")
        {
            Code = "lease_not_found",
            MessageKey = "LeaseNotFound",
        };

    private static string NormalizeRegistrationCode(string? registrationCode)
    {
        var code = registrationCode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > RliRegistrationLimits.MaxRegistrationCodeLength || code.Any(char.IsControl))
            throw new DomainRuleException(RliRegistrationErrorCodes.RegistrationCodeInvalid, "RliRegistrationCodeInvalid", RliRegistrationLimits.MaxRegistrationCodeLength);
        return code;
    }

    /// <summary>
    /// The whole content when it is a PDF (<c>%PDF-</c> signature) of at most <see cref="RliRegistrationLimits.MaxReceiptBytes"/>, positioned
    /// at the start; null otherwise. The declared length is not trusted: the content is counted while it is read.
    /// </summary>
    private static async Task<MemoryStream?> ReadPdfAsync(Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0 or > RliRegistrationLimits.MaxReceiptBytes)
            return null;

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > RliRegistrationLimits.MaxReceiptBytes)
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
            logger.LogWarning(ex, "Could not delete an orphan RLI receipt from the private bucket");
        }
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request created the lease's registration first (one per lease).
            throw new DomainConflictException(RliRegistrationErrorCodes.InProgress, "RliRegistrationInProgress");
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>Row lock on the lease until the end of the transaction: concurrent registrations of a lease run one after the other.</summary>
    private async Task LockLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>The lease as stored now (a tracked instance is refreshed: its values may predate the lock).</summary>
    private async Task<LeaseContract?> ReloadLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var lease = await db.LeaseContracts.SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
        if (lease is not null)
            await ReloadAsync(lease, cancellationToken);
        return lease;
    }

    private async Task<LeaseRegistration?> ReloadRegistrationAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var registration = await db.LeaseRegistrations.SingleOrDefaultAsync(r => r.LeaseContractId == leaseId, cancellationToken);
        if (registration is not null)
            await ReloadAsync(registration, cancellationToken);
        return registration;
    }

    private async Task ReloadAsync(object entity, CancellationToken cancellationToken)
    {
        var entry = db.Entry(entity);
        if (entry.State is not (EntityState.Added or EntityState.Detached))
            await entry.ReloadAsync(cancellationToken);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
