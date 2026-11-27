using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The state of the content of a property on one channel of the channel manager (NF-27, NF-38, ADR-034): one row for each property and
/// channel, written by the publication and by the reading of its status. A property with no row for a channel was never published to it.
/// </summary>
/// <remarks>
/// <para>The channel is a text, not an enum: <see cref="ChannelCode"/> is the code of the portal in the channel manager (<c>booking_xml</c>,
/// <c>airbnb_xml</c>, <c>expedia</c>...), so a new channel needs no release.</para>
/// <para>How complete the content is for a channel is worked out when the state is read; it is not stored.</para>
/// <para>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2).</para>
/// </remarks>
[Table("ListingChannelStates")]
public class ListingChannelState : ITenantOwned
{
    public const int ChannelCodeMaxLength = 50;

    public const int ErrorCodeMaxLength = 100;

    public const int ErrorMessageMaxLength = 1000;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    [Required, MaxLength(ChannelCodeMaxLength)]
    public string ChannelCode { get; set; } = string.Empty;

    public ChannelPublishStatus Status { get; set; } = ChannelPublishStatus.Draft;

    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    [MaxLength(ErrorMessageMaxLength)]
    public string? ErrorMessage { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public int ContentVersion { get; set; }

    [Column(TypeName = "jsonb")]
    public string? ContentHashesJson { get; set; }

    public DateTime? ExternalChangeAt { get; set; }
}
