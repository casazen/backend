using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One code of an official Alloggiati Web table (comuni, stati, tipi documento, tipi alloggiato). Platform reference
/// data, the same for every org: it is loaded only by an admin import of the file downloaded from the Alloggiati portal
/// (<see cref="AlloggiatiCodeTableImport"/>), never written by hand nor committed in the repository (RS-1, CO-12).
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

    /// <summary>Code as written in the official file (uppercase, no spaces).</summary>
    [Required, MaxLength(9)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Description as written in the official file.</summary>
    [Required, MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Description normalized for matching and search: uppercase, no accents, letters and digits only.</summary>
    [Required, MaxLength(200)]
    public string NormalizedDescription { get; set; } = string.Empty;

    /// <summary>Province (two-letter car plate code) of a comune; null for the other tables.</summary>
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

    /// <summary>Name of the uploaded file.</summary>
    [Required, MaxLength(255)]
    public string SourceFileName { get; set; } = string.Empty;

    /// <summary>Version of the official table given by the admin (e.g. the download date shown on the portal).</summary>
    [Required, MaxLength(100)]
    public string SourceVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the uploaded file.</summary>
    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public int RowCount { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Auth0 id of the admin who imported the file.</summary>
    [Required, MaxLength(200)]
    public string ImportedBy { get; set; } = string.Empty;
}

/// <summary>
/// Official Alloggiati Web code tables (area download "Tabelle" of the portal, RS-1). Stored as an integer; never
/// renumber. Places of issue use the comuni table (issued in Italy) or the stati table (issued abroad).
/// </summary>
public enum AlloggiatiCodeTable
{
    /// <summary>Comuni (birth place and place of issue in Italy), 9-character codes.</summary>
    Comuni = 1,

    /// <summary>Stati (state of birth, citizenship, place of issue abroad), 9-character codes.</summary>
    Stati = 2,

    /// <summary>Tipi documento, 5-character codes.</summary>
    Documenti = 3,

    /// <summary>Tipi alloggiato, 2-character codes.</summary>
    TipiAlloggiato = 4,
}
