using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

/// <summary>
/// A comune of the official ISTAT list as the pickers and the profiles show it (SU-04). The ISTAT code is the value a form
/// stores; the region is derived from the comune, never chosen.
/// </summary>
public sealed class ComuneDto
{
    /// <summary>ISTAT code, 6 digits, a string: the leading zeros matter.</summary>
    public string IstatCode { get; init; } = string.Empty;

    /// <summary>Cadastral (Belfiore) code, e.g. <c>H501</c>; null when the official list does not have it yet.</summary>
    public string? CadastralCode { get; init; }

    /// <summary>Name in Italian: what is stored as the city of a property.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Official denomination, with the other language where there is one (<c>Bolzano/Bozen</c>).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Province plate code, two letters.</summary>
    public string ProvinceCode { get; init; } = string.Empty;

    /// <summary>CasaZen's region code (<c>LOM</c>), the key of the regional rules.</summary>
    public string? RegionCode { get; init; }

    /// <summary>ISTAT code of the region (<c>03</c>).</summary>
    public string RegionIstatCode { get; init; } = string.Empty;

    public string RegionName { get; init; } = string.Empty;

    /// <summary>False for a comune that is no longer in the list (a merger, a suppression): it cannot be chosen again.</summary>
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

/// <summary>Answer of <c>GET /api/comuni</c>: the matches and whether the official list is imported at all.</summary>
public sealed class ComuneSearchResponse
{
    /// <summary>
    /// False when the official list is not imported: <see cref="Items"/> is then empty because there is no list, not because
    /// nothing matches. The pickers say so instead of "no results".
    /// </summary>
    public bool DatasetAvailable { get; init; }

    public IReadOnlyList<ComuneDto> Items { get; init; } = [];
}

/// <summary>Source of the official list, as shown to admins.</summary>
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

/// <summary>State of the official list for <c>GET /api/admin/comuni</c>.</summary>
public sealed class ComuneDatasetStatusDto
{
    public bool Available { get; init; }
    public int TotalRows { get; init; }
    public int ActiveRows { get; init; }
    public ComuneImportDto? LastImport { get; init; }

    /// <summary>
    /// Pilot comuni of the supplier self-serve registration (<c>Suppliers:PilotComuni</c>) that are not an active comune of the
    /// list: they are not offered until the configuration is fixed. Empty when the list is not imported.
    /// </summary>
    public IReadOnlyList<string> InvalidPilotComuni { get; init; } = [];
}
