using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Copies a <c>condo-export-v1</c> snapshot into staging. A house changes balances only after it has a verified property
/// and the year matches to the cent. Confirming makes CasaZen the source for that house; the copy is not a sync.
/// </summary>
public interface ICondominiumHistoricalImportService
{
    Task<CondominiumImportBatchView> StageAsync(HostScope scope, string snapshotJson, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumImportBatchView> VerifyPropertyAsync(
        HostScope scope, Guid batchId, Guid houseId, Guid propertyId, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumImportBatchView> ConfirmAsync(HostScope scope, Guid batchId, string userId, CancellationToken cancellationToken = default);
}

public sealed record CondominiumImportYearView(
    string Label,
    decimal Ordinary,
    decimal Extraordinary,
    decimal FinalAmount,
    decimal Prior,
    decimal Payments,
    decimal Settlement,
    int InstallmentCount,
    decimal InstallmentTotal);

public sealed record CondominiumImportHouseView(
    Guid Id,
    string SourceId,
    string Name,
    Guid? PropertyId,
    bool Confirmed,
    bool AffectsBalances,
    IReadOnlyList<CondominiumImportYearView> Years);

public sealed record CondominiumImportBatchView(
    Guid Id,
    string ContentHash,
    bool AlreadyImported,
    bool CasaZenIsSource,
    string? CondolegerReadOnly,
    IReadOnlyList<CondominiumImportHouseView> Houses);

public static class CondominiumImportErrorCodes
{
    public const string Invalid = "condominium_import_invalid";
    public const string NotFound = "condominium_import_not_found";
    public const string Mismatch = "condominium_import_mismatch";
}
