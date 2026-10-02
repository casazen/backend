using Casazen.Core.Entities;
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
        SupplierRegistrationOptions options,
        SupplierInviteRecord invite,
        string token) =>
        EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture,
            invite.Email,
            DescribeComune(options, invite.ComuneCode),
            invite.Message,
            publicSiteLinks.SupplierInviteSignup(token),
            invite.ExpiresAt);

    /// <summary>
    /// "Name (code)" when the comune is a configured pilot comune, otherwise the code. <c>ItalianComuneRegistry</c> is
    /// not used: it knows 12 comuni and maps F205 to Firenze while F205 is Milano (A4-12, SU-04).
    /// </summary>
    public static string DescribeComune(SupplierRegistrationOptions options, string comuneCode)
    {
        var code = comuneCode.Trim();
        var name = options.FindPilotComune(code)?.Name.Trim();
        return string.IsNullOrEmpty(name) ? code : $"{name} ({code})";
    }
}
