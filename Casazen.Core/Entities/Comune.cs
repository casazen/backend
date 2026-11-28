using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Entities;

/// <summary>
/// A comune of the official ISTAT list ("Elenco dei comuni italiani", SU-04): platform reference data, the same for every
/// org. It is loaded only from the file of the official list (admin import or the seed file of the deploy, never written
/// by hand): <c>docs/runbooks/comuni-istat.md</c>. The ISTAT code is the key; properties and supplier profiles store it
/// without a foreign key because a comune is never deleted, only deactivated (<see cref="IsActive"/>) when a later full
/// list no longer has it (merger, suppression), so a stored code stays resolvable.
/// </summary>
[Table("Comuni")]
public class Comune
{
    /// <summary>ISTAT code of the comune, 6 digits: 3 of the (historical) province + 3 of the comune, leading zeros kept.</summary>
    [Key, MaxLength(ComuneRules.IstatCodeLength)]
    public string IstatCode { get; set; } = string.Empty;

    /// <summary>
    /// Cadastral (Belfiore) code: one letter and 3 digits, assigned by the Agenzia delle Entrate. Unique among the active
    /// comuni. Null when the list says "N.d." (not available: a comune just instituted).
    /// </summary>
    [MaxLength(ComuneRules.CadastralCodeLength)]
    public string? CadastralCode { get; set; }

    [Required, MaxLength(ComuneRules.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.DisplayNameMaxLength)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.NameMaxLength)]
    public string NormalizedName { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.DisplayNameMaxLength)]
    public string SearchText { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.ProvinceCodeLength)]
    public string ProvinceCode { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.RegionIstatCodeLength)]
    public string RegionIstatCode { get; set; } = string.Empty;

    [Required, MaxLength(ComuneRules.RegionNameMaxLength)]
    public string RegionName { get; set; } = string.Empty;

    [NotMapped]
    public string? RegionCode => ItalianRegions.FindByIstatCode(RegionIstatCode)?.Code;

    public bool IsActive { get; set; } = true;

    public DateOnly? ValidFrom { get; set; }

    /// <summary>
    /// Last day of validity. From the file when it states it; otherwise the reference date of the full list that no longer
    /// had the comune. Null while <see cref="IsActive"/>.
    /// </summary>
    public DateOnly? ValidTo { get; set; }

    public Guid SourceImportId { get; set; }

    public virtual ComuneImport SourceImport { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One import of the official comuni list: which file (name and SHA-256), which version and reference date, how many rows
/// it created, changed, left as they were and deactivated. Re-importing the same file changes nothing (idempotent) and is
/// logged anyway.
/// </summary>
[Table("ComuneImports")]
public class ComuneImport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public ComuneImportOrigin Origin { get; set; }

    [Required, MaxLength(255)]
    public string SourceFileName { get; set; } = string.Empty;

    /// <summary>Source and edition of the list, as written by whoever imported it (e.g. "ISTAT, Elenco dei comuni, 21/02/2026").</summary>
    [Required, MaxLength(300)]
    public string SourceVersion { get; set; } = string.Empty;

    public DateOnly ReferenceDate { get; set; }

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [MaxLength(200)]
    public string? Authority { get; set; }

    public DateTime? RetrievedAt { get; set; }

    public int RowCount { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }

    public int DeactivatedCount { get; set; }

    public bool IsPartial { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(200)]
    public string ImportedBy { get; set; } = string.Empty;
}

public enum ComuneImportOrigin
{
    AdminUpload = 1,

    /// <summary>Seed file shipped with the deploy (<c>Data/Seeds/comuni-istat.csv</c>), loaded at startup.</summary>
    StartupSeed = 2,

    ScheduledDownload = 3,
}
