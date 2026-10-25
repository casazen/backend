using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// A practice of the simulated ConcordatoFacile module (CF-SIM): solo per ambienti di prova (only for test environments). The test
/// environment has no environment of ConcordatoFacile to try the whole round with, so with <c>ConcordatoFacile:FormMode=Simulated</c>
/// the page <c>/guided-form</c> of the web app creates these rows (the "submit" of the module), whoever tries the round changes their
/// state from the test panel, and the status client reads them back with the shapes of the specification v0.2 §4. The table is empty
/// and inert with <c>FormMode=Real</c>, which is the only mode of production.
/// </summary>
/// <remarks>
/// Not tenant data: ConcordatoFacile is an outside service, its practices are not an org's rows. The lease a practice is bound to is
/// read from the signed link and is not a foreign key (a practice can arrive without one, and the lease can be deleted afterwards).
/// No personal data is kept: the number, the municipality, the state and the dates, which is what the real status API returns.
/// </remarks>
[Table("cf_simulated_practices")]
public class CfSimulatedPractice
{
    /// <summary><c>P-</c> and 16 lowercase hexadecimal characters.</summary>
    public const int PracticeIdLength = 18;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The number of the practice (<c>P-</c> and 16 hexadecimal characters), as the real service writes it.</summary>
    [Required, MaxLength(PracticeIdLength)]
    public string PracticeId { get; set; } = string.Empty;

    /// <summary>The lease of the signed link, only when the link was valid; null when it was missing, wrong or expired (v0.2 §6).</summary>
    public Guid? LeaseId { get; set; }

    /// <summary>The municipality of the practice, as the link named it; null when the link did not.</summary>
    [MaxLength(200)]
    public string? Comune { get; set; }

    [MaxLength(10)]
    public string? Provincia { get; set; }

    public CfPracticeStatus Status { get; set; } = CfPracticeStatus.Received;

    /// <summary><c>duplicate</c>, <c>withdrawn</c>, <c>not_compliant</c> or <c>other</c>, only with <see cref="CfPracticeStatus.ClosedWithoutAttestation"/>.</summary>
    [MaxLength(30)]
    public string? CloseReason { get; set; }

    /// <summary>When the module was submitted (UTC).</summary>
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the state last changed (UTC); it never goes back.</summary>
    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// SHA-256 (hex) of the control token returned once when the practice is created: whoever holds the token can change the state and
    /// download the documents of this practice, nobody else.
    /// </summary>
    [Required, MaxLength(64)]
    public string ControlTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// <c>comuneDiffersFromLink</c> of the status API (v0.2 §4): the module of the simulator does not let the person change the municipality
    /// the link named, so it is <c>false</c> when the practice has a lease and a municipality, and <c>null</c> (unknown) when the link named no
    /// municipality or the practice is not linked to a lease.
    /// </summary>
    public bool? ComuneDiffersFromLink() => LeaseId is not null && !string.IsNullOrWhiteSpace(Comune) ? false : null;
}
