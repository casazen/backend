using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>The formats of <c>GET api/fiscal/documents/export</c> (E06/F03/US03/T01, FIS-07, NF-87).</summary>
public enum FiscalDocumentExportFormat
{
    /// <summary>The CSV of <see cref="CsvWriter"/>: UTF-8 with BOM, <c>;</c>, decimal comma, for a spreadsheet set to Italian.</summary>
    Csv = 1,

    /// <summary>The Excel file of <see cref="XlsxWriter"/>: one sheet, amounts as numbers, days as dates.</summary>
    Xlsx = 2,
}

/// <summary>What the word <c>format</c> of an export request stands for, and how its file is named and typed.</summary>
public static class FiscalDocumentExportFormats
{
    /// <summary>The format a request names (<c>csv</c> or <c>xlsx</c>, whatever the case), <c>null</c> for a word the export does not know and for none at all.</summary>
    public static FiscalDocumentExportFormat? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "csv" => FiscalDocumentExportFormat.Csv,
        "xlsx" => FiscalDocumentExportFormat.Xlsx,
        _ => null,
    };

    /// <summary>The extension of the file (without the dot).</summary>
    public static string ExtensionOf(FiscalDocumentExportFormat format) => format == FiscalDocumentExportFormat.Xlsx ? "xlsx" : "csv";

    /// <summary>The content type of the file.</summary>
    public static string ContentTypeOf(FiscalDocumentExportFormat format) => format == FiscalDocumentExportFormat.Xlsx ? XlsxWriter.ContentType : "text/csv";
}
