using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITerritorialRentAgreementRepository
{
    Task<TerritorialRentAgreement?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);
}

public interface IHighTensionAreaComuneRepository
{
    Task<HighTensionAreaComune?> GetByComuneAsync(string comune, CancellationToken cancellationToken = default);
}
