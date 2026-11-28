using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Fiscal;

public enum FiscalDocumentExportFormat
{
    Csv = 1,

    Xlsx = 2,
}

public static class FiscalDocumentExportFormats
{
    public static FiscalDocumentExportFormat? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "csv" => FiscalDocumentExportFormat.Csv,
        "xlsx" => FiscalDocumentExportFormat.Xlsx,
        _ => null,
    };

    public static string ExtensionOf(FiscalDocumentExportFormat format) => format == FiscalDocumentExportFormat.Xlsx ? "xlsx" : "csv";

    public static string ContentTypeOf(FiscalDocumentExportFormat format) => format == FiscalDocumentExportFormat.Xlsx ? XlsxWriter.ContentType : "text/csv";
}
