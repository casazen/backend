using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("PropertyICalFeeds")]
public class PropertyICalFeed
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    [MaxLength(2048)]
    public string? ImportUrl { get; set; }

    public Guid ExportToken { get; set; } = Guid.NewGuid();

    public DateTime? LastImportAt { get; set; }

    public PropertyICalImportStatus? LastImportStatus { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}
