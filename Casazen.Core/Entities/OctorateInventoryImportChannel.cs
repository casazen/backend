using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One OTA channel of an import batch (D6, E02/F01/US03): the access to it (in the Octorate panel, or through «Collega da CasaZen» for a portal
/// that needs only the id of the property on it) and the steps that follow, with the stable state and error of each. Resume is decided by the
/// stamps (<see cref="AccessCompletedAt"/>, <see cref="RoomsImportedAt"/>) and by those of its <see cref="Items"/>, never by memory:
/// <see cref="Status"/> is the summary of them.
/// </summary>
/// <remarks>
/// <para>Unique per batch and channel code (<see cref="BatchChannelIndexName"/>). The code is the portal code of the channel manager's
/// catalog (<c>MarketplacePortal.PortalCode</c>); no name of a channel lives in the code. The access to the channel is the
/// <c>PanelStepKind.ChannelAccess</c> step of the org for that code (<see cref="PanelStepId"/>, a reference and not a foreign key), whose
/// callback, status and verification result this row consumes.</para>
/// <para>Ids, codes and stamps only: no credential, token, access address or payload of Octorate. The id of the property on the portal that
/// the host typed for «Collega» goes to Octorate in the create of the connection and is not kept here.</para>
/// </remarks>
[Table("OctorateInventoryImportChannels")]
public class OctorateInventoryImportChannel : ITenantOwned
{
    public const string BatchChannelIndexName = "UIX_OctorateInventoryImportChannels_BatchId_ChannelCode";

    public const int ChannelCodeMaxLength = 40;

    public const int ErrorCodeMaxLength = 60;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BatchId { get; set; }

    [Required]
    [MaxLength(ChannelCodeMaxLength)]
    public string ChannelCode { get; set; } = string.Empty;

    public OctorateInventoryImportStatus Status { get; set; } = OctorateInventoryImportStatus.Draft;

    /// <summary>The stable code of why the channel needs attention or failed (see <see cref="OctorateInventoryErrorCodes"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public ChannelAccessState AccessState { get; set; } = ChannelAccessState.NotStarted;

    public Guid? PanelStepId { get; set; }

    public DateTime? AccessStartedAt { get; set; }

    public DateTime? AccessCompletedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? AccessConfirmedByUserId { get; set; }

    /// <summary>
    /// The Octorate accommodations «Collega da CasaZen» connected to the channel for this batch, or found already connected to it, as a JSON array
    /// of ids (the style of <c>OctorateInventoryImportBatch.ScopePropertyIdsJson</c>); <c>null</c> while the host never used it. Written after each
    /// connection, so a later failure never forgets an earlier one; a connection of one of them that Octorate shows as <c>ENABLED</c> proves the
    /// access of the channel for the batch.
    /// </summary>
    public string? ConnectedAccommodationIdsJson { get; set; }

    public DateTime? RoomsImportedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<OctorateInventoryImportItem> Items { get; set; } = [];
}
