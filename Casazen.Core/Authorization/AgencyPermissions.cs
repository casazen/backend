namespace Casazen.Core.Authorization;

public static class AgencyPermissions
{
    public const string OwnerView = "agency.owner.view";

    public const string OwnerFinancialRead = "agency.owner.financial.read";

    public const string OwnerManage = "agency.owner.manage";

    public const string StatementView = "agency.statement.view";

    public const string StatementManage = "agency.statement.manage";

    public const string InvoiceManage = "agency.invoice.manage";

    public const string WithholdingManage = "agency.withholding.manage";

    public const string WireManage = "agency.wire.manage";

    public const string ActivityManage = "agency.activity.manage";

    public const string SupplierManage = "agency.supplier.manage";

    public const string OwnerFinancialManage = "agency.owner.financial.manage";

    public const string StatementPublish = "agency.statement.publish";


    public static IReadOnlyList<string> All { get; } =
    [
        OwnerView, OwnerFinancialRead, OwnerManage,
        StatementView, StatementManage,
        InvoiceManage, WithholdingManage, WireManage,
        ActivityManage, SupplierManage,
        OwnerFinancialManage, StatementPublish,
    ];
}
