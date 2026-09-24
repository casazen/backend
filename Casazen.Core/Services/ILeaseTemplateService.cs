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

    /// <summary>Preview marked "BOZZA - template non approvato" (or "ANTEPRIMA" when approved): never valid for signature.</summary>
    Task<byte[]> GeneratePreviewPdfAsync(LeaseContract lease);
}
