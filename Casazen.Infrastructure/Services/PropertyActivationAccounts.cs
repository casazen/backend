using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Hangfire;

namespace Casazen.Infrastructure.Services;

public sealed record ActivationAccountUnit(Guid PropertyId, [MaxLength(100)] string? Code);
public sealed record ActivationAccountInput(
    [Required, MaxLength(25)] string Service,
    [Required, MaxLength(200)] string Username,
    [Required, MaxLength(200)] string Password,
    [MaxLength(200)] string? WebServiceKey,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<ActivationAccountUnit> Properties)
{
    public override string ToString() => nameof(ActivationAccountInput);
}

public sealed record ActivationAccountView(Guid Id, string Service, DateTime ExpiresAt, Guid[] PropertyIds);
public sealed record ActivationAccountOutcome(Guid PropertyId, Guid? ActivationId, Guid? RequestId, string? ErrorCode);

public sealed class PropertyActivationAccounts(
    AppDbContext db,
    IMarketplaceChannelActivationService channels,
    PropertyActivationServices services,
    TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ActivationAccountView> StoreAsync(HostScope scope, ActivationAccountInput input, CancellationToken ct)
    {
        var portal = await PortalAsync(input.Service, ct);
        var ids = input.Properties.Select(p => p.PropertyId).ToArray();
        if (ids.Length is < 1 or > 100 || ids.Distinct().Count() != ids.Length
            || string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrEmpty(input.Password)
            || (input.Service == "alloggiati" && string.IsNullOrWhiteSpace(input.WebServiceKey))
            || (portal?.RequiresHotelId == true && input.Properties.Any(p => string.IsNullOrWhiteSpace(p.Code))))
            throw Invalid();

        var properties = await db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId && ids.Contains(p.Id)
                && p.RentalMode == RentalMode.Short).InScope(scope).ToListAsync(ct);
        if (properties.Count != ids.Length)
            throw NotFound();
        if (portal is null)
        {
            if (properties.Any(p => p.Country != "IT"))
                throw Invalid();
            if (input.Service == "istat" && properties.Count > 1 &&
                (properties.Any(p => string.IsNullOrWhiteSpace(p.RegionCode)) || properties.Select(p => p.RegionCode).Distinct().Count() != 1))
                throw Invalid();
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.PropertyPreparation, $"accounts:{scope.OrgId:N}:{input.Service}"));
        var account = new PropertyActivationCredential
        {
            OrgId = scope.OrgId,
            Service = input.Service,
            Username = input.Username.Trim(),
            Password = input.Password,
            WebServiceKey = input.Service == "alloggiati" ? input.WebServiceKey?.Trim() : null,
            ExpiresAt = Now.AddDays(7),
        };
        db.PropertyActivationCredentials.Add(account);
        var old = await db.PropertyActivationCredentialAssignments.Where(a =>
            a.OrgId == scope.OrgId && a.Service == input.Service && ids.Contains(a.PropertyId)).ToDictionaryAsync(a => a.PropertyId, ct);
        var previousIds = old.Values.Select(a => a.AccountId).Distinct().ToArray();
        foreach (var item in input.Properties)
        {
            if (!old.TryGetValue(item.PropertyId, out var assignment))
            {
                assignment = new PropertyActivationCredentialAssignment
                {
                    OrgId = scope.OrgId,
                    Service = input.Service,
                    PropertyId = item.PropertyId,
                };
                db.PropertyActivationCredentialAssignments.Add(assignment);
            }
            assignment.AccountId = account.Id;
            assignment.UnitCode = string.IsNullOrWhiteSpace(item.Code) ? null : item.Code.Trim();
        }
        await db.SaveChangesAsync(ct);

        var orphaned = await db.PropertyActivationCredentials.Where(a => a.OrgId == scope.OrgId && previousIds.Contains(a.Id)
            && !db.PropertyActivationCredentialAssignments.Any(p => p.OrgId == scope.OrgId && p.AccountId == a.Id)).ToListAsync(ct);
        db.PropertyActivationCredentials.RemoveRange(orphaned);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return new ActivationAccountView(account.Id, account.Service, account.ExpiresAt, ids);
    }

    public async Task<string> ServiceAsync(HostScope scope, Guid id, CancellationToken ct) =>
        (await ReadAsync(scope, id, ct)).Account.Service;

    public async Task<IReadOnlyList<ActivationAccountView>> ListAsync(HostScope scope, CancellationToken ct)
    {
        var reachable = db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var rows = await (from account in db.PropertyActivationCredentials.AsNoTracking()
                          join link in db.PropertyActivationCredentialAssignments.AsNoTracking() on account.Id equals link.AccountId
                          where account.OrgId == scope.OrgId && link.OrgId == scope.OrgId && reachable.Contains(link.PropertyId)
                          select new { account.Id, account.Service, account.ExpiresAt, link.PropertyId }).ToListAsync(ct);
        return rows.GroupBy(r => new { r.Id, r.Service, r.ExpiresAt }).Select(g =>
            new ActivationAccountView(g.Key.Id, g.Key.Service, g.Key.ExpiresAt, g.Select(r => r.PropertyId).ToArray())).ToArray();
    }

    public async Task<IReadOnlyList<ActivationAccountOutcome>> RunAsync(HostScope scope, string actor, Guid id, CancellationToken ct)
    {
        await using var operation = await LockAccountAsync(scope.OrgId, id, ct);
        var (account, assignments) = await ReadAsync(scope, id, ct);
        if (account.ExpiresAt <= Now)
            throw Invalid();
        var portal = await PortalAsync(account.Service, ct);
        var result = new List<ActivationAccountOutcome>();
        foreach (var assignment in assignments)
        {
            try
            {
                if (portal is null)
                {
                    var request = await services.RequestAsync(scope, actor, assignment.PropertyId, account.Service, ct);
                    result.Add(new(assignment.PropertyId, null, request.Id, null));
                }
                else
                {
                    var start = new MarketplaceActivationStart(MarketplaceActivationKind.Channel, portal.PortalCode,
                        assignment.PropertyId, assignment.UnitCode, account.Username, account.Password);
                    var activation = await channels.StartAsync(scope.OrgId, scope, start, ct);
                    result.Add(new(assignment.PropertyId, activation.ActivationId, null, null));
                }
            }
            catch (Exception ex) when (ex is DomainConflictException or DomainRuleException or OctorateException or NotFoundException)
            {
                var code = ex switch
                {
                    DomainConflictException conflict => conflict.Code,
                    DomainRuleException rule => rule.Code,
                    OctorateException provider => provider.Code,
                    _ => "property_not_found",
                };
                result.Add(new(assignment.PropertyId, null, null, code));
            }
        }
        return result;
    }

    public async Task DeleteAsync(HostScope scope, Guid id, CancellationToken ct)
    {
        await using var operation = await LockAccountAsync(scope.OrgId, id, ct);
        var (account, assignments) = await ReadAsync(scope, id, ct);
        db.PropertyActivationCredentialAssignments.RemoveRange(assignments);
        db.PropertyActivationCredentials.Remove(account);
        await db.SaveChangesAsync(ct);
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        await db.PropertyActivationCredentials.Where(c => c.ExpiresAt <= Now).ExecuteDeleteAsync(ct);
    }

    private async Task<(PropertyActivationCredential Account, PropertyActivationCredentialAssignment[] Assignments)> ReadAsync(
        HostScope scope, Guid id, CancellationToken ct)
    {
        var account = await db.PropertyActivationCredentials.SingleOrDefaultAsync(a => a.OrgId == scope.OrgId && a.Id == id, ct)
            ?? throw NotFound();
        var assignments = await db.PropertyActivationCredentialAssignments.Where(a => a.OrgId == scope.OrgId && a.AccountId == id
            && a.Service == account.Service).OrderBy(a => a.PropertyId).ToArrayAsync(ct);
        if (assignments.Length == 0)
            throw NotFound();
        var ids = assignments.Select(a => a.PropertyId).ToArray();
        var count = await db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId && ids.Contains(p.Id)
            && p.RentalMode == RentalMode.Short).InScope(scope).CountAsync(ct);
        if (count != ids.Length)
            throw NotFound();
        return (account, assignments);
    }

    private async Task<IAsyncDisposable> LockAccountAsync(Guid orgId, Guid id, CancellationToken ct) =>
        await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(db, PostgresAdvisoryLocks.Scope.PropertyPreparation, $"account:{orgId:N}:{id:N}", ct)
        ?? throw new DomainConflictException("activation_account_busy", "PropertyActivationServicesUnavailable");

    private async Task<MarketplacePortal?> PortalAsync(string service, CancellationToken ct)
    {
        if (service is "alloggiati" or "istat")
            return null;
        if (!service.StartsWith("ota:", StringComparison.Ordinal) || service.Length <= 4 || !service[4..].All(char.IsAsciiDigit))
            throw Invalid();
        var portalCode = service[4..];
        return await db.MarketplacePortals.AsNoTracking().SingleOrDefaultAsync(p => p.PortalCode == portalCode
            && p.Category == MarketplacePortalCategory.Ota && p.RequiresPassword && p.Creatable, ct) ?? throw Invalid();
    }

    private static DomainRuleException Invalid() => new("activation_account_invalid", "PropertyActivationServicesUnavailable");
    private static NotFoundException NotFound() => new("Activation account not found") { Code = "activation_account_not_found" };
}
