namespace Casazen.Core.Services;

/// <summary>
/// A public register of the energy certificates, asked for the identification code of an APE whose code cannot be read on the file
/// (the dark header of a photocopy). Today the CENED register of Regione Lombardia, published as open data: it receives the comune
/// and the EP gl,nren index read on the certificate, never the document.
/// </summary>
public interface IApeRegistryLookup
{
    /// <summary>The certificate of the register that the data identify, or null when there is none, more than one, or the register did not answer.</summary>
    Task<ApeRegistryEntry?> FindAsync(ApeRegistryQuery query, CancellationToken cancellationToken = default);
}

/// <param name="Comune">The comune of the property, as the register writes it (case does not matter).</param>
/// <param name="GlobalNonRenewableIndex">EP gl,nren read on the certificate (kWh/m² year).</param>
/// <param name="IssueDate">The issue date read on the certificate, when read: it narrows two certificates with the same index.</param>
public sealed record ApeRegistryQuery(string Comune, decimal GlobalNonRenewableIndex, DateOnly? IssueDate);

/// <summary>A certificate of the register: its code, its class and the day it was filed.</summary>
public sealed record ApeRegistryEntry(string Code, string? EnergyClass, DateOnly? IssueDate);
