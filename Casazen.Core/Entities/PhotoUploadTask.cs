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

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    [Required, MaxLength(PhotoUrlMaxLength)]
    public string PhotoUrl { get; set; } = string.Empty;

    [MaxLength(TargetRoomTypeMaxLength)]
    public string? TargetRoomType { get; set; }

    public PhotoUploadStatus Status { get; set; } = PhotoUploadStatus.Pending;

    [MaxLength(ExternalIdMaxLength)]
    public string? OctorateQueueId { get; set; }

    [MaxLength(ExternalIdMaxLength)]
    public string? OctoratePhotoId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    /// <summary>Why the channel manager could not take the photo; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorMessageMaxLength)]
    public string? ErrorMessage { get; set; }
}
