using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Validation;

/// <summary>
/// The value must be an IANA time zone known to the server (e.g. <c>Europe/Rome</c>): the property timezone is used
/// for booking dates and stay alerts, and an unknown or Windows id would make them fail later (A2-04). Null is valid
/// (field not sent).
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the error
/// message (<see cref="ValidationAttribute.ErrorMessage"/> is a SharedResources key), as <see cref="CinCodeAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class IanaTimeZoneAttribute : RegularExpressionAttribute
{
    public IanaTimeZoneAttribute()
        : base(@"^[A-Za-z0-9_+\-/]+$")
    {
    }

    public override bool IsValid(object? value) => value is null || IsKnownIanaId(value.ToString()?.Trim());

    /// <summary>True for an IANA id the server can resolve; false for blanks, Windows ids and unknown names.</summary>
    public static bool IsKnownIanaId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
        && zone.HasIanaId;
}
