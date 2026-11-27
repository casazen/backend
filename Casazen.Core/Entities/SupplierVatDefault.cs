using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What an organization remembers of the VAT of a cleaning company («Ricorda per questa ditta», FIX/BE-CLEANING-VAT): whether the company applies VAT to its
/// invoices. It answers the question of every cleaning of that company recharged to the organization's owners that has no answer of its own.
/// </summary>
/// <remarks>
/// The organization's own memory, one row for each organization and company: never on the supplier's profile, which every organization reads, so one agency's
/// answer never decides the invoices of another. «Non lo so» is not remembered; the rate is never stored here, it is read from <c>VatRates</c> on the day of each
/// cleaning.
/// </remarks>
[Table("SupplierVatDefaults")]
public class SupplierVatDefault : ITenantOwned
{
    public const string OrgSupplierIndexName = "UIX_SupplierVatDefaults_OrgId_SupplierOrgId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid SupplierOrgId { get; set; }

    /// <summary><c>true</c>: the company applies VAT (the standard rate of the country of each cleaning); <c>false</c>: it applies none.</summary>
    public bool AppliesVat { get; set; }

    public DateTime UpdatedAt { get; set; }

    [MaxLength(255)]
    public string? UpdatedByUserId { get; set; }
}
