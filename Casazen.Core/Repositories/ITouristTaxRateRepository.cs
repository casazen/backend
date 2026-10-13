using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface ITouristTaxRateRepository
{
    Task<TouristTaxRate?> GetByIdAsync(Guid id);
    Task<IEnumerable<TouristTaxRate>> GetAllAsync();

    /// <summary>
    /// Active rates of every comune whose validity overlaps <paramref name="from"/>..<paramref name="to"/> (dates
    /// included). The table is small reference data: the comune is matched by the caller with
    /// <see cref="Casazen.Core.TouristTax.TouristTaxComune.Matches"/> (ISTAT code or normalized name).
    /// </summary>
    Task<IReadOnlyList<TouristTaxRate>> GetActiveInPeriodAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    Task<TouristTaxRate> AddAsync(TouristTaxRate taxRate);
    Task UpdateAsync(TouristTaxRate taxRate);
    Task DeleteAsync(Guid id);
}
