namespace Casazen.Core.Services;

/// <summary>
/// The pilot comuni of the supplier self-serve registration (<c>Suppliers:PilotComuni</c>), validated against the official
/// ISTAT list (SU-04): a pilot is identified by its ISTAT code, its name is the one of the list, and a pilot that is not in the
/// list (a typo in the configuration) is not offered and is logged. Until the list is imported the pilots are those written in
/// the configuration, unvalidated.
/// </summary>
public interface ISupplierPilotComuni
{
    /// <summary>The pilot comuni offered to a supplier registering without an invite.</summary>
    Task<IReadOnlyList<SupplierPilot>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The pilot comune a <paramref name="code"/> stands for (its ISTAT code, or the code written in the configuration, or a
    /// cadastral code of the same comune), or <c>null</c> when it is not a pilot.
    /// </summary>
    Task<SupplierPilot?> FindAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>True when at least one pilot comune is offered: without one the registration is by invite only.</summary>
    Task<bool> IsSelfServeEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Configured pilot comuni that cannot be used because they are not in the official list (for the health check).</summary>
    Task<IReadOnlyList<string>> GetInvalidConfiguredCodesAsync(CancellationToken cancellationToken = default);
}

/// <param name="Code">ISTAT code when the list validated it, otherwise the code as configured.</param>
/// <param name="Name">Name of the comune (the one of the list when validated).</param>
/// <param name="Validated">True when the code was found in the official list.</param>
public sealed record SupplierPilot(string Code, string Name, bool Validated);
