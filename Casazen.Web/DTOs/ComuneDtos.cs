using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

/// <summary>
/// A comune of the official ISTAT list as the pickers and the profiles show it (SU-04). The ISTAT code is the value a form
/// stores; the region is derived from the comune, never chosen.
/// </summary>
public sealed class ComuneDto
{
    public string IstatCode { get; init; } = string.Empty;

    public string? CadastralCode { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ProvinceCode { get; init; } = string.Empty;

    public string? RegionCode { get; init; }

    public string RegionIstatCode { get; init; } = string.Empty;

    public string RegionName { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public static ComuneDto From(Comune comune)
    {
        ArgumentNullException.ThrowIfNull(comune);
        return new ComuneDto
        {
            IstatCode = comune.IstatCode,
            CadastralCode = comune.CadastralCode,
            Name = comune.Name,
            DisplayName = comune.DisplayName,
            ProvinceCode = comune.ProvinceCode,
            RegionCode = comune.RegionCode,
            RegionIstatCode = comune.RegionIstatCode,
            RegionName = comune.RegionName,
            IsActive = comune.IsActive,
        };
    }
}

public sealed class ComuneSearchResponse
{
    /// <summary>
    /// False when the official list is not imported: <see cref="Items"/> is then empty because there is no list, not because
    /// nothing matches. The pickers say so instead of "no results".
    /// </summary>
    public bool DatasetAvailable { get; init; }

    public IReadOnlyList<ComuneDto> Items { get; init; } = [];
}

public sealed class ComuneImportDto
{
    public Guid Id { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public DateOnly ReferenceDate { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public int InsertedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int DeactivatedCount { get; init; }
    public bool IsPartial { get; init; }
    public DateTime ImportedAt { get; init; }

    public static ComuneImportDto From(Casazen.Core.Services.ComuneImportInfo info) => new()
    {
        Id = info.Id,
        Origin = info.Origin.ToString(),
        SourceFileName = info.SourceFileName,
        SourceVersion = info.SourceVersion,
        ReferenceDate = info.ReferenceDate,
        Sha256 = info.Sha256,
        RowCount = info.RowCount,
        InsertedCount = info.InsertedCount,
        UpdatedCount = info.UpdatedCount,
        UnchangedCount = info.UnchangedCount,
        DeactivatedCount = info.DeactivatedCount,
        IsPartial = info.IsPartial,
        ImportedAt = info.ImportedAt,
    };
}

public sealed class ComuneDatasetStatusDto
{
    public bool Available { get; init; }
    public int TotalRows { get; init; }
    public int ActiveRows { get; init; }
    public ComuneImportDto? LastImport { get; init; }

    public IReadOnlyList<string> InvalidPilotComuni { get; init; } = [];
}
