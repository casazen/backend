namespace Casazen.Core.Services;

/// <summary>
/// Validates the comune a host chose for a property against the official ISTAT list and gives the region that follows it
/// (SU-04). The code is the only thing stored as trusted: it is never inferred from the free-text city.
/// </summary>
public interface IPropertyComuneResolver
{
    /// <summary>The active comune with this ISTAT code.</summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// 422 <c>comuni_dataset_unavailable</c> when the official list is not imported, <c>comune_istat_unknown</c> when the code
    /// is not an active comune of it.
    /// </exception>
    Task<PropertyComune> ResolveAsync(string istatCode, CancellationToken cancellationToken = default);
}

/// <param name="IstatCode">ISTAT code, 6 digits.</param>
/// <param name="Name">Name of the comune in Italian.</param>
/// <param name="RegionCode">CasaZen's region code (<c>LOM</c>) of the comune; the key of <c>Compliance:RequiredDocuments</c>.</param>
public sealed record PropertyComune(string IstatCode, string Name, string RegionCode);
