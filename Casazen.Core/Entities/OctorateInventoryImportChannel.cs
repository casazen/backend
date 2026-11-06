using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One OTA channel of an import batch (D6, E02/F01/US03): the access to it in the Octorate panel and the steps that follow, with the stable
/// state and error of each. Resume is decided by the stamps (<see cref="AccessCompletedAt"/>, <see cref="RoomsImportedAt"/>) and by those
/// of its <see cref="Items"/>, never by memory: <see cref="Status"/> is the summary of them.
/// </summary>
/// <remarks>
/// <para>Unique per batch and channel code (<see cref="BatchChannelIndexName"/>). The code is the portal code of the channel manager's
/// catalog (<c>MarketplacePortal.PortalCode</c>); no name of a channel lives in the code. The access to the channel is the
/// <c>PanelStepKind.ChannelAccess</c> step of the org for that code (<see cref="PanelStepId"/>, a reference and not a foreign key), whose
/// callback, status and verification result this row consumes.</para>
/// <para>Ids, codes and stamps only: no credential, token, access address or payload of Octorate.</para>
/// </remarks>
[Table("OctorateInventoryImportChannels")]
public class OctorateInventoryImportChannel : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="BatchId"/>, <see cref="ChannelCode"/>).</summary>
    public const string BatchChannelIndexName = "UIX_OctorateInventoryImportChannels_BatchId_ChannelCode";

    /// <summary>Longest <see cref="ChannelCode"/>: the length of the code of a panel step (<c>OnboardingPanelStep.ChannelCodeMaxLength</c>).</summary>
    public const int ChannelCodeMaxLength = 40;

    /// <summary>Longest <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 60;

    /// <summary>Longest <see cref="AccessConfirmedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The batch the channel belongs to (FK, CASCADE).</summary>
    public Guid BatchId { get; set; }

    /// <summary>The portal code of the channel, as the catalog of the channel manager gives it.</summary>
    [Required]
    [MaxLength(ChannelCodeMaxLength)]
    public string ChannelCode { get; set; } = string.Empty;

    public OctorateInventoryImportStatus Status { get; set; } = OctorateInventoryImportStatus.Draft;

    /// <summary>The stable code of why the channel needs attention or failed (see <see cref="OctorateInventoryErrorCodes"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public ChannelAccessState AccessState { get; set; } = ChannelAccessState.NotStarted;

    /// <summary>The panel step of the access (<c>OnboardingPanelStep.Id</c>); a reference, not a foreign key.</summary>
    public Guid? PanelStepId { get; set; }

    /// <summary>When the person was handed over to the Octorate panel for this channel (UTC).</summary>
    public DateTime? AccessStartedAt { get; set; }

    /// <summary>When the access was seen given, or confirmed by the person after it could not be checked (UTC).</summary>
    public DateTime? AccessCompletedAt { get; set; }

    /// <summary>Who confirmed the access, when CasaZen could not check it (the Auth0 subject); <c>null</c> when it was verified.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? AccessConfirmedByUserId { get; set; }

    /// <summary>When the rooms of every connection of the channel were imported and the listings read again, so the mapping can start (UTC).</summary>
    public DateTime? RoomsImportedAt { get; set; }

    /// <summary>When every listing of the channel was connected, left for later or rejected (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The connections of the channel found after the access, one each.</summary>
    public List<OctorateInventoryImportItem> Items { get; set; } = [];
}
