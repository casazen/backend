using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The progress of one wizard, saved on the server so that the person resumes it on any device and never loses what it typed
/// (ADR-024, NF-03, NF-06). One row per person, wizard and entity: the draft of a wizard that creates something has no
/// <see cref="EntityId"/>, the draft of a wizard that edits something carries the id of what it edits.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Two halves.</b> The fields that name a person (<c>WizardDraftPiiFields</c>) are split off the typed data and kept in
/// <see cref="EncryptedFieldsJson"/>, encrypted at rest with the purpose <c>Casazen.WizardDraft.PII</c> (<c>EncryptedColumns</c>):
/// the code reads and writes that column in clear and EF does the rest. <see cref="DraftJson"/> never holds one of them.</item>
/// <item><b>Version.</b> <see cref="Version"/> is the counter the client sees: a save carries the one the client read, and a save
/// whose version is behind the server's is a conflict, never a lost update. <see cref="RowVersion"/> is another thing: PostgreSQL's
/// <c>xmin</c>, which makes two parallel saves of the same version unable to both win.</item>
/// <item><b>Retention.</b> A draft lives 30 days from <see cref="LastSavedAt"/> (<see cref="ExpiresAt"/>). The nightly
/// <c>gdpr-data-retention</c> job then anonymizes it (<see cref="Anonymize"/>) and never deletes the row, so the audit trail stays.
/// A draft the person discards is anonymized in the same way.</item>
/// <item><b>Tenant and person.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org the person worked in. A draft is
/// read, saved and discarded only by the person it belongs to, in that org: the draft of somebody else is a 404, never a 403.</item>
/// </list>
/// </remarks>
[Table("WizardDrafts")]
public class WizardDraft : ITenantOwned
{
    public const string NewEntityIndexName = "UIX_WizardDrafts_OrgId_UserId_WizardType";

    public const string ExistingEntityIndexName = "UIX_WizardDrafts_OrgId_UserId_WizardType_EntityId";

    public const int UserIdMaxLength = 255;

    public const string EmptyJson = "{}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>
    /// The person the draft belongs to (<c>User.Id</c>, the Auth0 subject: a string in this code base, not a Guid). Not a foreign key:
    /// the retention overwrites it with the empty string when it anonymizes the row, and a row with the empty string is left out of
    /// both unique indexes.
    /// </summary>
    [Required, MaxLength(UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    public WizardType WizardType { get; set; }

    public Guid? EntityId { get; set; }

    public int Version { get; set; }

    [Required, Column(TypeName = "jsonb")]
    public string DraftJson { get; set; } = EmptyJson;

    /// <summary>
    /// The personal fields of the typed data, as JSON, <c>null</c> when there are none. Encrypted at rest by EF (purpose
    /// <c>Casazen.WizardDraft.PII</c>): the code writes and reads it in clear and never protects it a second time.
    /// </summary>
    public string? EncryptedFieldsJson { get; set; }

    public DateTime LastSavedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no
    /// column of its own, as <c>SupplierServiceListing.Version</c>). It is not <see cref="Version"/>.
    /// </summary>
    public uint RowVersion { get; set; }

    public void Anonymize()
    {
        DraftJson = EmptyJson;
        EncryptedFieldsJson = null;
        UserId = string.Empty;
    }
}

public static class WizardDraftLimits
{
    public const int RetentionDays = 30;

    public const int MaxDataBytes = 256 * 1024;
}
