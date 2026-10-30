using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.Dtos.CfSimulator;

/// <summary>
/// The query string of the link to the module (specification v0.2 §2): <c>partner</c>, <c>leaseId</c>, <c>expiresAt</c>, <c>signature</c>,
/// <c>comune</c>, <c>provincia</c>. Every value is optional here: a link that lacks one is not an error, the module can be filled in all the same.
/// </summary>
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

/// <summary>A practice as the status API of ConcordatoFacile shows it (v0.2 §4).</summary>
public sealed class CfSimulatorPracticeResponse
{
    public required string PracticeId { get; init; }

    public required CfPracticeStatus Status { get; init; }

    public required DateTime StatusChangedAt { get; init; }

    public required DateTime SubmittedAt { get; init; }

    public string? Comune { get; init; }

    /// <summary><c>null</c> when the link named no municipality or the practice has no lease.</summary>
    public bool? ComuneDiffersFromLink { get; init; }

    /// <summary>Only with <see cref="CfPracticeStatus.ClosedWithoutAttestation"/>: duplicate, withdrawn, not_compliant or other.</summary>
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

/// <summary>What the module learns from the link when its page opens.</summary>
public sealed class CfSimulatorLinkResponse
{
    /// <summary>The signature and the expiry of the link are good.</summary>
    public required bool Valid { get; init; }

    /// <summary>Why the link is not good (<c>expired</c>, <c>signature_invalid</c>...); null when it is.</summary>
    public string? Reason { get; init; }

    public Guid? LeaseId { get; init; }

    public string? Comune { get; init; }

    public string? Provincia { get; init; }

    /// <summary>The practices already born from the contract, newest first; empty when the link is not good.</summary>
    public required IReadOnlyList<CfSimulatorPracticeResponse> Practices { get; init; }
}

/// <summary>The practice just born in the module: the number, the state and the token that controls it, shown only here.</summary>
public sealed class CfSimulatorCreatedPracticeResponse
{
    public required string PracticeId { get; init; }

    /// <summary>The secret that lets whoever holds it change the state of the practice and download its documents.</summary>
    public required string ControlToken { get; init; }

    public required CfPracticeStatus Status { get; init; }

    public required DateTime StatusChangedAt { get; init; }

    /// <summary>False when the link was not good and the practice arrived without a lease (v0.2 §6).</summary>
    public required bool Linked { get; init; }
}

/// <summary>Body of the change of the state of a practice from the test panel.</summary>
public sealed class CfSimulatorSetStatusRequest
{
    [Required]
    public string? ControlToken { get; set; }

    [Required]
    public CfPracticeStatus? Status { get; set; }

    /// <summary>Only for <see cref="CfPracticeStatus.ClosedWithoutAttestation"/>: duplicate, withdrawn, not_compliant or other (other when missing).</summary>
    [RegularExpression("^(duplicate|withdrawn|not_compliant|other)$")]
    public string? CloseReason { get; set; }
}
