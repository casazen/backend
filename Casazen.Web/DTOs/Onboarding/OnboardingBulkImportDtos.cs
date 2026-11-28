using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Onboarding;

public sealed class BulkImportResultDto
{
    public bool DryRun { get; set; }

    public BulkImportSummaryDto Summary { get; set; } = new();

    public List<string> UnknownColumns { get; set; } = [];

    public List<BulkImportRowDto> Rows { get; set; } = [];

    public static BulkImportResultDto From(BulkImportResult result) => new()
    {
        DryRun = result.DryRun,
        Summary = new BulkImportSummaryDto
        {
            Rows = result.Summary.Rows,
            Valid = result.Summary.Valid,
            Invalid = result.Summary.Invalid,
            Applied = result.Summary.Applied,
        },
        UnknownColumns = [.. result.UnknownColumns],
        Rows =
        [
            .. result.Rows.Select(row => new BulkImportRowDto
            {
                RowNumber = row.RowNumber,
                PropertyRef = row.PropertyRef,
                PropertyId = row.PropertyId,
                Status = row.Status,
                Errors = [.. row.Errors.Select(error => new BulkImportRowErrorDto { Column = error.Column, Code = error.Code })],
                LockCodeProvided = row.LockCodeProvided,
                Applied = row.Applied,
            }),
        ],
    };
}

public sealed class BulkImportSummaryDto
{
    public int Rows { get; set; }

    public int Valid { get; set; }

    public int Invalid { get; set; }

    public int Applied { get; set; }
}

/// <summary>
/// One row of the file. <c>status</c> is <c>ok</c> or the code of its first error (<c>property_not_found</c>, <c>property_ambiguous</c>,
/// <c>comune_unknown</c>, <c>region_mismatch</c>, <c>room_numbers_invalid</c>, <c>lock_code_invalid</c>, <c>duplicate_row</c>); <c>errors</c> lists every
/// error with its column. <c>rowNumber</c> is the row of the sheet (the header is row 1). <c>applied</c> is true only when the row changed something
/// (never in a dry run): a valid row that holds what the property has already, or that has only its key, is <c>ok</c> and not applied.
/// </summary>
public sealed class BulkImportRowDto
{
    public int RowNumber { get; set; }

    public string? PropertyRef { get; set; }

    public Guid? PropertyId { get; set; }

    public string Status { get; set; } = string.Empty;

    public List<BulkImportRowErrorDto> Errors { get; set; } = [];

    public bool LockCodeProvided { get; set; }

    public bool Applied { get; set; }
}

public sealed class BulkImportRowErrorDto
{
    public string Column { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}
