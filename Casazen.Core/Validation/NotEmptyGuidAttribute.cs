using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Validation;

/// <summary>
/// An id sent in a request body must not be <see cref="Guid.Empty"/> (A4-18): a missing <c>Guid</c> binds to the empty
/// GUID, which must be refused as a malformed request (400 <c>validation_error</c>) instead of reaching the lookups.
/// <c>null</c> (an optional <c>Guid?</c> that was not sent) is valid.
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the error message
/// (<see cref="ValidationAttribute.ErrorMessage"/> is a SharedResources key), as <see cref="NotBlankWhenPresentAttribute"/>.
/// The check itself compares with <see cref="Guid.Empty"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotEmptyGuidAttribute : RegularExpressionAttribute
{
    public NotEmptyGuidAttribute()
        : base("^(?!00000000-0000-0000-0000-000000000000$).+$")
    {
    }

    public override bool IsValid(object? value) => value is not Guid id || id != Guid.Empty;
}
