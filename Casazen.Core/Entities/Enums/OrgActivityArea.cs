namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The part of the product an entry of the activity log belongs to (<see cref="OrgActivityEntry.Area"/>, AM-02b). The codes
/// the API speaks (<c>account</c>, <c>short-rent</c>, <c>long-rent</c>, <c>supplier</c>) are the keys of the contexts of the
/// web app, so the client filters and labels without a table of its own (<c>OrgActivityCatalog.AreaCode</c>). Persisted as
/// int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgActivityArea
{
    Account = 1,

    ShortRent = 2,

    LongRent = 3,

    Supplier = 4,
}
