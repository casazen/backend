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

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The code of the portal in the channel manager, e.g. <c>expedia</c>; unique together with <see cref="PropertyId"/>.</summary>
    [Required, MaxLength(ChannelCodeMaxLength)]
    public string ChannelCode { get; set; } = string.Empty;

    public ChannelPublishStatus Status { get; set; } = ChannelPublishStatus.Draft;

    /// <summary>A stable code of what went wrong on the channel; <c>null</c> while nothing did.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>What went wrong, in simple words, for the person who has to fix it; <c>null</c> while nothing did.</summary>
    [MaxLength(ErrorMessageMaxLength)]
    public string? ErrorMessage { get; set; }

    /// <summary>UTC instant of the last push accepted or of the last status read; <c>null</c> = never.</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>How many times the content was accepted by the channel manager.</summary>
    public int ContentVersion { get; set; }

    /// <summary>
    /// A JSON object from a group of the content (<c>descriptions</c>, <c>rooms</c>...) to the SHA-256 of what the channel manager returned
    /// right after the last accepted push: the baseline to tell that someone changed the content outside CasaZen. <c>null</c> = no push yet.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ContentHashesJson { get; set; }

    /// <summary>UTC instant a change made outside CasaZen was found; <c>null</c> = none, cleared by the person's choice.</summary>
    public DateTime? ExternalChangeAt { get; set; }
}
