using System.ComponentModel.DataAnnotations;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed class PropertyImportReview : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    [MaxLength(80)]
    public required string Provider { get; set; }
    [MaxLength(240)]
    public required string SourceId { get; set; }
    public required string Snapshot { get; set; }
    public required string ResolvedFields { get; set; }
    public Guid? TodoId { get; set; }
    public DateTime CheckedAt { get; set; }
}
