using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ICedolareAdvisoryService
{
    Task<CedolareAdvisoryResult?> EvaluateAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}

public record CedolareAdvisoryResult(
    FiscalRegime LeaseRegime,
    decimal AnnualRent,
    decimal CedolareRate,
    decimal CedolareEstimateEur,
    decimal RegistroRate,
    decimal RegistroEstimateEur,
    decimal BolloEur,
    string OrdinaryIrpefNote,
    string Disclaimer);

public interface IRliExportService
{
    Task<RliExportResult?> ExportAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}

public record RliExportResult(byte[] PdfBytes, string FileName);

public interface IRliChecklistService
{
    Task<RliChecklistResult?> GetAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}

public record RliChecklistResult(
    DateTime RegistrationDeadline,
    int DaysRemaining,
    string TosVersion,
    string AttestationText,
    IReadOnlyList<RliChecklistItem> Items);

public record RliChecklistItem(string Key, string Label, bool Done);

public record RegistrationAuthorizationRequest(string TosVersion, bool AttestationAccepted);
