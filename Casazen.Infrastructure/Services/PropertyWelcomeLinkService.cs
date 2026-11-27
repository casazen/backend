using System.Buffers.Text;
using System.Data;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertyWelcomeLinkService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The token</b> is <see cref="PropertyWelcomeLink.TokenBytes"/> random bytes (192 bits) in base64url: 32 characters, nothing derived
/// from an id. It is unique in the whole table; a collision (one in 2<sup>192</sup>) is handled like the race below.</item>
/// <item><b>One active link for each property</b> is the partial unique index of the model (<c>UIX_PropertyWelcomeLinks_PropertyId_Active</c>),
/// not a check of this class: two requests that both find no link end with one, the other gets a unique violation and looks again.</item>
/// <item><b>Rotation</b> revokes the active link, saves it, and makes the new one, in a transaction. The revoke is saved first because the
/// order EF would run an update and an insert of the same unique index in one batch is not the one the index needs.</item>
/// <item><b>Tenant</b>: every query names the org, on top of the tenant filter of the scope the service runs in. The logs carry ids only.</item>
/// </list>
/// </remarks>
public class PropertyWelcomeLinkService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<PropertyWelcomeLinkService> logger) : IPropertyWelcomeLinkService
{
    /// <summary>How many times a write is repeated when another writer won the unique index at the same moment.</summary>
    private const int MaxAttempts = 3;

    public async Task<PropertyWelcomeLink?> GetActiveAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        return await db.PropertyWelcomeLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OrgId == orgId && l.PropertyId == propertyId && l.RevokedAt == null, cancellationToken);
    }

    public async Task<PropertyWelcomeLinkResult> CreateOrGetAsync(
        Guid orgId,
        Guid propertyId,
        string? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsurePropertyAsync(orgId, propertyId, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.PropertyWelcomeLinks
                .FirstOrDefaultAsync(l => l.OrgId == orgId && l.PropertyId == propertyId && l.RevokedAt == null, cancellationToken);
            if (existing is not null)
                return new PropertyWelcomeLinkResult(existing, false);

            var link = NewLink(orgId, propertyId, createdByUserId);
            db.PropertyWelcomeLinks.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Welcome link {LinkId} made for property {PropertyId} of org {OrgId}", link.Id, propertyId, orgId);
                return new PropertyWelcomeLinkResult(link, true);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && CrmDatabaseErrors.IsUniqueViolation(ex))
            {
                // Another request made the active link of the property at the same moment: look again, and take theirs.
                Forget();
                logger.LogInformation("The welcome link of property {PropertyId} was made by another request at the same moment", propertyId);
            }
        }
    }

    public async Task<PropertyWelcomeLink> RotateAsync(
        Guid orgId,
        Guid propertyId,
        string? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsurePropertyAsync(orgId, propertyId, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);

            // The old link is revoked, and saved, before the new one is added: the unique index lets one active link of the property in.
            var active = await db.PropertyWelcomeLinks
                .Where(l => l.OrgId == orgId && l.PropertyId == propertyId && l.RevokedAt == null)
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var old in active)
                old.RevokedAt = now;

            if (active.Count > 0)
                await db.SaveChangesAsync(cancellationToken);

            var link = NewLink(orgId, propertyId, createdByUserId);
            db.PropertyWelcomeLinks.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Welcome link of property {PropertyId} of org {OrgId} rotated: {Revoked} revoked, {LinkId} is the active one",
                    propertyId,
                    orgId,
                    active.Count,
                    link.Id);
                return link;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && CrmDatabaseErrors.IsUniqueViolation(ex))
            {
                // Another rotation won at the same moment: this attempt is undone (the transaction ends without a commit) and made again
                // from what is stored.
                Forget();
                logger.LogInformation("The welcome link of property {PropertyId} was rotated by another request at the same moment", propertyId);
            }
        }
    }

    /// <summary>The property must be one of the org (a deleted one is not): the link copies its org (TN-2).</summary>
    private async Task EnsurePropertyAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken))
        {
            throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
        }
    }

    private PropertyWelcomeLink NewLink(Guid orgId, Guid propertyId, string? createdByUserId)
    {
        var createdBy = createdByUserId?.Trim();
        return new PropertyWelcomeLink
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(PropertyWelcomeLink.TokenBytes)),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = string.IsNullOrEmpty(createdBy)
                ? null
                : createdBy.Length <= PropertyWelcomeLink.CreatedByUserIdMaxLength
                    ? createdBy
                    : createdBy[..PropertyWelcomeLink.CreatedByUserIdMaxLength],
        };
    }

    /// <summary>Lets go of the links of the unit of work after a refused save, so that the next attempt reads what is stored.</summary>
    private void Forget()
    {
        foreach (var entry in db.ChangeTracker.Entries<PropertyWelcomeLink>().ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>A transaction on a relational database, unless the caller already opened one; none on the in-memory store of the tests.</summary>
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }
}
