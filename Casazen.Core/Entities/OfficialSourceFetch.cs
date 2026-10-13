using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// One attempt to read an official public dataset (ISTAT comuni, Alloggiati code tables, tourist-tax page of a
/// comune). Platform reference data: URL, retrieval time, authority and outcome of every check, including failures
/// that must not invent amounts. Written by the scheduled refresh job (RS-6, RS-7, CO-12).
/// </summary>
[Table("OfficialSourceFetches")]
public class OfficialSourceFetch
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable dataset key (<see cref="OfficialSourceDatasets"/>).</summary>
    [Required, MaxLength(40)]
    public string Dataset { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string SourceUrl { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Authority { get; set; } = string.Empty;

    public DateTime RetrievedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? Sha256 { get; set; }

    public int? HttpStatus { get; set; }

    /// <summary>Outcome of the attempt (<see cref="OfficialSourceFetchStatus"/>).</summary>
    [Required, MaxLength(40)]
    public string Status { get; set; } = string.Empty;

    /// <summary>Short reason or counts; never a document body, never a secret.</summary>
    [MaxLength(500)]
    public string? Detail { get; set; }

    /// <summary>ISTAT code of the comune, when the fetch is a tourist-tax page of one comune.</summary>
    [MaxLength(6)]
    public string? IstatCode { get; set; }
}

/// <summary>Dataset keys stored on <see cref="OfficialSourceFetch.Dataset"/>.</summary>
public static class OfficialSourceDatasets
{
    public const string IstatComuni = "istat_comuni";
    public const string AlloggiatiComuni = "alloggiati_comuni";
    public const string AlloggiatiStati = "alloggiati_stati";
    public const string AlloggiatiDocumenti = "alloggiati_documenti";
    public const string AlloggiatiTipiAlloggiato = "alloggiati_tipi_alloggiato";
    public const string TouristTax = "tourist_tax";
    public const string MefNuovaAtIndex = "mef_nuova_at_index";
    public const string MefImpostaSoggiorno = "mef_imposta_soggiorno";
}

/// <summary>Outcome keys stored on <see cref="OfficialSourceFetch.Status"/>.</summary>
public static class OfficialSourceFetchStatus
{
    public const string Imported = "imported";
    public const string Unchanged = "unchanged";
    public const string FetchFailed = "fetch_failed";
    public const string Rejected = "rejected";
    public const string ExtractFailed = "extract_failed";
    public const string HostNotAllowed = "host_not_allowed";
}
