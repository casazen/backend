using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the supplier's earnings
/// (SP-16a, <c>api/supplier/earnings</c>). The codes are snake_case and never renamed: the frontend branches on them. A tab or a period that is not
/// valid is a 400 <c>validation_error</c> (the keys below), a selection too large for one file a 422.
/// </summary>
public static class SupplierEarningsErrors
{
    /// <summary>422: the export would hold more than <see cref="SupplierEarningsLimits.MaxExportRows"/> rows: the supplier narrows the period.</summary>
    public const string ExportTooLarge = "supplier_earnings_export_too_large";

    /// <summary>The key of <see cref="ExportTooLarge"/>.</summary>
    public const string ExportTooLargeMessageKey = "SupplierEarningsExportTooLarge";

    /// <summary>400 <c>validation_error</c>: the <c>tab</c> is not one of <see cref="SupplierEarningsTabs"/>.</summary>
    public const string TabInvalidMessageKey = "SupplierEarningsTabInvalid";

    /// <summary>400 <c>validation_error</c>: <c>from</c> is after <c>to</c>.</summary>
    public const string PeriodInvalidMessageKey = "SupplierEarningsPeriodInvalid";

    /// <summary>Every <c>SharedResources</c> key of the earnings (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        ExportTooLargeMessageKey,
        TabInvalidMessageKey,
        PeriodInvalidMessageKey,
    ];

    /// <summary>The 422 of an export that is too large.</summary>
    public static DomainRuleException TooLargeToExport() =>
        new(ExportTooLarge, ExportTooLargeMessageKey, SupplierEarningsLimits.MaxExportRows);
}
