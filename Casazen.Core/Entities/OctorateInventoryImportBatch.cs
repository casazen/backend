using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One import of existing OTA inventory, the <c>onOtas</c> scenario of the connect wizard (D6, E02/F01/US03): the channels the person chose
/// and the properties it is about, with where it stands. It follows channel access, room import, mapping, reservations, calendar and
/// «mark connected» durably, so a reload, a second device or a restart of the worker finds the exact step.
/// </summary>
/// <remarks>
/// <para><b>Ids and codes only.</b> Nothing here is a credential, a token, an access address or a payload of Octorate: the batch holds ids
/// of CasaZen, the codes of the channels, the states and stable error codes. The access to a channel is given in the Octorate panel and
/// never reaches CasaZen.</para>
/// <para><b>Never a creation.</b> No state of a batch, no error and no retry calls an operation that creates or publishes a listing on
/// Octorate or on an OTA, and none falls back to the scenario <see cref="OctorateInventoryScenario.NewInventory"/>.</para>
/// <para><see cref="Status"/> is the summary of the channels (the earliest step one still has to do, <c>NeedsAttention</c> when a person
/// has to act, <c>Completed</c> when all are); the channels are the truth.</para>
/// </remarks>
[Table("OctorateInventoryImportBatches")]
public class OctorateInventoryImportBatch : ITenantOwned
{
    /// <summary>Longest <see cref="ScopeGroup"/>.</summary>
    public const int GroupMaxLength = 100;

    /// <summary>Longest <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 60;

    /// <summary>Longest <see cref="CreatedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>An empty JSON array, the value of <see cref="ScopePropertyIdsJson"/> when the scope names no property.</summary>
    public const string EmptyJsonArray = "[]";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public OctorateInventoryScopeKind ScopeKind { get; set; } = OctorateInventoryScopeKind.AllInventory;

    /// <summary>The named group of the mixed portfolio, for a batch of kind <see cref="OctorateInventoryScopeKind.Group"/>; otherwise <c>null</c>.</summary>
    [MaxLength(GroupMaxLength)]
    public string? ScopeGroup { get; set; }

    /// <summary>The properties the batch is about, as a JSON array of ids (jsonb); empty for the whole inventory.</summary>
    [Required]
    public string ScopePropertyIdsJson { get; set; } = EmptyJsonArray;

    public OctorateInventoryImportStatus Status { get; set; } = OctorateInventoryImportStatus.Draft;

    /// <summary>The stable code of why the batch needs attention or failed; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>Who started it: the Auth0 subject.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When every channel was completed (UTC); <c>null</c> until then.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>The channels of the batch, one each.</summary>
    public List<OctorateInventoryImportChannel> Channels { get; set; } = [];
}
