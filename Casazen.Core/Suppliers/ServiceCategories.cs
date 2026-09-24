using System.Collections.Frozen;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Single source of truth for the service categories of the supplier marketplace (SU-03, A4-05 / A6-03): what a
/// supplier offers (<c>SupplierProfile.CategoriesJson</c>, admin invites) and what a host asks for
/// (<c>ServiceRequest.Category</c>, supplier search and match). Only these stable lowercase English codes are stored
/// and compared; web and app read the list from <c>GET /api/service-categories</c> and translate
/// <c>serviceRequest.categories.&lt;code&gt;</c> (web) and the app's own it/en labels.
/// </summary>
/// <remarks>
/// The list is the union of the categories the three clients already offered before SU-03: host web
/// (<c>cleaning</c>, <c>maintenance</c>, <c>plumbing</c>, <c>laundry</c>), app (<c>cleaning</c>, <c>maintenance</c>,
/// <c>linen</c>, <c>check-in</c>) and the supplier wizard, which saved Italian labels (Pulizie, Manutenzione,
/// Giardinaggio, Eventi, Noleggio, Escursioni; the migration <c>NormalizeServiceCategories</c> converts them). Doubtful
/// synonyms stay distinct: <c>laundry</c> and <c>linen</c> are two categories, <c>check-in</c> is its own category.
/// Never rename a code: it is stored in the database and used as an i18n key by the clients.
/// </remarks>
public static class ServiceCategories
{
    public const string Cleaning = "cleaning";
    public const string Maintenance = "maintenance";
    public const string Plumbing = "plumbing";
    public const string Laundry = "laundry";
    public const string Linen = "linen";
    public const string CheckIn = "check-in";
    public const string Gardening = "gardening";
    public const string Events = "events";
    public const string Rental = "rental";
    public const string Excursions = "excursions";

    /// <summary>Stable error code (API <c>code</c>, HTTP 422) for a category that is not one of <see cref="All"/>.</summary>
    public const string InvalidCategoryCode = "invalid_service_category";

    /// <summary>SharedResources key of the "invalid service category" message.</summary>
    public const string InvalidCategoryMessageKey = "ServiceCategoryInvalid";

    /// <summary>Longest part of a rejected value echoed back in the error message.</summary>
    private const int MaxEchoedLength = 50;

    /// <summary>Every category code, in the order the clients show them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Cleaning,
        Maintenance,
        Plumbing,
        Laundry,
        Linen,
        CheckIn,
        Gardening,
        Events,
        Rental,
        Excursions,
    ];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Form to validate and store: trimmed, lower case (invariant culture). Returns <c>null</c> when nothing is left.
    /// It does not translate labels: <c>Pulizie</c> stays <c>pulizie</c> and is rejected.
    /// </summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>True when <paramref name="code"/> is exactly one of <see cref="All"/> (already normalized).</summary>
    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);

    /// <summary>
    /// Normalized code of <paramref name="value"/>, or a <see cref="DomainRuleException"/> (HTTP 422,
    /// <see cref="InvalidCategoryCode"/>) when it is empty or not a known category.
    /// </summary>
    public static string Require(string? value)
    {
        var code = Normalize(value);
        if (code is not null && Known.Contains(code))
            return code;

        var echoed = (value ?? string.Empty).Trim();
        if (echoed.Length > MaxEchoedLength)
            echoed = echoed[..MaxEchoedLength];
        throw new DomainRuleException(InvalidCategoryCode, InvalidCategoryMessageKey, echoed);
    }

    /// <summary>
    /// Normalized, de-duplicated codes (first occurrence order), or a <see cref="DomainRuleException"/> for the first
    /// value that is not a known category.
    /// </summary>
    public static IReadOnlyList<string> RequireAll(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(Require).Distinct(StringComparer.Ordinal).ToList();
    }
}
