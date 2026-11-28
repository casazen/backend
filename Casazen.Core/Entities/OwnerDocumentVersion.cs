using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One file of an <see cref="OwnerDocument"/> (E07/F07/US01/T02, AC-NF111-3): immutable once saved (<see cref="IAppendOnlyRecord"/>), so a new file is a new
/// version and the history is never rewritten. Only a file that passed the scan of <c>IOwnerFileScanner</c> is stored (<see cref="ScanEngine"/> says which
/// engine judged it); it lives in the private bucket and is read only through the endpoints that check the owner, the agency and the permission.
/// </summary>
[Table("OwnerDocumentVersions")]
public class OwnerDocumentVersion : ITenantOwned, IAppendOnlyRecord
{
    public const int StorageKeyMaxLength = 300;
    public const int FileNameMaxLength = 200;
    public const int ContentTypeMaxLength = 100;
    public const int HashLength = 64;
    public const int EngineMaxLength = 64;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid OwnerDocumentId { get; set; }

    public int VersionNumber { get; set; }

    [Required]
    [MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(FileNameMaxLength)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(ContentTypeMaxLength)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required]
    [MaxLength(HashLength)]
    public string Sha256 { get; set; } = string.Empty;

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    [Required]
    [MaxLength(EngineMaxLength)]
    public string ScanEngine { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UploadedByUserId { get; set; } = string.Empty;
}
