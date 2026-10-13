namespace Casazen.Core.Services;

/// <summary>
/// Downloads the official ISTAT comuni list, the Alloggiati Web code tables and the tourist-tax pages of the
/// configured pilot comuni, and imports them when the official file changed. Failures are logged; amounts are
/// never invented.
/// </summary>
public interface IOfficialReferenceDataRefreshService
{
    Task<OfficialReferenceDataRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record OfficialReferenceDataRefreshResult(
    OfficialDatasetRefreshResult IstatComuni,
    IReadOnlyList<OfficialDatasetRefreshResult> Alloggiati,
    IReadOnlyList<OfficialDatasetRefreshResult> TouristTax);

public sealed record OfficialDatasetRefreshResult(
    string Dataset,
    string SourceUrl,
    string Status,
    string? Detail,
    int? HttpStatus = null,
    string? Sha256 = null);
