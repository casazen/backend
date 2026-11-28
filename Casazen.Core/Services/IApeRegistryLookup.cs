namespace Casazen.Core.Services;

/// <summary>
/// A public register of the energy certificates, asked for the identification code of an APE whose code cannot be read on the file
/// (the dark header of a photocopy). Today the CENED register of Regione Lombardia, published as open data: it receives the comune
/// and the EP gl,nren index read on the certificate, never the document.
/// </summary>
public interface IApeRegistryLookup
{
    Task<ApeRegistryEntry?> FindAsync(ApeRegistryQuery query, CancellationToken cancellationToken = default);
}

public sealed record ApeRegistryQuery(string Comune, decimal GlobalNonRenewableIndex, DateOnly? IssueDate);

public sealed record ApeRegistryEntry(string Code, string? EnergyClass, DateOnly? IssueDate);
