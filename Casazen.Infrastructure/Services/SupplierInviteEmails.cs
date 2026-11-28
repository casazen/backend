using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;

namespace Casazen.Infrastructure.Services;

internal static class SupplierInviteEmails
{
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
