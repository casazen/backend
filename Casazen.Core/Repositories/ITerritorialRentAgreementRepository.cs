using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITerritorialRentAgreementRepository
{
    Task<TerritorialRentAgreement?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TerritorialRentAgreement>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<TerritorialRentAgreement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConcordatoRentBand?> GetBandByIdAsync(Guid agreementId, Guid bandId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IHighTensionAreaComuneRepository
{
    Task<HighTensionAreaComune?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);
}

public interface IComuneImuChannelRepository
{
    Task<ComuneImuChannel?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComuneImuChannel>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ComuneImuChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IRegulatoryDataAuditLogRepository
{
    Task AddAsync(RegulatoryDataAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegulatoryDataAuditEntry>> GetByEntityIdAsync(Guid entityId, CancellationToken cancellationToken = default);
}
