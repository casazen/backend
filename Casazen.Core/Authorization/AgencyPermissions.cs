namespace Casazen.Core.Authorization;

/// <summary>
/// The permissions of the agency tier (ADR-010, ADR-012): what a person of an org on the agency plan may do with the owners of the
/// properties the agency manages, their statements, invoices, withholding and wire transfers, the internal activities and the trusted
/// suppliers. Every <c>agency.*</c> policy of the API requires the agency plan <b>and</b> one of these, held by the caller's org role
/// (<see cref="OrgRoleCatalog.AgencyPermissionsOf"/>, DEB-01): the plan alone let any member of the org, collaborators and accountants
/// included, read the full IBAN of the owners and create wire batches.
/// </summary>
public static class AgencyPermissions
{
    /// <summary>The list of the owners and their details, IBAN masked (<c>GET api/owners</c>).</summary>
    public const string OwnerView = "agency.owner.view";

    /// <summary>The full IBAN of an owner, audit-logged (<c>GET api/owners/{id}/iban</c>, ADR-012-R3).</summary>
    public const string OwnerFinancialRead = "agency.owner.financial.read";

    /// <summary>Invite the owners to their portal (ADR-011-R4).</summary>
    public const string OwnerManage = "agency.owner.manage";

    /// <summary>Read the monthly statements of the owners and export them.</summary>
    public const string StatementView = "agency.statement.view";

    /// <summary>Generate, approve and publish the monthly statements.</summary>
    public const string StatementManage = "agency.statement.manage";

    /// <summary>Generate the invoices of the agency and read their XML and PDF.</summary>
    public const string InvoiceManage = "agency.invoice.manage";

    /// <summary>The withholding tax: summary, F24, reconciliation.</summary>
    public const string WithholdingManage = "agency.withholding.manage";

    /// <summary>The wire transfer batches that pay the owners (SEPA pain.001 and Swiss files).</summary>
    public const string WireManage = "agency.wire.manage";

    /// <summary>The internal activities of the agency's staff.</summary>
    public const string ActivityManage = "agency.activity.manage";

    /// <summary>The agency's trusted suppliers.</summary>
    public const string SupplierManage = "agency.supplier.manage";

    /// <summary>Every agency permission.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        OwnerView, OwnerFinancialRead, OwnerManage,
        StatementView, StatementManage,
        InvoiceManage, WithholdingManage, WireManage,
        ActivityManage, SupplierManage,
    ];
}
