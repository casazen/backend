using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The regional ISTAT credentials of a property, as the host enters them (E20/F03/US01/T01): the user name and the password of the regional
/// portal of the tourist statistics and the code of the structure (CIR, CUSR or the code the region gives). <b>Write-only</b> like
/// <see cref="PropertyQuesturaCredentials"/>: the API sets, replaces or removes them and answers only whether they are configured and since when.
/// The one reader is the controlled opening of the support vault, on an open «Do it for me» request, with a reason and an audit line.
/// </summary>
/// <remarks>
/// The three values are encrypted at rest by the Data Protection converter with the purpose <c>Casazen.PropertyIstatCredentials</c>
/// (<c>EncryptedColumns</c>); the columns are <c>text</c> because the payload is longer than the value. One row per property (unique index on
/// <see cref="PropertyId"/>). The row follows its property and its org (cascade): erasing either erases the credentials.
/// </remarks>
[Table("PropertyIstatCredentials")]
public class PropertyIstatCredentials : ITenantOwned
{
    /// <summary>Longest user name accepted (plain text).</summary>
    public const int MaxUsernameLength = 100;

    /// <summary>Longest password accepted (plain text).</summary>
    public const int MaxPasswordLength = 200;

    /// <summary>Longest code of the structure accepted (plain text).</summary>
    public const int MaxStructureCodeLength = 50;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The property (FK, CASCADE, unique).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>User name of the regional portal, encrypted at rest.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>Password of the regional portal, encrypted at rest.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>The code of the structure in the regional system (CIR, CUSR...), encrypted at rest; <c>null</c> when the portal asks for none.</summary>
    public string? StructureCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the credentials were set or replaced: the "configured on" date shown to the host.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
