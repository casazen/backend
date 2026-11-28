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
/// <c>electrical</c> was added by SP-02 (the redesign of the supplier console offers "Elettricista").
/// Never rename a code: it is stored in the database and used as an i18n key by the clients. The AI paths accept only
/// these codes too (FD-21, A8-01): free text never reaches a prompt or a cache key.
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

    /// <summary>
    /// Electrician (SP-02): the demo of the supplier console offers it and the backend did not have it. Added last, because
    /// the order of <see cref="All"/> is the order the clients show; the clients add its label
    /// (<c>serviceRequest.categories.electrical</c>, <c>EmailTexts</c> <c>ServiceCategory_electrical</c>).
    /// </summary>
    public const string Electrical = "electrical";

    public const string InvalidCategoryCode = "invalid_service_category";

    public const string InvalidCategoryMessageKey = "ServiceCategoryInvalid";

    private const int MaxEchoedLength = 50;

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
        Electrical,
    ];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Form to validate and store: trimmed, lower case (invariant culture). Returns <c>null</c> when nothing is left.
    /// It does not translate labels: <c>Pulizie</c> stays <c>pulizie</c> and is rejected.
    /// </summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);

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

    public static IReadOnlyList<string> RequireAll(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(Require).Distinct(StringComparer.Ordinal).ToList();
    }
}
