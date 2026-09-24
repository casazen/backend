using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Validation;

/// <summary>
/// For the optional fields of a request with PATCH semantics (A2-04): <c>null</c> means "not sent, keep the stored
/// value" and is valid, but a value that is sent must not be empty or whitespace (e.g. the name of a property).
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the error
/// message (<see cref="ValidationAttribute.ErrorMessage"/> is a SharedResources key), as <see cref="CinCodeAttribute"/>.
/// The check itself is <see cref="string.IsNullOrWhiteSpace(string)"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotBlankWhenPresentAttribute : RegularExpressionAttribute
{
    public NotBlankWhenPresentAttribute()
        : base(@"\S")
    {
    }

    public override bool IsValid(object? value) => value is null || !string.IsNullOrWhiteSpace(value.ToString());
}
