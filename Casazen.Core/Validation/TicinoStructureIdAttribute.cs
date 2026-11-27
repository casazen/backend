using System.ComponentModel.DataAnnotations;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Validation;

/// <summary>
/// Validates the identifier of a structure on the Ticino police portal with <see cref="TicinoStructureId"/> (casazen/backend#979): spaces
/// are ignored, case-insensitive, 3 to 20 letters and digits with single hyphens, e.g. <c>NL-12345678</c>. Null or blank is valid (the
/// identifier is optional on input; the police file asks for it).
/// </summary>
/// <remarks>
/// Derives from <see cref="RegularExpressionAttribute"/> only so that MVC's built-in adapter localizes the error message (the SharedResources
/// key <see cref="TicinoStructureId.InvalidFormatMessageKey"/>), like <see cref="CinCodeAttribute"/>.
/// </remarks>
public sealed class TicinoStructureIdAttribute : RegularExpressionAttribute
{
    public TicinoStructureIdAttribute()
        : base(TicinoStructureId.Pattern)
    {
        ErrorMessage = TicinoStructureId.InvalidFormatMessageKey;
    }

    public override bool IsValid(object? value)
    {
        var text = value?.ToString();
        return TicinoStructureId.Normalize(text) is null || TicinoStructureId.IsValid(text);
    }
}
