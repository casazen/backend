namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The part of the product an entry of the activity log belongs to (<see cref="OrgActivityEntry.Area"/>, AM-02b). The codes
/// the API speaks (<c>account</c>, <c>short-rent</c>, <c>long-rent</c>, <c>supplier</c>) are the keys of the contexts of the
/// web app, so the client filters and labels without a table of its own (<c>OrgActivityCatalog.AreaCode</c>). Persisted as
/// int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgActivityArea
{
    /// <summary>The «Amministrazione» of the org: people, plan, organization.</summary>
    Account = 1,

    /// <summary>Short-term rentals.</summary>
    ShortRent = 2,

    /// <summary>Long-term rentals.</summary>
    LongRent = 3,

    /// <summary>The suppliers of the org.</summary>
    Supplier = 4,
}
