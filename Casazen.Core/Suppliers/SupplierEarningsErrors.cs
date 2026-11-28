using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the supplier's earnings
/// (SP-16a, <c>api/supplier/earnings</c>). The codes are snake_case and never renamed: the frontend branches on them. A tab or a period that is not
/// valid is a 400 <c>validation_error</c> (the keys below), a selection too large for one file a 422.
/// </summary>
public static class SupplierEarningsErrors
{
    public const string ExportTooLarge = "supplier_earnings_export_too_large";

    public const string ExportTooLargeMessageKey = "SupplierEarningsExportTooLarge";

    public const string TabInvalidMessageKey = "SupplierEarningsTabInvalid";

    public const string PeriodInvalidMessageKey = "SupplierEarningsPeriodInvalid";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        ExportTooLargeMessageKey,
        TabInvalidMessageKey,
        PeriodInvalidMessageKey,
    ];

    public static DomainRuleException TooLargeToExport() =>
        new(ExportTooLarge, ExportTooLargeMessageKey, SupplierEarningsLimits.MaxExportRows);
}
