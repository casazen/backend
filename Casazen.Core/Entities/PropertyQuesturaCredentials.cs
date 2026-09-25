using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Credentials of the Alloggiati Web (Questura) account of a property: username, password and web service key (WSKey)
/// the transmission needs (CO-13). Write-only for clients (CO-14, A5-30): the API sets or replaces them and answers only
/// whether they are configured and since when, never a value. The three values are encrypted at rest by the EF value
/// converter of <c>AppDbContext</c> (Data Protection, <c>docs/runbooks/encryption.md</c>); the columns are
/// <c>text</c> because the payload is longer than the value.
/// </summary>
[Table("PropertyQuesturaCredentials")]
public class PropertyQuesturaCredentials : ITenantOwned
{
    /// <summary>Longest username accepted (plain text).</summary>
    public const int MaxUsernameLength = 100;

    /// <summary>Longest password accepted (plain text).</summary>
    public const int MaxPasswordLength = 200;

    /// <summary>Longest web service key accepted (plain text).</summary>
    public const int MaxWsKeyLength = 200;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Alloggiati Web username, encrypted at rest.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>Alloggiati Web password, encrypted at rest.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Alloggiati Web service key (WSKey), encrypted at rest.</summary>
    [Required]
    public string WsKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the credentials were set or replaced: the "configured on" date shown to the host.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
