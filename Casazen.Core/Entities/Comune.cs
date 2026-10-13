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

    /// <summary>Name in Italian ("Denominazione in italiano"), the one shown and stored as city.</summary>
    [Required, MaxLength(ComuneRules.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Official denomination with the other language, when there is one (<c>Bolzano/Bozen</c>); else the name.</summary>
    [Required, MaxLength(ComuneRules.DisplayNameMaxLength)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary><see cref="Name"/> normalized for matching (<see cref="ComuneNames.Normalize"/>).</summary>
    [Required, MaxLength(ComuneRules.NameMaxLength)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary><see cref="DisplayName"/> normalized: where the search looks for names in the other language too.</summary>
    [Required, MaxLength(ComuneRules.DisplayNameMaxLength)]
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Province plate code ("sigla automobilistica"), two letters (<c>MI</c>, <c>NA</c>).</summary>
    [Required, MaxLength(ComuneRules.ProvinceCodeLength)]
    public string ProvinceCode { get; set; } = string.Empty;

    /// <summary>ISTAT code of the region, two digits (<c>03</c> Lombardia).</summary>
    [Required, MaxLength(ComuneRules.RegionIstatCodeLength)]
    public string RegionIstatCode { get; set; } = string.Empty;

    /// <summary>Name of the region as written in the official list.</summary>
    [Required, MaxLength(ComuneRules.RegionNameMaxLength)]
    public string RegionName { get; set; } = string.Empty;

    /// <summary>
    /// CasaZen's code of the region (<c>LOM</c>, <c>LAZ</c>), the one of <c>Compliance:RequiredDocuments</c>, the tourist
    /// tax rates and the SEO pages. Derived from <see cref="RegionIstatCode"/> (<see cref="ItalianRegions"/>), not stored.
    /// </summary>
    [NotMapped]
    public string? RegionCode => ItalianRegions.FindByIstatCode(RegionIstatCode)?.Code;

    /// <summary>
    /// True while the comune is in the latest full list imported. False once a later full list no longer has it, or when
    /// the file said it ceased: it can no longer be selected, a code already stored still resolves.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>First day of validity when the file states it (null: the official list does not).</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>
    /// Last day of validity. From the file when it states it; otherwise the reference date of the full list that no longer
    /// had the comune. Null while <see cref="IsActive"/>.
    /// </summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>The import that created the row or last changed it (source file, version and reference date).</summary>
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

    /// <summary>Name of the file (the uploaded one, or the seed file of the deploy).</summary>
    [Required, MaxLength(255)]
    public string SourceFileName { get; set; } = string.Empty;

    /// <summary>Source and edition of the list, as written by whoever imported it (e.g. "ISTAT, Elenco dei comuni, 21/02/2026").</summary>
    [Required, MaxLength(300)]
    public string SourceVersion { get; set; } = string.Empty;

    /// <summary>Date the list is valid at (the "aggiornato al" date of the official file).</summary>
    public DateOnly ReferenceDate { get; set; }

    /// <summary>SHA-256 (lower-case hex) of the imported file.</summary>
    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Official URL the file was downloaded from. Null for an admin upload of a local file.</summary>
    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    /// <summary>Publishing body (e.g. ISTAT). Null when not stated.</summary>
    [MaxLength(200)]
    public string? Authority { get; set; }

    /// <summary>When the file was retrieved from <see cref="SourceUrl"/>. Null for an admin upload.</summary>
    public DateTime? RetrievedAt { get; set; }

    public int RowCount { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }

    /// <summary>Comuni that were active and are not in the file (full list only).</summary>
    public int DeactivatedCount { get; set; }

    /// <summary>True when the file is declared a part of the list: nothing is deactivated.</summary>
    public bool IsPartial { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Auth0 id of the admin, or <c>system</c> for the seed file.</summary>
    [Required, MaxLength(200)]
    public string ImportedBy { get; set; } = string.Empty;
}

/// <summary>Where an import came from. Stored as an integer; never renumber.</summary>
public enum ComuneImportOrigin
{
    /// <summary>File uploaded by an admin (<c>POST /api/admin/comuni/import</c>).</summary>
    AdminUpload = 1,

    /// <summary>Seed file shipped with the deploy (<c>Data/Seeds/comuni-istat.csv</c>), loaded at startup.</summary>
    StartupSeed = 2,

    /// <summary>File downloaded by the scheduled job from the ISTAT permalink.</summary>
    ScheduledDownload = 3,
}
