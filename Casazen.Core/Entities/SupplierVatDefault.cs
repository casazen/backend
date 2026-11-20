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
    /// <summary>The unique index of the organization and the company: one memory for each.</summary>
    public const string OrgSupplierIndexName = "UIX_SupplierVatDefaults_OrgId_SupplierOrgId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The organization that remembers (the host org of the cleaning requests).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The cleaning company (its supplier org).</summary>
    public Guid SupplierOrgId { get; set; }

    /// <summary><c>true</c>: the company applies VAT (the standard rate of the country of each cleaning); <c>false</c>: it applies none.</summary>
    public bool AppliesVat { get; set; }

    /// <summary>When it was remembered or last changed (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Who remembered it.</summary>
    [MaxLength(255)]
    public string? UpdatedByUserId { get; set; }
}
