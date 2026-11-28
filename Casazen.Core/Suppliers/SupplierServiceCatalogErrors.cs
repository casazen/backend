using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the
/// errors of the supplier's service catalog (SP-02). 404 <see cref="NotFound"/>, 409 <see cref="Changed"/>, 422 every
/// other one. The codes are snake_case and never renamed: the frontend branches on them.
/// </summary>
public static class SupplierServiceCatalogErrors
{
    public const string NotFound = "supplier_service_not_found";

    public const string Invalid = "supplier_service_invalid";

    public const string NotPublishable = "supplier_service_not_publishable";

    public const string LimitReached = "supplier_service_limit_reached";

    public const string CannotPause = "supplier_service_cannot_pause";

    /// <summary>409: the service was changed since the client read it (stale <c>version</c>, or a concurrent change).</summary>
    public const string Changed = "supplier_service_changed";

    public const string PhotoNone = "supplier_service_photo_none";

    public const string PhotoInvalidType = "supplier_service_photo_invalid_type";

    public const string PhotoInvalidSize = "supplier_service_photo_invalid_size";

    public const string PhotoLimitReached = "supplier_service_photo_limit_reached";

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

    public static NotFoundException ServiceNotFound(Guid id) =>
        new($"Supplier service {id} not found")
        {
            Code = NotFound,
            MessageKey = "SupplierServiceNotFound",
        };

    public static SupplierServiceRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierServiceInvalid", fields);

    public static SupplierServiceRuleException NotPublishableFields(IReadOnlyList<string> missing) =>
        new(NotPublishable, "SupplierServiceNotPublishable", missing);

    public static DomainConflictException ServiceChanged() =>
        new DomainConflictException(Changed, "SupplierServiceChanged");
}

public sealed class SupplierServiceRuleException : DomainRuleException
{
    public SupplierServiceRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    public IReadOnlyList<string> Fields { get; }
}
