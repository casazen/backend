namespace Casazen.Web.Infrastructure;

/// <summary>
/// Generic values of the <c>code</c> extension that every API error response carries (see
/// <see cref="ApiProblemDetails"/>). Feature-specific codes (e.g. <c>duplicate_property_slug</c>) are
/// declared where the error is raised. Codes are snake_case and stable: the frontend translates them
/// (<c>apiErrors.codes.*</c>) and branches on them, so never rename one.
/// </summary>
public static class ProblemCodes
{
    public const string BadRequest = "bad_request";
    public const string ValidationError = "validation_error";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string Conflict = "conflict";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string BusinessRuleViolation = "business_rule_violation";
    public const string TooManyRequests = "too_many_requests";
    public const string ClientError = "client_error";
    public const string InternalError = "internal_error";
    public const string PaymentProviderError = "payment_provider_error";
    public const string ServiceUnavailable = "service_unavailable";
    public const string ServerError = "server_error";

    /// <summary>Default code for a status code when the error has no more specific one.</summary>
    public static string ForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => BadRequest,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status405MethodNotAllowed => MethodNotAllowed,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity => BusinessRuleViolation,
        StatusCodes.Status429TooManyRequests => TooManyRequests,
        StatusCodes.Status500InternalServerError => InternalError,
        StatusCodes.Status503ServiceUnavailable => ServiceUnavailable,
        >= 500 => ServerError,
        _ => ClientError,
    };
}
