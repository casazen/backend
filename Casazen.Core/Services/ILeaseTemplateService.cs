using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>Lease contract PDF from the lawyer-approved template of the lease's fiscal regime (LT-03, A7-03).</summary>
public interface ILeaseTemplateService
{
    /// <summary>
    /// Final contract, the one sent to signature and registration. Throws a <c>DomainRuleException</c> (422)
    /// <c>contract_template_not_approved</c> when the template of the regime is missing, incomplete or not approved,
    /// and <c>contract_data_missing</c> when the lease lacks a datum the template uses.
    /// </summary>
    Task<byte[]> GeneratePdfAsync(LeaseContract lease);

    /// <summary>
    /// The gate of <see cref="GeneratePdfAsync"/> without building the PDF: throws the same 422 errors when the final
    /// contract of <paramref name="lease"/> cannot exist (LT-02: an offline signature needs an approved template too).
    /// </summary>
    void EnsureFinalContractAvailable(LeaseContract lease);

    /// <summary>
    /// The error code <see cref="GeneratePdfAsync"/> would answer for <paramref name="lease"/>
    /// (<c>contract_template_not_approved</c>, <c>contract_data_missing</c>), or null when the final contract can be
    /// generated. Throws and logs nothing: the signature panel reads it on every load (LT-02).
    /// </summary>
    string? GetFinalContractBlocker(LeaseContract lease);

    /// <summary>Preview marked "BOZZA - template non approvato" (or "ANTEPRIMA" when approved): never valid for signature.</summary>
    Task<byte[]> GeneratePreviewPdfAsync(LeaseContract lease);
}
