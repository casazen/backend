using System.ComponentModel.DataAnnotations;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed class PropertyActivationCredential : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    [MaxLength(25)]
    public required string Service { get; set; }
    public required string Username { get; set; }
    public required string Password { get; set; }
    public string? WebServiceKey { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class PropertyActivationCredentialAssignment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid AccountId { get; set; }
    [MaxLength(25)]
    public required string Service { get; set; }
    public string? UnitCode { get; set; }
}
