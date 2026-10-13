using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The public iCal export link of a property (<c>/api/public/ical/{ExportToken}</c>), one per property. It lived on
/// the single import feed until PC-11 made the import feeds many per property; the existing tokens were moved here
/// unchanged, so the links already pasted on the OTAs keep working.
/// </summary>
[Table("PropertyICalExports")]
public class PropertyICalExport : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>
    /// Secret of the public link, created with <see cref="NewExportToken"/>. Replaced by the host with
    /// <c>POST /api/properties/{id}/ical/export-url/regenerate</c> (PC-12): the old link then answers 404. Stored as it
    /// is, not hashed: the host can copy the link again at any time, and the feed only publishes busy dates the
    /// database already holds (see docs/runbooks/ical.md "Export link token").
    /// </summary>
    public Guid ExportToken { get; set; } = NewExportToken();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;

    /// <summary>
    /// A new link token that cannot be guessed: 128 bits from the cryptographically secure generator
    /// (<see cref="RandomNumberGenerator"/>; <see cref="Guid.NewGuid"/> is not meant for secrets). It keeps the UUID
    /// shape of the links already pasted on the OTAs (route <c>{exportToken:guid}</c>, column <c>uuid</c>).
    /// </summary>
    public static Guid NewExportToken() => new(RandomNumberGenerator.GetBytes(16));
}
