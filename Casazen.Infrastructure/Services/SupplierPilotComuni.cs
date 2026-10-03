using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierPilotComuni" />
public class SupplierPilotComuni(
    IOptions<SupplierRegistrationOptions> options,
    IComuneDirectory directory) : ISupplierPilotComuni
{
    public async Task<IReadOnlyList<SupplierPilot>> GetAsync(CancellationToken cancellationToken = default)
    {
        var (pilots, _) = await EvaluateAsync(cancellationToken);
        return pilots;
    }

    public async Task<IReadOnlyList<string>> GetInvalidConfiguredCodesAsync(CancellationToken cancellationToken = default)
    {
        var (_, invalid) = await EvaluateAsync(cancellationToken);
        return invalid;
    }

    public async Task<bool> IsSelfServeEnabledAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).Count > 0;

    public async Task<SupplierPilot?> FindAsync(string? code, CancellationToken cancellationToken = default)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        var pilots = await GetAsync(cancellationToken);
        var direct = pilots.FirstOrDefault(p => p.Code.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (direct is not null || !pilots.Any(p => p.Validated))
            return direct;

        // A cadastral code (H501) or a name of a validated pilot: the same comune of the list.
        var resolved = await directory.ResolveAsync([trimmed], cancellationToken);
        return resolved.TryGetValue(trimmed, out var comune)
            ? pilots.FirstOrDefault(p => p.Code == comune.IstatCode)
            : null;
    }

    private async Task<(IReadOnlyList<SupplierPilot> Pilots, IReadOnlyList<string> Invalid)> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        var configured = options.Value.PilotComuni;
        if (configured.Count == 0)
            return ([], []);

        if (!await directory.IsAvailableAsync(cancellationToken))
        {
            // The list is not imported yet: the pilots are as configured, nothing can be validated.
            return (configured.Select(c => new SupplierPilot(c.Code.Trim(), c.Name.Trim(), Validated: false)).ToList(), []);
        }

        var resolved = await directory.ResolveAsync(configured.Select(c => c.Code), cancellationToken);
        var pilots = new List<SupplierPilot>();
        var invalid = new List<string>();
        foreach (var pilot in configured)
        {
            var code = pilot.Code.Trim();
            if (resolved.TryGetValue(code, out var comune) && comune.IsActive)
            {
                if (!pilots.Any(p => p.Code == comune.IstatCode))
                    pilots.Add(new SupplierPilot(comune.IstatCode, comune.Name, Validated: true));
            }
            else
            {
                // Not offered; the health check and the startup log name it (a request would only repeat the warning).
                invalid.Add(code);
            }
        }

        return (pilots, invalid);
    }
}
