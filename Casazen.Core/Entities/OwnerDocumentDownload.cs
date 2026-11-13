using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One download of a version of an <see cref="OwnerDocument"/> (E07/F07/US01/T02, AC-NF111-3: the download audit): who and when, written before the file is
/// handed out, never changed or deleted (<see cref="IAppendOnlyRecord"/>). Exactly one of <see cref="OwnerUserId"/> (the owner or a contact it delegated) and
/// <see cref="UserId"/> (a person of the agency) is set.
/// </summary>
[Table("OwnerDocumentDownloads")]
public class OwnerDocumentDownload : ITenantOwned, IAppendOnlyRecord
{
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid OwnerDocumentId { get; set; }

    public Guid OwnerDocumentVersionId { get; set; }

    public DateTime DownloadedAt { get; set; }

    /// <summary>The person of the owner portal who downloaded it (<c>OwnerUser.Id</c>).</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>The person of the agency who downloaded it (<c>User.Id</c>).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UserId { get; set; }
}
