using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.Dtos.CfSimulator;

public sealed class CfSimulatorLinkQuery
{
    public string? Partner { get; set; }

    public string? LeaseId { get; set; }

    public string? ExpiresAt { get; set; }

    public string? Signature { get; set; }

    public string? Comune { get; set; }

    public string? Provincia { get; set; }

    public CfSimulatorLinkParameters ToParameters() => new(Partner, LeaseId, ExpiresAt, Signature, Comune, Provincia);
}

public sealed class CfSimulatorPracticeResponse
{
    public required string PracticeId { get; init; }

    public required CfPracticeStatus Status { get; init; }

    public required DateTime StatusChangedAt { get; init; }

    public required DateTime SubmittedAt { get; init; }

    public string? Comune { get; init; }

    public bool? ComuneDiffersFromLink { get; init; }

    public string? CloseReason { get; init; }

    public static CfSimulatorPracticeResponse From(CfSimulatorPracticeView view) => new()
    {
        PracticeId = view.PracticeId,
        Status = view.Status,
        StatusChangedAt = view.StatusChangedAt,
        SubmittedAt = view.SubmittedAt,
        Comune = view.Comune,
        ComuneDiffersFromLink = view.ComuneDiffersFromLink,
        CloseReason = view.CloseReason,
    };
}

public sealed class CfSimulatorLinkResponse
{
    public required bool Valid { get; init; }

    public string? Reason { get; init; }

    public Guid? LeaseId { get; init; }

    public string? Comune { get; init; }

    public string? Provincia { get; init; }

    public required IReadOnlyList<CfSimulatorPracticeResponse> Practices { get; init; }
}

public sealed class CfSimulatorCreatedPracticeResponse
{
    public required string PracticeId { get; init; }

    public required string ControlToken { get; init; }

    public required CfPracticeStatus Status { get; init; }

    public required DateTime StatusChangedAt { get; init; }

    public required bool Linked { get; init; }
}

public sealed class CfSimulatorSetStatusRequest
{
    [Required]
    public string? ControlToken { get; set; }

    [Required]
    public CfPracticeStatus? Status { get; set; }

    [RegularExpression("^(duplicate|withdrawn|not_compliant|other)$")]
    public string? CloseReason { get; set; }
}
