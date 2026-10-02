using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The comune a supplier is looked for in: the ISTAT code when it is known and trusted (the one chosen for the property from
/// the official list), and the name as written otherwise (SU-04). With a code the supplier is matched by code; without one
/// the match stays what it was before the list, by the written name.
/// </summary>
/// <param name="IstatCode">Trusted ISTAT code (6 digits), or <c>null</c>.</param>
/// <param name="Name">The comune as written (the city of a property, a free-text filter), or <c>null</c>.</param>
public sealed record ComuneTarget(string? IstatCode, string? Name)
{
    /// <summary>The comune of a property: its chosen ISTAT code (if any) and its city.</summary>
    public static ComuneTarget ForProperty(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new ComuneTarget(property.ComuneIstatCode, property.City);
    }

    /// <summary>A value typed or sent by a client: six digits are an ISTAT code, anything else a name.</summary>
    public static ComuneTarget FromInput(string? value)
    {
        var trimmed = value?.Trim();
        return ComuneRules.IsIstatCode(trimmed) ? new ComuneTarget(trimmed, null) : new ComuneTarget(null, trimmed);
    }

    /// <summary>True when there is nothing to look up.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(IstatCode) && string.IsNullOrWhiteSpace(Name);
}
