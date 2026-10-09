using System.Globalization;
using System.Text;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The slug of a supplier's public showcase (SU-13, A4-16): the last part of <c>/fornitori/{slug}</c>. Generated once from
/// the business name when the profile is activated and then never changed (a link already shared keeps working when the
/// supplier renames the business). Lowercase ASCII letters, digits and single hyphens; unique (index
/// <c>UIX_SupplierProfiles_ShowcaseSlug</c>).
/// </summary>
public static class SupplierShowcaseSlug
{
    /// <summary>Longest slug of a name; a suffix for uniqueness (<c>-2</c>, <c>-a1b2</c>) stays within the 100-character column.</summary>
    public const int MaxBaseLength = 60;

    /// <summary>Slug of a name without a usable character (only symbols, or empty).</summary>
    public const string Fallback = "fornitore";

    /// <summary>
    /// <paramref name="name"/> as a slug: accents removed (<c>Pulizie Città</c> → <c>pulizie-citta</c>), anything that is not a
    /// letter or digit becomes one hyphen, no hyphen at the ends, cut at <see cref="MaxBaseLength"/> characters.
    /// <paramref name="fallback"/> (default <see cref="Fallback"/>) is the slug of a name without a usable character; the
    /// slugs of the supplier's services (SP-02) use the same rules with a fallback of their own.
    /// </summary>
    public static string FromName(string? name, string fallback = Fallback)
    {
        var decomposed = (name ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingHyphen = false;

        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0)
                    builder.Append('-');
                pendingHyphen = false;
                builder.Append(lower);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = builder.Length > MaxBaseLength ? builder.ToString(0, MaxBaseLength).TrimEnd('-') : builder.ToString();
        return slug.Length == 0 ? fallback : slug;
    }

    /// <summary>The form a slug from the address bar is looked up in: trimmed and lowercase.</summary>
    public static string Normalize(string? slug) => (slug ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// True when a <see cref="Normalize"/>d slug is worth a lookup (SP-09, the anonymous reads): not empty, not longer than
    /// <paramref name="maxLength"/> (the column) and without control characters. Anything else is a slug nobody has, and the
    /// answer is "not found" <b>without</b> asking the database: a NUL character, for one, cannot even be sent to PostgreSQL
    /// (error 22021, a 500 for a visitor who typed <c>%00</c>).
    /// </summary>
    public static bool IsLookupable(string normalized, int maxLength) =>
        normalized.Length > 0 && normalized.Length <= maxLength && !normalized.Any(char.IsControl);
}
