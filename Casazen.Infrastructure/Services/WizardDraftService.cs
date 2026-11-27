using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IWizardDraftService"/>
/// <remarks>
/// <para><b>Encryption is the repository's one mechanism.</b> The personal half of the data is written in clear to
/// <see cref="WizardDraft.EncryptedFieldsJson"/>: that column is declared in <c>EncryptedColumns</c> (purpose
/// <c>Casazen.WizardDraft.PII</c>), so EF encrypts it on write and decrypts it on read. This service never protects it a second time.</para>
/// <para><b>No lost update.</b> A save loads the row, compares <see cref="WizardDraft.Version"/> with the client's, increments it and
/// saves. Two parallel saves that read the same version both pass the comparison, so the row also carries PostgreSQL's <c>xmin</c>
/// (<see cref="WizardDraft.RowVersion"/>): the second update finds the row changed and fails with a
/// <see cref="DbUpdateConcurrencyException"/>, which is answered as a conflict with the draft that won. The first save of a draft that does
/// not exist yet relies on the unique indexes in the same way: the loser's insert is a unique violation, answered the same.</para>
/// <para><b>Expired drafts.</b> A draft past <see cref="WizardDraft.ExpiresAt"/> is gone for the person even if the nightly retention has
/// not anonymized it yet: a read does not show it and a save rewrites it, never as a conflict. The version keeps counting.</para>
/// <para>Nothing about the content of a draft is ever logged.</para>
/// </remarks>
public sealed class WizardDraftService(AppDbContext db, TimeProvider timeProvider) : IWizardDraftService
{
    // A save that lost a race against a row that has vanished since (anonymized, discarded) writes again once; a delete tries a few times
    // against the saves of the same person.
    private const int MaxSaveAttempts = 2;
    private const int MaxDeleteAttempts = 3;

    public async Task<WizardDraftResult> SaveDraftAsync(Guid orgId, string userId, SaveDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireUser(userId);
        if (!Enum.IsDefined(request.Type))
            throw new DomainRuleException(WizardDraftErrors.TypeUnknown, "WizardDraftTypeUnknown");

        if (request.Data.RootElement.ValueKind != JsonValueKind.Object || WizardDraftPii.HasNullCharacter(request.Data.RootElement))
            throw new DomainRuleException(WizardDraftErrors.DataInvalid, "WizardDraftDataInvalid");

        var parts = WizardDraftPii.Split(request.Data.RootElement);
        var bytes = Encoding.UTF8.GetByteCount(parts.PlainJson)
            + (parts.PersonalJson is null ? 0 : Encoding.UTF8.GetByteCount(parts.PersonalJson));
        if (bytes > WizardDraftLimits.MaxDataBytes)
            throw new DomainPayloadTooLargeException(WizardDraftErrors.TooLarge, "WizardDraftTooLarge", WizardDraftLimits.MaxDataBytes / 1024);

        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var existing = await db.WizardDrafts
                .Where(Key(orgId, userId, request.Type, request.EntityId))
                .OrderByDescending(d => d.Version)
                .FirstOrDefaultAsync(ct);

            // The client is behind the server: nothing is written, the client chooses between its copy and the server's.
            if (existing is not null && existing.ExpiresAt > now && existing.Version > request.ClientVersion)
                return new WizardDraftResult(DraftStatus.Conflict, existing.Version, ToDto(existing));

            if (existing is null)
            {
                existing = new WizardDraft
                {
                    OrgId = orgId,
                    UserId = userId,
                    WizardType = request.Type,
                    EntityId = request.EntityId,
                };
                db.WizardDrafts.Add(existing);
            }

            existing.Version++;
            existing.DraftJson = parts.PlainJson;
            existing.EncryptedFieldsJson = parts.PersonalJson;
            existing.LastSavedAt = now;
            existing.ExpiresAt = now.AddDays(WizardDraftLimits.RetentionDays);

            try
            {
                await db.SaveChangesAsync(ct);
                return new WizardDraftResult(DraftStatus.Saved, existing.Version, ToDto(existing));
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex))
            {
                // A parallel save of the same person won. Nothing of ours was written: the client is shown what is there now.
                db.ChangeTracker.Clear();
                var current = await FindLiveAsync(orgId, userId, request.Type, request.EntityId, ct);
                if (current is not null)
                    return new WizardDraftResult(DraftStatus.Conflict, current.Version, ToDto(current));

                // What it raced against has gone since (discarded, anonymized): write again, once.
                if (attempt >= MaxSaveAttempts)
                    throw new DomainConflictException(WizardDraftErrors.Conflict, "WizardDraftConflict");
            }
        }
    }

    public async Task<WizardDraftDto?> GetDraftAsync(Guid orgId, string userId, WizardType type, Guid? entityId, CancellationToken ct)
    {
        RequireUser(userId);

        var draft = await FindLiveAsync(orgId, userId, type, entityId, ct);
        return draft is null ? null : ToDto(draft);
    }

    public async Task DeleteDraftAsync(Guid orgId, string userId, WizardType type, Guid? entityId, CancellationToken ct)
    {
        RequireUser(userId);

        for (var attempt = 1; ; attempt++)
        {
            var draft = await db.WizardDrafts
                .Where(Key(orgId, userId, type, entityId))
                .OrderByDescending(d => d.Version)
                .FirstOrDefaultAsync(ct);
            if (draft is null)
                return;

            draft.Anonymize();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxDeleteAttempts)
            {
                // The same person saved the draft at the same moment: read it again and discard that one.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>The draft of the person that has not expired, as the database holds it (not tracked).</summary>
    private Task<WizardDraft?> FindLiveAsync(Guid orgId, string userId, WizardType type, Guid? entityId, CancellationToken ct)
    {
        var now = Now();
        return db.WizardDrafts
            .AsNoTracking()
            .Where(Key(orgId, userId, type, entityId))
            .Where(d => d.ExpiresAt > now)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The one draft of this person, in this org, for this wizard and entity. The org and the user are the ones the web layer resolved,
    /// never a value of the request body; the tenant query filter says the same about the org.
    /// </summary>
    private static Expression<Func<WizardDraft, bool>> Key(Guid orgId, string userId, WizardType type, Guid? entityId) =>
        d => d.OrgId == orgId && d.UserId == userId && d.WizardType == type && d.EntityId == entityId;

    private static WizardDraftDto ToDto(WizardDraft draft) =>
        new(
            draft.WizardType,
            draft.EntityId,
            draft.Version,
            WizardDraftPii.Merge(draft.DraftJson, draft.EncryptedFieldsJson),
            draft.LastSavedAt);

    /// <summary>
    /// A caller with no user id would match the anonymized rows (they have the empty one): the web layer never lets it through, and
    /// this service refuses it as well.
    /// </summary>
    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A wizard draft belongs to a person: the user id is required.", nameof(userId));
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Now, cut to the microsecond PostgreSQL keeps, so what is written and what is read back compare equal.</summary>
    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);
}
