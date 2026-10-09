using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the
/// errors of the supplier's service catalog (SP-02). 404 <see cref="NotFound"/>, 409 <see cref="Changed"/>, 422 every
/// other one. The codes are snake_case and never renamed: the frontend branches on them.
/// </summary>
public static class SupplierServiceCatalogErrors
{
    /// <summary>404: no service with that id in the caller's catalog (another supplier's service, or a deleted one, answers the same).</summary>
    public const string NotFound = "supplier_service_not_found";

    /// <summary>
    /// 422: a value is not valid (<see cref="SupplierServiceRuleException.Fields"/> names them, also in the response as
    /// <c>fields</c>). The message arguments are the field names.
    /// </summary>
    public const string Invalid = "supplier_service_invalid";

    /// <summary>
    /// 422: the service cannot be published (or, while published, cannot be saved like this): it lacks a name, a category,
    /// a duration, or a price or the quote flag. <see cref="SupplierServiceRuleException.Fields"/> lists what is missing.
    /// </summary>
    public const string NotPublishable = "supplier_service_not_publishable";

    /// <summary>422: the supplier already has <see cref="SupplierServiceCatalogLimits.MaxServicesPerSupplier"/> services.</summary>
    public const string LimitReached = "supplier_service_limit_reached";

    /// <summary>422: only a published service can be paused.</summary>
    public const string CannotPause = "supplier_service_cannot_pause";

    /// <summary>409: the service was changed since the client read it (stale <c>version</c>, or a concurrent change).</summary>
    public const string Changed = "supplier_service_changed";

    /// <summary>422: the photo upload carries no file.</summary>
    public const string PhotoNone = "supplier_service_photo_none";

    /// <summary>422: not a JPEG, PNG or WebP image (extension, declared type or content).</summary>
    public const string PhotoInvalidType = "supplier_service_photo_invalid_type";

    /// <summary>422: empty file or larger than <see cref="SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes"/>.</summary>
    public const string PhotoInvalidSize = "supplier_service_photo_invalid_size";

    /// <summary>422: the service would exceed <see cref="SupplierServiceCatalogLimits.MaxPhotos"/> photos.</summary>
    public const string PhotoLimitReached = "supplier_service_photo_limit_reached";

    /// <summary>
    /// Every <c>SharedResources</c> key the catalog uses (a test checks that each one exists in Italian and English);
    /// <c>SupplierServiceCopySuffix</c> is the text added to the name of a duplicated service.
    /// </summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "SupplierServiceNotFound",
        "SupplierServiceInvalid",
        "SupplierServiceNotPublishable",
        "SupplierServiceLimitReached",
        "SupplierServiceCannotPause",
        "SupplierServiceChanged",
        "SupplierServicePhotoNone",
        "SupplierServicePhotoInvalidType",
        "SupplierServicePhotoInvalidSize",
        "SupplierServicePhotoLimitReached",
        "SupplierServiceCopySuffix",
    ];

    /// <summary>The 404 of a service that is not in the supplier's catalog.</summary>
    public static NotFoundException ServiceNotFound(Guid id) =>
        new($"Supplier service {id} not found")
        {
            Code = NotFound,
            MessageKey = "SupplierServiceNotFound",
        };

    /// <summary>The 422 of one or more invalid values (<paramref name="fields"/>: JSON names, e.g. <c>name</c>, <c>supplements[1].per</c>).</summary>
    public static SupplierServiceRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierServiceInvalid", fields);

    /// <summary>The 422 of a service that cannot be published; <paramref name="missing"/> are the fields it lacks.</summary>
    public static SupplierServiceRuleException NotPublishableFields(IReadOnlyList<string> missing) =>
        new(NotPublishable, "SupplierServiceNotPublishable", missing);

    /// <summary>The 409 of a change made on a stale copy of the service.</summary>
    public static DomainConflictException ServiceChanged() =>
        new DomainConflictException(Changed, "SupplierServiceChanged");
}

/// <summary>
/// A 422 of the catalog rules that names the fields at fault: the controller adds them to the response as
/// <c>fields</c>, so the form can mark them. Anything that does not catch it gets the plain 422 with code and message from
/// the error middleware.
/// </summary>
public sealed class SupplierServiceRuleException : DomainRuleException
{
    public SupplierServiceRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    /// <summary>JSON names of the fields at fault (<c>name</c>, <c>durationMinutes</c>, <c>supplements[0].amountCents</c>...).</summary>
    public IReadOnlyList<string> Fields { get; }
}
