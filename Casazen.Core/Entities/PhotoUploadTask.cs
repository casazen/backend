using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The sending of one photo of a property to the channel manager (NF-32, ADR-036): the channel manager downloads the photo from its public
/// address and answers with an entry of its photo queue. A photo is its address (there is no photo id in CasaZen), so a property has at most one
/// task for an address, and a photo whose task is <see cref="PhotoUploadStatus.Done"/> is never sent again.
/// </summary>
/// <remarks>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2).</remarks>
[Table("PhotoUploadTasks")]
public class PhotoUploadTask : ITenantOwned
{
    public const int PhotoUrlMaxLength = 1000;

    public const int TargetRoomTypeMaxLength = 50;

    public const int ExternalIdMaxLength = 200;

    public const int ErrorMessageMaxLength = 1000;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The address of the photo, as the gallery of the property lists it; unique together with <see cref="PropertyId"/>.</summary>
    [Required, MaxLength(PhotoUrlMaxLength)]
    public string PhotoUrl { get; set; } = string.Empty;

    /// <summary>The type of room of the channel manager the photo is meant for; <c>null</c> = the property as a whole.</summary>
    [MaxLength(TargetRoomTypeMaxLength)]
    public string? TargetRoomType { get; set; }

    public PhotoUploadStatus Status { get; set; } = PhotoUploadStatus.Pending;

    /// <summary>The entry of the photo queue of the channel manager the sending is waiting on; <c>null</c> = not sent.</summary>
    [MaxLength(ExternalIdMaxLength)]
    public string? OctorateQueueId { get; set; }

    /// <summary>The name the channel manager gave the photo once it has it, kept so the photo is not sent again; <c>null</c> = not yet.</summary>
    [MaxLength(ExternalIdMaxLength)]
    public string? OctoratePhotoId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the sending ended (done, removed or in error); <c>null</c> while it goes on.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Why the channel manager could not take the photo; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorMessageMaxLength)]
    public string? ErrorMessage { get; set; }
}
