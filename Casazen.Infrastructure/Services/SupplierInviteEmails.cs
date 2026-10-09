using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The supplier invite email, shared by the creation of an invite (<see cref="SupplierService"/>) and its resending
/// by an admin (<see cref="SupplierAdminService"/>, SU-12), so both send the same text.
/// </summary>
internal static class SupplierInviteEmails
{
    /// <summary>
    /// The invite email for <paramref name="token"/> (the secret of the link, only ever sent by email). Rendered
    /// before the invite is saved: a missing <c>App:PublicSiteBaseUrl</c> is a configuration error, not an invite with
    /// a wrong link.
    /// </summary>
    public static EmailContent Build(
        PublicSiteLinks publicSiteLinks,
        SupplierInviteRecord invite,
        string token,
        string? comuneName) =>
        EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture,
            invite.Email,
            DescribeComune(invite.ComuneCode, comuneName),
            invite.Message,
            publicSiteLinks.SupplierInviteSignup(token),
            invite.ExpiresAt);

    /// <summary>"Name (code)" when the name of the comune is known, otherwise the code.</summary>
    public static string DescribeComune(string comuneCode, string? name)
    {
        var code = comuneCode.Trim();
        return string.IsNullOrWhiteSpace(name) ? code : $"{name.Trim()} ({code})";
    }

    /// <summary>
    /// Name of the comune of an invite code: the pilot comune or the comune of the official list (SU-04); <c>null</c> when it
    /// is neither (the code is then shown as it is). <c>ItalianComuneRegistry</c> is gone: it knew 12 comuni and mapped F205 to
    /// Firenze while F205 is Milano (A4-12).
    /// </summary>
    public static async Task<string?> ResolveComuneNameAsync(
        ISupplierPilotComuni pilotComuni, IComuneDirectory comuneDirectory, string comuneCode, CancellationToken cancellationToken)
    {
        var code = comuneCode.Trim();
        if (await pilotComuni.FindAsync(code, cancellationToken) is { Validated: false } unvalidated)
            return unvalidated.Name;

        var resolved = await comuneDirectory.ResolveAsync([code], cancellationToken);
        return resolved.TryGetValue(code, out var comune) ? comune.Name : null;
    }
}
