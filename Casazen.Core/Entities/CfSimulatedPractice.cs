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
    public const int PracticeIdLength = 18;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(PracticeIdLength)]
    public string PracticeId { get; set; } = string.Empty;

    public Guid? LeaseId { get; set; }

    [MaxLength(200)]
    public string? Comune { get; set; }

    [MaxLength(10)]
    public string? Provincia { get; set; }

    public CfPracticeStatus Status { get; set; } = CfPracticeStatus.Received;

    [MaxLength(30)]
    public string? CloseReason { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(64)]
    public string ControlTokenHash { get; set; } = string.Empty;

    public bool? ComuneDiffersFromLink() => LeaseId is not null && !string.IsNullOrWhiteSpace(Comune) ? false : null;
}
