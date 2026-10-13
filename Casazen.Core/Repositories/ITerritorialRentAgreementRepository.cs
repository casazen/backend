using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITerritorialRentAgreementRepository
{
    Task<TerritorialRentAgreement?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);

    /// <summary>Every agreement (admin list, LT-13), with its bands for the zone names and band count.</summary>
    Task<IReadOnlyList<TerritorialRentAgreement>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>One agreement with its bands and signatories, or null (admin detail/edit, LT-13).</summary>
    Task<TerritorialRentAgreement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>One band of an agreement, tracked, or null (admin band edit, LT-13).</summary>
    Task<ConcordatoRentBand?> GetBandByIdAsync(Guid agreementId, Guid bandId, CancellationToken cancellationToken = default);

    /// <summary>Persists changes made on entities loaded from this repository (LT-13).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IHighTensionAreaComuneRepository
{
    Task<HighTensionAreaComune?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);
}

public interface IComuneImuChannelRepository
{
    /// <summary>Channel of the given comune (case-insensitive), or null when no reference data is known (LT-13, A7-22).</summary>
    Task<ComuneImuChannel?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);

    /// <summary>Every channel (admin list, LT-13).</summary>
    Task<IReadOnlyList<ComuneImuChannel>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ComuneImuChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Audit trail of admin changes to regulatory reference data (LT-13, A7-22).</summary>
public interface IRegulatoryDataAuditLogRepository
{
    Task AddAsync(RegulatoryDataAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Entries of one row, newest first.</summary>
    Task<IReadOnlyList<RegulatoryDataAuditEntry>> GetByEntityIdAsync(Guid entityId, CancellationToken cancellationToken = default);
}
