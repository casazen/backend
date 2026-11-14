using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAccountantPackageSettingsService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Every query names the org.</b> The row is found by <see cref="AccountantPackageSettings.OrgId"/> and no other id, so an org never reads or writes the settings of another, whatever tenant context the
/// caller runs in.</item>
/// <item><b>Saved as a whole, under the lock of the org.</b> A save takes the lock of <see cref="PostgresAdvisoryLocks.Scope.AccountantPackage"/> (the one the send takes to record a month), reads the row again and writes
/// all the fields of the change in one save, so two saves never mix their fields and a save never lands between the check that a month was sent and the line that says so. The first save creates the row; the
/// unique index on the org is the last word if two first saves run on a database that does not lock.</item>
/// <item><b>The owner accounting is not for everyone to send.</b> On an organization on the agency plan a caller that does not hold <c>agency.statement.view</c> cannot choose the folders that carry the owner accounting
/// (<see cref="AccountantPackageFolderRules.OwnerAccountingCarriers"/>): the refusal is a 422 and saves nothing. The save replaces the folders as a whole, so what such a caller saves holds none of them.</item>
/// <item><b>What a save leaves alone.</b> The history of the last send and the package that waits in the storage are the send's: a change of the address, the day or the folders does not erase what was sent.</item>
/// <item><b>Nothing personal in the log.</b> A save writes the ids of the org and of the user, whether the send is on, the day and the number of folders: never the address.</item>
/// </list>
/// </remarks>
public sealed class AccountantPackageSettingsService(
    AppDbContext db,
    ILogger<AccountantPackageSettingsService> logger,
    TimeProvider? timeProvider = null) : IAccountantPackageSettingsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AccountantPackageSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var row = await db.AccountantPackageSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        return ViewOf(row);
    }

    public async Task<AccountantPackageSettingsView> SaveAsync(
        Guid orgId,
        AccountantPackageSettingsChange change,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (enabled, email, day, contents) = Validate(change);

        // The owner accounting is the agency's: whoever chooses the folders that carry it for the package sent to a third party must hold agency.statement.view (the web layer says so in the change). The plan is read here, so an
        // organization that is not on the agency plan (whose package holds no owner accounting) is never refused.
        if (!change.OwnerAccounting && (contents & AccountantPackageFolderRules.OwnerAccountingCarriers) != 0 && await HoldsOwnerAccountingAsync(orgId, cancellationToken))
            throw new DomainRuleException(AccountantPackageErrors.OwnerAccountingRequired, "AccountantPackageOwnerAccountingRequired");

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.AccountantPackage, orgId.ToString("N")));

        var row = await db.AccountantPackageSettings.FirstOrDefaultAsync(s => s.OrgId == orgId, cancellationToken);
        if (row is null)
        {
            row = new AccountantPackageSettings { OrgId = orgId, CreatedAt = _clock.GetUtcNow().UtcDateTime };
            db.AccountantPackageSettings.Add(row);
        }

        row.AccountantEmail = email;
        row.MonthlySendEnabled = enabled;
        row.SendDayOfMonth = day;
        row.Contents = contents;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        row.UpdatedByUserId = UserOf(userId);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Accountant package settings of org {OrgId} saved by {UserId}: monthly send {Enabled}, day {SendDay}, {Folders} folders",
            orgId,
            userId,
            enabled,
            day,
            AccountantPackageFolderRules.KeysOf(contents).Count);
        return ViewOf(row);
    }

    /// <summary>True when the organization is on the agency plan (<see cref="AccountantPackageFolderRules.PlanHoldsOwnerAccounting"/>): the package it sends holds the owner accounting.</summary>
    private async Task<bool> HoldsOwnerAccountingAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var plan = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => new { o.PlanTier, o.IsAgencyTierDowngraded }).FirstOrDefaultAsync(cancellationToken);
        return plan is not null && AccountantPackageFolderRules.PlanHoldsOwnerAccounting(plan.PlanTier, plan.IsAgencyTierDowngraded);
    }

    /// <summary>The id of the user as the column keeps it: trimmed and at most the length of the column; <c>null</c> for none.</summary>
    private static string? UserOf(string? userId)
    {
        var user = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        return user is { Length: > AccountantPackageSettings.UpdatedByUserIdMaxLength } ? user[..AccountantPackageSettings.UpdatedByUserIdMaxLength] : user;
    }

    /// <summary>The settings as a view; the defaults of an org that has none.</summary>
    internal static AccountantPackageSettingsView ViewOf(AccountantPackageSettings? row)
    {
        if (row is null)
        {
            return new AccountantPackageSettingsView(
                Enabled: false,
                Email: null,
                AccountantPackageSettings.DefaultSendDay,
                AccountantPackageFolders.All,
                LastSend: null,
                UpdatedAt: null);
        }

        var lastSend = row is { LastSendPeriod: { } period, LastSendAt: { } at, LastSendResult: { } result }
            ? new AccountantPackageLastSend(period, DateTime.SpecifyKind(at, DateTimeKind.Utc), result, row.LastSendError)
            : null;
        return new AccountantPackageSettingsView(
            row.MonthlySendEnabled,
            row.AccountantEmail,
            row.SendDayOfMonth,
            row.Contents,
            lastSend,
            DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// The change as the row stores it, or a 422. The order is the order a person fixes them: what is missing, the address, the send without an address, the day, the folders. A refused change saves nothing.
    /// </summary>
    private static (bool Enabled, string? Email, int Day, AccountantPackageFolders Contents) Validate(AccountantPackageSettingsChange change)
    {
        if (change.Enabled is not { } enabled || change.SendDay is not { } day || change.Contents is null)
            throw new DomainRuleException(AccountantPackageErrors.SettingsInvalid, "AccountantPackageSettingsInvalid");

        var email = NormalizeEmail(change.Email);
        if (enabled && email is null)
            throw new DomainRuleException(AccountantPackageErrors.RecipientMissing, "AccountantPackageRecipientMissing");

        if (day is < AccountantPackageSettings.MinSendDay or > AccountantPackageSettings.MaxSendDay)
            throw new DomainRuleException(AccountantPackageErrors.SendDayInvalid, "AccountantPackageSendDayInvalid", AccountantPackageSettings.MinSendDay, AccountantPackageSettings.MaxSendDay);

        if (!AccountantPackageFolderRules.TryFromKeys(change.Contents, out var contents))
            throw new DomainRuleException(AccountantPackageErrors.ContentsInvalid, "AccountantPackageContentsInvalid");

        return (enabled, email, day, contents);
    }

    /// <summary>
    /// The plain address (<see cref="CrmNormalizer.TryNormalizeEmail"/>: trimmed, lower case, no name, no spaces, a host with a dot) of at most 254 characters, or <c>null</c> for none. An empty address is never stored as
    /// <c>""</c>. The address is not in the message of the refusal.
    /// </summary>
    internal static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        if (!CrmNormalizer.TryNormalizeEmail(email, out var normalized) || normalized.Length > AccountantPackageSettings.EmailMaxLength)
            throw new DomainRuleException(AccountantPackageErrors.RecipientInvalid, "AccountantPackageRecipientInvalid");

        return normalized;
    }
}
