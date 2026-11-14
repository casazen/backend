using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupportVaultService" />
/// <remarks>
/// <para><b>Staff works across orgs.</b> The platform administrator's context is under the tenant of its own org, so every read lifts the tenant filter
/// by name and names the org it means: the org of the credential for the writes, the org <b>of the request</b> for the opening. A credential of
/// another org or of another property of the same org is never found for a request.</para>
/// <para><b>The opening is one unit.</b> Under the transaction lock of the request (the one its staff updates take: <c>assist:{id:N}</c>), the request
/// is read again, the openings of the last hour are counted, the values are read, the access row and the line of the log are staged and saved
/// together. Two openings at once never pass the limit, and an opening that fails leaves no row and no line.</para>
/// <para><b>Logs.</b> Ids, kinds and counts only: never a value, never the reason.</para>
/// </remarks>
public sealed class SupportVaultService(
    AppDbContext db,
    IActivityLog activityLog,
    TimeProvider timeProvider,
    ILogger<SupportVaultService> logger) : ISupportVaultService
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    public async Task<SupportVaultSecretPresence> StoreAsync(string staffUserId, SupportVaultSecretInput input, CancellationToken ct)
    {
        CheckUserId(staffUserId);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Kind is not (SupportVaultSecretKind.OctoratePanel or SupportVaultSecretKind.RegionalIstat))
            throw new DomainRuleException(SupportVaultCodes.KindInvalid, "SupportVaultKindInvalid");

        var values = Checked(input);
        if (!await db.Orgs.AsNoTracking().AnyAsync(o => o.Id == input.OrgId && o.OrgType == OrgType.Host, ct))
        {
            throw new NotFoundException("Org not found.")
            {
                Code = SupportVaultCodes.OrgNotFound,
                MessageKey = "SupportVaultOrgNotFound",
            };
        }

        if (input.PropertyId is { } propertyId
            && !await db.Properties.IgnoreQueryFilters([Tenant]).AsNoTracking().AnyAsync(p => p.Id == propertyId && p.OrgId == input.OrgId, ct))
        {
            throw new NotFoundException("Property not found.")
            {
                Code = "property_not_found",
                MessageKey = "PropertyNotFound",
            };
        }

        var now = Now();
        for (var attempt = 1; ; attempt++)
        {
            var row = await db.SupportVaultSecrets
                .IgnoreQueryFilters([Tenant])
                .FirstOrDefaultAsync(s => s.OrgId == input.OrgId && s.PropertyId == input.PropertyId && s.Kind == input.Kind, ct);
            var created = row is null;
            if (row is null)
            {
                row = new SupportVaultSecret
                {
                    OrgId = input.OrgId,
                    PropertyId = input.PropertyId,
                    Kind = input.Kind,
                    CreatedByUserId = staffUserId,
                    CreatedAt = now,
                };
                db.SupportVaultSecrets.Add(row);
            }

            row.Username = values.Username;
            row.Password = values.Password;
            row.ExtraCode = values.ExtraCode;
            row.UpdatedByUserId = staffUserId;
            row.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt == 1 && created && OrgTeamAccess.IsUniqueViolation(ex, SupportVaultSecret.SecretIndexName))
            {
                // A parallel first save of the same credential won: the values replace that row instead.
                db.Entry(row).State = EntityState.Detached;
                continue;
            }

            logger.LogInformation(
                "Support vault: credential {SecretId} ({Kind}) of org {OrgId}, property {PropertyId} {Change} by staff {StaffUserId}",
                row.Id, row.Kind, row.OrgId, row.PropertyId, created ? "stored" : "replaced", staffUserId);
            return PresenceOf(row);
        }
    }

    public async Task<IReadOnlyList<SupportVaultSecretPresence>> ListAsync(Guid orgId, CancellationToken ct) =>
        await db.SupportVaultSecrets
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId)
            .OrderBy(s => s.PropertyId)
            .ThenBy(s => s.Kind)
            .Select(s => new SupportVaultSecretPresence(s.Id, s.OrgId, s.PropertyId, s.Kind, s.CreatedAt, s.UpdatedAt, s.UpdatedByUserId))
            .ToListAsync(ct);

    public async Task<bool> DeleteAsync(string staffUserId, Guid secretId, CancellationToken ct)
    {
        CheckUserId(staffUserId);
        var row = await db.SupportVaultSecrets.IgnoreQueryFilters([Tenant]).FirstOrDefaultAsync(s => s.Id == secretId, ct);
        if (row is null)
            return false;

        db.SupportVaultSecrets.Remove(row);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Support vault: credential {SecretId} ({Kind}) of org {OrgId} removed by staff {StaffUserId}", row.Id, row.Kind, row.OrgId, staffUserId);
        return true;
    }

    public async Task<SupportVaultReveal> RevealAsync(
        string staffUserId,
        Guid requestId,
        SupportVaultCredentialKind kind,
        string? reason,
        CancellationToken ct)
    {
        CheckUserId(staffUserId);
        if (!Enum.IsDefined(kind))
            throw new DomainRuleException(SupportVaultCodes.KindInvalid, "SupportVaultKindInvalid");
        if (!SupportVaultRules.TryNormalizeReason(reason, out var why))
        {
            throw new DomainRuleException(
                SupportVaultCodes.ReasonInvalid, "SupportVaultReasonInvalid", SupportVaultAccess.ReasonMinLength, SupportVaultAccess.ReasonMaxLength);
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.OnboardingPanelAssist, $"assist:{requestId:N}"));

        var request = await db.OnboardingAssistRequests
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new NotFoundException("Assist request not found.")
            {
                Code = OnboardingAssistCodes.NotFound,
                MessageKey = "AssistRequestNotFound",
            };
        if (request.Status is not (OnboardingAssistStatus.Requested or OnboardingAssistStatus.InProgress))
            throw new DomainConflictException(SupportVaultCodes.RequestNotOpen, "SupportVaultRequestNotOpen");
        if (!SupportVaultRules.KindFits(request.PartKey, request.Context, request.PropertyId, kind))
            throw new DomainRuleException(SupportVaultCodes.KindNotForRequest, "SupportVaultKindNotForRequest");

        // The limit counts the openings of the request, whatever their kind, in the hour before now.
        var now = Now();
        var windowStart = now.AddHours(-1);
        var recent = await db.SupportVaultAccesses
            .IgnoreQueryFilters([Tenant])
            .CountAsync(a => a.RequestId == request.Id && a.AccessedAt > windowStart, ct);
        if (recent >= SupportVaultLimits.MaxOpeningsPerRequestPerHour)
        {
            logger.LogWarning(
                "Support vault: opening {Kind} of assist request {RequestId} of org {OrgId} refused to staff {StaffUserId}: {Count} openings in the last hour",
                SupportVaultRules.CodeOf(kind), request.Id, request.OrgId, staffUserId, recent);
            throw new DomainTooManyRequestsException(
                SupportVaultCodes.TooManyOpenings, "SupportVaultTooManyOpenings", SupportVaultLimits.MaxOpeningsPerRequestPerHour);
        }

        var values = await ReadAsync(request, kind, ct)
            ?? throw new NotFoundException("Credential not found in the support vault.")
            {
                Code = SupportVaultCodes.SecretMissing,
                MessageKey = "SupportVaultSecretMissing",
            };

        // The audit and the line the host reads, in the org OF THE REQUEST, in the save of the opening: never the value, never the reason in the line.
        db.SupportVaultAccesses.Add(new SupportVaultAccess
        {
            OrgId = request.OrgId,
            RequestId = request.Id,
            PropertyId = request.PropertyId,
            Kind = kind,
            Reason = why,
            AccessedByUserId = staffUserId,
            AccessedAt = now,
        });
        activityLog.Record(OrgActivity.Of(
            request.OrgId,
            OrgActivityType.OnboardingAssistUpdated,
            staffUserId,
            request.OrgId.ToString(),
            (OrgActivityDetailKeys.Part, OnboardingPartCatalog.Of(request.PartKey).Code),
            (OrgActivityDetailKeys.Context, request.Context),
            (OrgActivityDetailKeys.Status, OnboardingAssistRules.StatusCode(request.Status)),
            (OrgActivityDetailKeys.Credential, SupportVaultRules.CodeOf(kind))));
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Support vault: credential {Kind} opened for assist request {RequestId} of org {OrgId} by staff {StaffUserId}",
            SupportVaultRules.CodeOf(kind), request.Id, request.OrgId, staffUserId);
        return new SupportVaultReveal(
            kind,
            values.Username,
            values.Password,
            string.IsNullOrEmpty(values.ExtraCode) ? null : values.ExtraCode,
            now,
            now.AddSeconds(SupportVaultLimits.VisibleSeconds));
    }

    /// <summary>
    /// The values of one kind of credential for the org and the property of the request, or <c>null</c>. The panel of the structure is the vault's;
    /// the police credentials are the property's Alloggiati Web credentials (their web service key as the further code); the ISTAT ones are the
    /// host's own form, else the ones support stored. Each read names the org of the request and its property.
    /// </summary>
    private async Task<VaultValues?> ReadAsync(OnboardingAssistRequest request, SupportVaultCredentialKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case SupportVaultCredentialKind.OctoratePanel:
                return await SecretOfAsync(request.OrgId, request.PropertyId, SupportVaultSecretKind.OctoratePanel, ct);

            case SupportVaultCredentialKind.Police:
                return await db.PropertyQuesturaCredentials
                    .IgnoreQueryFilters([Tenant])
                    .AsNoTracking()
                    .Where(c => c.OrgId == request.OrgId && c.PropertyId == request.PropertyId)
                    .Select(c => new VaultValues(c.Username, c.Password, c.WsKey))
                    .FirstOrDefaultAsync(ct);

            case SupportVaultCredentialKind.RegionalIstat:
                return await db.PropertyIstatCredentials
                           .IgnoreQueryFilters([Tenant])
                           .AsNoTracking()
                           .Where(c => c.OrgId == request.OrgId && c.PropertyId == request.PropertyId)
                           .Select(c => new VaultValues(c.Username, c.Password, c.StructureCode))
                           .FirstOrDefaultAsync(ct)
                       ?? await SecretOfAsync(request.OrgId, request.PropertyId, SupportVaultSecretKind.RegionalIstat, ct);

            default:
                return null;
        }
    }

    private Task<VaultValues?> SecretOfAsync(Guid orgId, Guid? propertyId, SupportVaultSecretKind kind, CancellationToken ct) =>
        db.SupportVaultSecrets
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.PropertyId == propertyId && s.Kind == kind)
            .Select(s => new VaultValues(s.Username, s.Password, s.ExtraCode))
            .FirstOrDefaultAsync(ct);

    /// <summary>The values to store: the user name trimmed, the password exactly as typed, the further code trimmed (none when blank); 422 otherwise.</summary>
    private static VaultValues Checked(SupportVaultSecretInput input)
    {
        var username = input.Username?.Trim();
        var password = input.Password;
        var extraCode = string.IsNullOrWhiteSpace(input.ExtraCode) ? null : input.ExtraCode.Trim();
        if (string.IsNullOrEmpty(username)
            || username.Length > SupportVaultSecret.MaxUsernameLength
            || string.IsNullOrEmpty(password)
            || password.Length > SupportVaultSecret.MaxPasswordLength
            || extraCode?.Length > SupportVaultSecret.MaxExtraCodeLength)
        {
            throw new DomainRuleException(SupportVaultCodes.ValueInvalid, "SupportVaultValueInvalid");
        }

        return new VaultValues(username, password, extraCode);
    }

    private static SupportVaultSecretPresence PresenceOf(SupportVaultSecret row) =>
        new(row.Id, row.OrgId, row.PropertyId, row.Kind, row.CreatedAt, row.UpdatedAt, row.UpdatedByUserId);

    private static void CheckUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > SupportVaultAccess.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(userId));
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The values of a credential, in clear, inside the service only: it hides them from <c>ToString</c> too.</summary>
    private sealed record VaultValues(string Username, string Password, string? ExtraCode)
    {
        public override string ToString() => nameof(VaultValues) + " { *** }";
    }
}
