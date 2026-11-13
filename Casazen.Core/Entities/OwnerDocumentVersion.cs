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

    /// <summary>1 for the first file, then one more for each new file of the document.</summary>
    public int VersionNumber { get; set; }

    /// <summary>The key of the file in the private bucket (<c>StorageKeys.OwnerDocumentFile</c>): made by the server, never the name sent.</summary>
    [Required]
    [MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>The name of the file as uploaded, without any folder: what the download offers.</summary>
    [Required]
    [MaxLength(FileNameMaxLength)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>The type the scan recognised from the content (<c>application/pdf</c>, <c>image/jpeg</c>, <c>image/png</c>), never the one declared.</summary>
    [Required]
    [MaxLength(ContentTypeMaxLength)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file (lower-case hex).</summary>
    [Required]
    [MaxLength(HashLength)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The first day the document is in force; <c>null</c> when it says nothing.</summary>
    public DateOnly? EffectiveFrom { get; set; }

    /// <summary>The last day the document is in force (an insurance, a mandate): the agency is told before it lapses. <c>null</c> for no expiry.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>The engine that judged the file clean (<c>IOwnerFileScanner.Engine</c>).</summary>
    [Required]
    [MaxLength(EngineMaxLength)]
    public string ScanEngine { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UploadedByUserId { get; set; } = string.Empty;
}
