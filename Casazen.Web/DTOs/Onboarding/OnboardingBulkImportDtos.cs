using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// The answer of <c>POST api/onboarding/bulk/import</c> (E10/F02/US01/T02): the same shape for the check (<c>dryRun</c> true, nothing written) and
/// for the import (only the valid rows written). <c>unknownColumns</c> are the header cells the import does not know, once each. Codes only, and
/// <b>never the lock code</b>: a row says only <c>lockCodeProvided</c>.
/// </summary>
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

/// <summary>How many rows the file has, how many are valid, how many are not and how many changed something (<c>applied</c>: a valid row that repeats what the property holds, or has only its key, is not).</summary>
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

/// <summary>An error of a row: the column (<c>propertyId</c>, <c>cinCode</c>, <c>comuneIstatCode</c>, <c>regionCode</c>, <c>alloggiatiRoomNumbers</c>, <c>lockCode</c>) and the code.</summary>
public sealed class BulkImportRowErrorDto
{
    public string Column { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}
