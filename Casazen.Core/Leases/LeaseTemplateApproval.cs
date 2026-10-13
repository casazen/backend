using System.Text.RegularExpressions;
using Casazen.Core.Options;

namespace Casazen.Core.Leases;

/// <summary>
/// Rules of the approval of a lease contract template (LT-03, A7-03). <c>Approved=true</c> counts only with a real
/// version (not empty, not <see cref="LeaseTemplateVariantOptions.DevStubVersionId"/>) and evidence of the approval
/// (reference or date). The template itself must also be complete: see <c>LeaseContractTemplateCatalog</c>.
/// </summary>
public static partial class LeaseTemplateApproval
{
    /// <summary>A version is also a file name: letters, digits, dot, dash and underscore, starting with a letter or digit.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdPattern();

    public static bool IsDevStub(string? versionId) =>
        string.Equals(versionId?.Trim(), LeaseTemplateVariantOptions.DevStubVersionId, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidVersionId(string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId)
        && VersionIdPattern().IsMatch(versionId.Trim())
        && !versionId.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Why a declared approval (<c>Approved=true</c>) is not valid; empty when it is valid or when the variant is not
    /// declared approved at all.
    /// </summary>
    public static IReadOnlyList<string> GetInvalidApprovalReasons(LeaseTemplateVariantOptions? variant)
    {
        if (variant is null || !variant.Approved)
            return [];

        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(variant.VersionId))
            reasons.Add("VersionId is empty");
        else if (IsDevStub(variant.VersionId))
            reasons.Add($"VersionId '{LeaseTemplateVariantOptions.DevStubVersionId}' is not an approved version");
        else if (!IsValidVersionId(variant.VersionId))
            reasons.Add("VersionId is not a valid file name");

        if (string.IsNullOrWhiteSpace(variant.ApprovalReference) && variant.ApprovedAt is null)
            reasons.Add("ApprovalReference or ApprovedAt is required");

        return reasons;
    }
}
