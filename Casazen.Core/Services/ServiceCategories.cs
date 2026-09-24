namespace Casazen.Core.Services;

/// <summary>
/// Service request categories the host can pick (web form <c>service-request-form.tsx</c>). The AI paths accept only
/// these codes (A8-01): free text never reaches a prompt or a cache key.
/// </summary>
public static class ServiceCategories
{
    public const string Cleaning = "cleaning";
    public const string Maintenance = "maintenance";
    public const string Plumbing = "plumbing";
    public const string Laundry = "laundry";

    public static IReadOnlyList<string> All { get; } = [Cleaning, Maintenance, Plumbing, Laundry];

    /// <summary>True for one of <see cref="All"/> (exact, lowercase code).</summary>
    public static bool IsKnown(string? category) => category is not null && All.Contains(category, StringComparer.Ordinal);
}
