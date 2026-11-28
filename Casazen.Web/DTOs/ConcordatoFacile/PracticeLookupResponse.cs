using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Dtos.ConcordatoFacile;

public class PracticeLookupResponse
{
    public string? LeaseId { get; set; }

    public required string PracticeId { get; set; }

    public CfPracticeStatus? Status { get; set; }

    public required bool ManualSelectionRequired { get; set; }
}
