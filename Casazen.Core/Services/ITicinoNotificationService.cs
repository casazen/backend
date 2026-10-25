namespace Casazen.Core.Services;

/// <summary>
/// Ticino police notification file generator (notifiche.ti.ch format).
/// Generates daily XML files for overnight stays in Ticino properties (ADR-016).
/// </summary>
public interface ITicinoNotificationService
{
    /// <summary>
    /// Generates the daily police notification XML for a Ticino property.
    /// Returns the XML content and metadata for archiving.
    /// </summary>
    /// <param name="propertyId">Property in canton Ticino (TI).</param>
    /// <param name="reportDate">Report date in Europe/Zurich.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML content and file reference for archiving.</returns>
    Task<TicinoNotificationResult> GenerateDailyNotificationAsync(
        Guid propertyId,
        DateTime reportDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a property is in canton Ticino.
    /// </summary>
    Task<bool> IsPropertyInTicinoAsync(Guid propertyId);
}

/// <summary>Result of a Ticino notification generation.</summary>
public sealed record TicinoNotificationResult(
    string XmlContent,
    string Reference,
    string FileHash);
