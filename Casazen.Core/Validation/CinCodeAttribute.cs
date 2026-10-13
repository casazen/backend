using System.ComponentModel.DataAnnotations;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Validation;

/// <summary>
/// Validates the CIN (Codice Identificativo Nazionale, D.L. 145/2023) with <see cref="CinFormat"/>: spaces and
/// hyphens are ignored, case-insensitive, e.g. <c>IT058091C27G5FFZDZ</c>. Null or blank is valid (the CIN is
/// optional on input).
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the
/// error message: <see cref="ValidationAttribute.ErrorMessage"/> is the SharedResources key
/// <see cref="CinFormat.InvalidFormatMessageKey"/>. The check itself is <see cref="CinFormat.IsValid"/> on the
/// normalized value, not the raw regex match.
/// </remarks>
public class CinCodeAttribute : RegularExpressionAttribute
{
    public CinCodeAttribute()
        : base(CinFormat.Pattern)
    {
        ErrorMessage = CinFormat.InvalidFormatMessageKey;
    }

    public override bool IsValid(object? value)
    {
        var text = value?.ToString();
        return CinFormat.Normalize(text) is null || CinFormat.IsValid(text);
    }
}
