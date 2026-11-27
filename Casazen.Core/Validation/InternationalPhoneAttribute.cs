using System.ComponentModel.DataAnnotations;
using Casazen.Core.Leases;

namespace Casazen.Core.Validation;

/// <summary>
/// Optional international phone (<see cref="LeaseDraftRules.IsInternationalPhone"/>): a leading <c>+</c>, separators
/// allowed, and 8 to 15 digits not starting with 0 once those separators are dropped. Null is valid.
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the error
/// message: <see cref="ValidationAttribute.ErrorMessage"/> is the SharedResources key <c>InvalidPhone</c>. The check
/// itself is <see cref="LeaseDraftRules.IsInternationalPhone"/>, the same rule as the lease wizard.
/// </remarks>
public sealed class InternationalPhoneAttribute : RegularExpressionAttribute
{
    public const string MessageKey = "InvalidPhone";

    public InternationalPhoneAttribute()
        : base(@"^\+[0-9\s().-]+$")
    {
        ErrorMessage = MessageKey;
    }

    public override bool IsValid(object? value) =>
        value is null || (value is string phone && LeaseDraftRules.IsInternationalPhone(phone));
}
