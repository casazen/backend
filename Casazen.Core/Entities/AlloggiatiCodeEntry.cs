using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One code of an official Alloggiati Web table (comuni, stati, tipi documento, tipi alloggiato). Platform reference
/// data, the same for every org: loaded only from the official portal file (startup seed, scheduled download or admin
/// upload; <see cref="AlloggiatiCodeTableImport"/>), never written by hand (RS-1, CO-12).
/// </summary>
[Table("AlloggiatiCodeEntries")]
[Index(nameof(Table), nameof(Code), IsUnique = true)]
[Index(nameof(Table), nameof(NormalizedDescription))]
public class AlloggiatiCodeEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public AlloggiatiCodeTable Table { get; set; }

    [Required, MaxLength(9)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string NormalizedDescription { get; set; } = string.Empty;

    [MaxLength(2)]
    public string? Province { get; set; }

    public Guid ImportId { get; set; }
    public virtual AlloggiatiCodeTableImport Import { get; set; } = null!;
}

/// <summary>
/// One import of an official Alloggiati table: which file, which version and when. The last import of a table replaces
/// all its codes.
/// </summary>
[Table("AlloggiatiCodeTableImports")]
[Index(nameof(Table), nameof(ImportedAt))]
public class AlloggiatiCodeTableImport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public AlloggiatiCodeTable Table { get; set; }

    [Required, MaxLength(255)]
    public string SourceFileName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string SourceVersion { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [MaxLength(200)]
    public string? Authority { get; set; }

    public int RowCount { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(200)]
    public string ImportedBy { get; set; } = string.Empty;
}

/// <summary>
/// Official Alloggiati Web code tables (area download "Tabelle" of the portal, RS-1). Stored as an integer; never
/// renumber. Places of issue use the comuni table (issued in Italy) or the stati table (issued abroad).
/// </summary>
public enum AlloggiatiCodeTable
{
    Comuni = 1,

    Stati = 2,

    Documenti = 3,

    TipiAlloggiato = 4,
}
