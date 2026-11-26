using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Entities;

/// <summary>A PDF supplied by the seller for an accepted guest order. The original is kept in private storage.</summary>
public sealed class GuestSupplierOrderDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    [MaxLength(64)] public required string ContentHash { get; set; }
    [MaxLength(180)] public required string FileName { get; set; }
    public DateTime UploadedAt { get; set; }
}
