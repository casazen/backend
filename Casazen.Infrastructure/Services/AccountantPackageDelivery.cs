using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAccountantPackageDeliveryService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The order of a send.</b> What cannot be sent from this environment is known first (the secret of the signed links, the public address of this API, the e-mail provider): nothing is made for it. Then the package
/// is made outside any lock (<see cref="IAccountantPackageService"/>, in memory: it takes a while and nobody waits for it), and everything else happens under the lock of the org
/// (<see cref="PostgresAdvisoryLocks.Scope.AccountantPackage"/>, held until the commit): the row is read again (its folders must still be the ones the package was made with: a change in between stops the send, made again at the next run), the package is put in the private storage, the e-mail is handed to the queue and the row says which
/// month was sent. A monthly send that finds its month already sent stops there, so the month is sent once however many runs reach it. The lock is held while the object is stored (a second or two) so that the nightly
/// deletion can never remove a package that is being replaced.</item>
/// <item><b>At least once, never lost.</b> The month is recorded in the same step as the queue: a crash between the queue and the commit sends the e-mail again at the next run rather than losing the month (the same
/// choice as the monthly compliance summary). A queue that refuses is a failure of the send, recorded with its code, never a success.</item>
/// <item><b>One package at a time for an org.</b> The package is kept under a key made of the org and the month; a newer package replaces the one of the month before, which is deleted first, while the row still names it:
/// an object that cannot be deleted is a failure of the send (<c>storage_failed</c>, recorded and tried again), never swallowed. The nightly run deletes what has outlived its link. The row says which package is in the
/// storage, so nothing stays in the bucket that no row names.</item>
/// <item><b>Failures are recorded, never swallowed.</b> A monthly send that fails writes <c>failed</c> and its code in the settings (never replacing a send that went out) and answers
/// <see cref="AccountantPackageMonthlyOutcome.Failed"/>; the job fails once it has tried every org. «Send a test» does not record its failures: they are the answer.</item>
/// <item><b>Nothing personal in a log.</b> The address of the accountant is read to be queued and never written; the link is never written; the logs carry the ids of the org, the month, the counts and the codes.</item>
/// </list>
/// </remarks>
public sealed class AccountantPackageDelivery(
    AppDbContext db,
    IAccountantPackageService packages,
    IFileStorage storage,
    IEmailQueue emailQueue,
    IOptions<EmailOptions> emailOptions,
    AccountantPackageLinks links,
    IFeatureFlags featureFlags,
    ILogger<AccountantPackageDelivery> logger,
    TimeProvider? timeProvider = null) : IAccountantPackageDeliveryService
{
    private const string ZipContentType = "application/zip";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<Guid>> DueOrganizationsAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AccountantPackage))
            return [];

        var period = PreviousMonth(today).Key;
        var sent = AccountantPackageSendResult.Sent;

        // The address is asked for with IS NOT NULL only: an encrypted column is never compared with a text.
        return await db.AccountantPackageSettings
            .AsNoTracking()
            .Where(s => s.MonthlySendEnabled
                        && s.AccountantEmail != null
                        && s.SendDayOfMonth <= today.Day
                        && (s.LastSendPeriod != period || s.LastSendResult != sent)
                        && db.Orgs.Any(o => o.Id == s.OrgId && o.IsActive))
            .OrderBy(s => s.OrgId)
            .Select(s => s.OrgId)
            .ToListAsync(cancellationToken);
    }

    public async Task<AccountantPackageMonthlyOutcome> SendMonthlyAsync(Guid orgId, DateOnly today, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AccountantPackage))
            return AccountantPackageMonthlyOutcome.NotConfigured;

        var period = PreviousMonth(today);
        var row = await db.AccountantPackageSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is not { MonthlySendEnabled: true, AccountantEmail.Length: > 0 })
            return AccountantPackageMonthlyOutcome.NotConfigured;

        if (row.SendDayOfMonth > today.Day)
            return AccountantPackageMonthlyOutcome.NotDue;

        if (IsSent(row, period.Key))
            return AccountantPackageMonthlyOutcome.AlreadySent;

        var delivery = await DeliverAsync(orgId, period, AccountantPackageAccess.Full.Narrowed(row.Contents), monthly: true, cancellationToken);
        switch (delivery.State)
        {
            case DeliveryState.Sent:
                return AccountantPackageMonthlyOutcome.Sent;
            case DeliveryState.AlreadySent:
                return AccountantPackageMonthlyOutcome.AlreadySent;
            case DeliveryState.NotConfigured:
                return AccountantPackageMonthlyOutcome.NotConfigured;
            case DeliveryState.Changed:
                return AccountantPackageMonthlyOutcome.SettingsChanged;
            default:
                if (!delivery.Recorded)
                    await RecordFailureAsync(orgId, period.Key, delivery.Error!, cancellationToken);

                return AccountantPackageMonthlyOutcome.Failed;
        }
    }

    public async Task<AccountantPackageSendNowResult> SendNowAsync(Guid orgId, AccountantPackageAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (!featureFlags.IsEnabled(FeatureFlags.AccountantPackage))
            throw new DomainRuleException(AccountantPackageErrors.NotSent, "AccountantPackageNotSent");

        var period = PreviousMonth(_clock.TodayInRomeAsDateOnly());
        var row = await db.AccountantPackageSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is not { AccountantEmail.Length: > 0 })
            throw new DomainRuleException(AccountantPackageErrors.RecipientMissing, "AccountantPackageRecipientMissing");

        var delivery = await DeliverAsync(orgId, period, access.Narrowed(row.Contents), monthly: false, cancellationToken);
        return delivery.State switch
        {
            DeliveryState.Sent => new AccountantPackageSendNowResult(period.Key, delivery.LinkExpiresAt!.Value),
            DeliveryState.NotConfigured => throw new DomainRuleException(AccountantPackageErrors.RecipientMissing, "AccountantPackageRecipientMissing"),
            _ => throw new DomainRuleException(AccountantPackageErrors.NotSent, "AccountantPackageNotSent"),
        };
    }

    public async Task<int> DeleteExpiredPackagesAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - AccountantPackageLinkRules.Retention;
        var due = await db.AccountantPackageSettings
            .AsNoTracking()
            .Where(s => s.StoredPackagePeriod != null && s.StoredPackageAt < cutoff)
            .OrderBy(s => s.OrgId)
            .Select(s => s.OrgId)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        var failures = new List<Exception>();
        foreach (var orgId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await DeleteExpiredAsync(orgId, cutoff, cancellationToken))
                    deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An object that cannot be deleted stays, named by its row, and is tried again at the next run; it never stops the others, and the run says it failed once all were tried.
                logger.LogError(ex, "The expired accountant package of org {OrgId} could not be deleted", orgId);
                failures.Add(ex);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} expired accountant packages could not be deleted", failures);

        return deleted;
    }

    public async Task<AccountantPackageFile?> OpenAsync(Guid orgId, string? period, string? expiresAt, string? signature, CancellationToken cancellationToken = default)
    {
        // The signature is checked first and costs no query: a link that was not made for this org and this month stops here, whatever else is true.
        if (!links.Verify(orgId, period, expiresAt, signature))
        {
            logger.LogInformation("A link to the accountant package of org {OrgId} did not open: it is not valid or has expired", orgId);
            return null;
        }

        if (!AccountantPackagePeriod.TryParse(period, out var parsed) || parsed.Kind != AccountantPackagePeriodKind.Month || parsed.Key != period)
            return null;

        var stream = await storage.OpenReadAsync(StorageBucket.Private, StorageKeys.AccountantPackage(orgId, parsed.Key), cancellationToken);
        if (stream is null)
        {
            logger.LogInformation("The accountant package {Period} of org {OrgId} is no longer there: its link does not open", parsed.Key, orgId);
            return null;
        }

        logger.LogInformation("The accountant package {Period} of org {OrgId} was opened with its link", parsed.Key, orgId);
        return new AccountantPackageFile(stream, parsed.FileName);
    }

    // ---------------------------------------------------------------- one send

    /// <summary>
    /// Makes the package of <paramref name="period"/>, keeps it and queues the e-mail with its link, as the remarks say. <paramref name="monthly"/> is the run of the job: its failures are answers
    /// (<see cref="DeliveryState.Failed"/>) that the caller records, and it stops when the month is already sent; a test of a person lets the exceptions of the package that are the person's to read (the caps) through,
    /// and sends again whatever was sent.
    /// </summary>
    private async Task<Delivery> DeliverAsync(Guid orgId, AccountantPackagePeriod period, AccountantPackageAccess access, bool monthly, CancellationToken cancellationToken)
    {
        var key = period.Key;

        // What this environment cannot send is known before anything is made.
        if (!links.IsConfigured)
        {
            logger.LogWarning("The accountant package {Period} of org {OrgId} was not sent: the secret of the signed links or the public address of this API is not configured", key, orgId);
            return Delivery.Failure(AccountantPackageSendErrors.LinkNotConfigured);
        }

        if (!emailOptions.Value.IsConfigured)
        {
            logger.LogWarning("The accountant package {Period} of org {OrgId} was not sent: the e-mail provider is not configured", key, orgId);
            return Delivery.Failure(AccountantPackageSendErrors.EmailNotConfigured);
        }

        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId && o.IsActive)
            .Select(o => new OrgFacts(o.DisplayName, o.Name, o.DefaultLanguage, o.PlanTier, o.IsAgencyTierDowngraded))
            .FirstOrDefaultAsync(cancellationToken);
        if (org is null)
            return Delivery.NotConfigured();

        // The owner accounting follows the plan, never the job: the package of an organization that is not on the agency plan holds none, whatever it chose and whoever asks (the agency.* policies ask for the same plan).
        var ownerAccountingPlan = AccountantPackageFolderRules.PlanHoldsOwnerAccounting(org.PlanTier, org.AgencyTierDowngraded);

        // A test sends what the monthly send sends and counts as the send of its month, so on an agency's organization it needs the permission to read the owner accounting: without it the package would be a partial one that the monthly
        // run would never complete (the month is recorded as sent) and that would replace, under the same key, a complete package the accountant already has. Nothing is made, stored, queued or recorded.
        if (!monthly && ownerAccountingPlan && !access.OwnerAccounting)
            throw new DomainRuleException(AccountantPackageErrors.OwnerAccountingRequired, "AccountantPackageOwnerAccountingRequired");

        access = access with { OwnerAccounting = access.OwnerAccounting && ownerAccountingPlan };

        // The package, made outside the lock.
        await using var buffer = new MemoryStream();
        AccountantPackageResult built;
        try
        {
            built = await packages.BuildAsync(new HostScope(orgId), access, period, buffer, cancellationToken);
        }
        catch (DomainRuleException ex) when (monthly && ex.Code == AccountantPackageErrors.TooLarge)
        {
            logger.LogWarning("The accountant package {Period} of org {OrgId} is over the caps of the archive: it is not sent", key, orgId);
            return Delivery.Failure(AccountantPackageSendErrors.PackageTooLarge);
        }
        catch (Exception ex) when (monthly && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The accountant package {Period} of org {OrgId} could not be made", key, orgId);
            return Delivery.Failure(AccountantPackageSendErrors.PackageFailed);
        }

        // Everything else is one step under the lock of the org.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.AccountantPackage, orgId.ToString("N")));

        var row = await db.AccountantPackageSettings.FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is not { AccountantEmail.Length: > 0 } || (monthly && !row.MonthlySendEnabled))
            return Delivery.NotConfigured();

        if (monthly && IsSent(row, key))
            return Delivery.AlreadySent();

        // The package was made, outside the lock, with the folders the settings had when the send began (the access is narrowed to them by the caller). If the organization changed them in between (took a folder out, for
        // instance), that package holds folders it no longer chose: it is neither stored nor mailed, nothing is recorded, and the next run makes it again with the folders of the settings.
        if (row.Contents != access.Folders)
        {
            logger.LogInformation("The folders of the accountant package {Period} of org {OrgId} changed while it was being made: it is not stored or queued", key, orgId);
            return Delivery.Changed();
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        try
        {
            // One package at a time for an org: the one of an older month is no longer named by any link worth keeping, and it is deleted FIRST, while the row still names it. An object that cannot be deleted is then a failure that is
            // recorded and tried again (the row keeps naming it, so the nightly run deletes it too), never one that stays in the bucket with no row that names it.
            if (row.StoredPackagePeriod is { } older && older != key)
                await storage.DeleteAsync(StorageBucket.Private, StorageKeys.AccountantPackage(orgId, older), cancellationToken);

            buffer.Position = 0;
            await storage.PutAsync(StorageBucket.Private, StorageKeys.AccountantPackage(orgId, key), buffer, ZipContentType, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The accountant package {Period} of org {OrgId} could not be stored, or the older one could not be deleted", key, orgId);
            return Delivery.Failure(AccountantPackageSendErrors.StorageFailed);
        }

        row.StoredPackagePeriod = key;
        row.StoredPackageAt = now;

        var link = links.Create(orgId, key);
        var content = EmailTemplates.AccountantPackage(
            OrgInvitationEmails.CultureOf(org.Language),
            string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName,
            key,
            link.Url,
            link.ExpiresAt);
        if (!emailQueue.Enqueue(row.AccountantEmail, content, EmailTemplates.Names.AccountantPackage))
        {
            logger.LogWarning("The e-mail with the accountant package {Period} of org {OrgId} could not be queued", key, orgId);
            if (monthly)
                MarkFailed(row, key, AccountantPackageSendErrors.EmailNotQueued, now);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return Delivery.Failure(AccountantPackageSendErrors.EmailNotQueued, recorded: monthly);
        }

        row.LastSendPeriod = key;
        row.LastSendAt = now;
        row.LastSendResult = AccountantPackageSendResult.Sent;
        row.LastSendError = null;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Accountant package {Period} of org {OrgId} queued for the accountant: {Documents} documents, {Files} files, {Bytes} bytes, the link expires at {ExpiresAt}",
            key,
            orgId,
            built.Documents,
            built.Files,
            built.Bytes,
            link.ExpiresAt);
        return Delivery.Sent(link.ExpiresAt);
    }

    /// <summary>
    /// Writes <c>failed</c> and its code in the settings, under the lock of the org, unless the month is already sent (a failure never replaces a send that went out). The tracker is cleared first: nothing a
    /// failed attempt left in it is saved with the failure.
    /// </summary>
    private async Task RecordFailureAsync(Guid orgId, string period, string error, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.AccountantPackage, orgId.ToString("N")));

        var row = await db.AccountantPackageSettings.FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is null || IsSent(row, period))
            return;

        MarkFailed(row, period, error, _clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private static void MarkFailed(AccountantPackageSettings row, string period, string error, DateTime now)
    {
        if (IsSent(row, period))
            return;

        row.LastSendPeriod = period;
        row.LastSendAt = now;
        row.LastSendResult = AccountantPackageSendResult.Failed;
        row.LastSendError = error;
    }

    /// <summary>Deletes the package of an org that its link has outlived, and forgets it in the row, under the lock of the org, if it is still the old one (a send in between has replaced it). True when it deleted one.</summary>
    private async Task<bool> DeleteExpiredAsync(Guid orgId, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.AccountantPackage, orgId.ToString("N")));

        var row = await db.AccountantPackageSettings.FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is not { StoredPackagePeriod: { } period, StoredPackageAt: { } storedAt } || storedAt >= cutoff)
            return false;

        await storage.DeleteAsync(StorageBucket.Private, StorageKeys.AccountantPackage(orgId, period), cancellationToken);
        row.StoredPackagePeriod = null;
        row.StoredPackageAt = null;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("The accountant package {Period} of org {OrgId} was deleted: its link has expired", period, orgId);
        return true;
    }

    /// <summary>True when the row says the accountant already has the package of <paramref name="period"/>.</summary>
    private static bool IsSent(AccountantPackageSettings row, string period) =>
        row.LastSendPeriod == period && row.LastSendResult == AccountantPackageSendResult.Sent;

    /// <summary>The month before the day <paramref name="today"/> (Europe/Rome): the month a package sent today is of.</summary>
    internal static AccountantPackagePeriod PreviousMonth(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return AccountantPackagePeriod.OfMonth(first.Year, first.Month);
    }

    private sealed record OrgFacts(string DisplayName, string Name, string Language, PlanTier PlanTier, bool AgencyTierDowngraded);

    private enum DeliveryState
    {
        Sent,
        AlreadySent,
        NotConfigured,
        Changed,
        Failed,
    }

    /// <summary>What <see cref="DeliverAsync"/> did: a state, for a failure its code and whether it is already written in the settings, for a send the last instant its link opens.</summary>
    private readonly record struct Delivery(DeliveryState State, string? Error = null, bool Recorded = false, DateTime? LinkExpiresAt = null)
    {
        public static Delivery Sent(DateTime linkExpiresAt) => new(DeliveryState.Sent, LinkExpiresAt: linkExpiresAt);

        public static Delivery AlreadySent() => new(DeliveryState.AlreadySent);

        public static Delivery NotConfigured() => new(DeliveryState.NotConfigured);

        public static Delivery Changed() => new(DeliveryState.Changed);

        public static Delivery Failure(string error, bool recorded = false) => new(DeliveryState.Failed, error, recorded);
    }
}
