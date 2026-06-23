using System.Net;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.External;

public static class SupplierInviteEmailBuilder
{
    public static (string Subject, string HtmlContent) Build(
        SupplierInviteRecord invite,
        string signupUrl,
        DateTime expiresAtUtc)
    {
        var subject = "Invito CasaZen — Console fornitore";
        var expiresLabel = expiresAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var customMessage = string.IsNullOrWhiteSpace(invite.Message)
            ? string.Empty
            : $"""
               <p style="margin:16px 0;padding:12px;background:#f4f4f5;border-radius:8px;">
                 <strong>Messaggio dal team CasaZen:</strong><br />
                 {WebUtility.HtmlEncode(invite.Message)}
               </p>
               """;

        var html = $"""
            <!DOCTYPE html>
            <html lang="it">
            <body style="font-family:Arial,sans-serif;color:#18181b;line-height:1.5;">
              <h1 style="font-size:20px;">Sei stato invitato su CasaZen</h1>
              <p>
                CasaZen ti invita a registrarti come <strong>fornitore</strong>
                per il comune <strong>{WebUtility.HtmlEncode(invite.ComuneCode)}</strong>.
              </p>
              {customMessage}
              <p>
                <a href="{WebUtility.HtmlEncode(signupUrl)}"
                   style="display:inline-block;padding:12px 20px;background:#0d8abc;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;">
                  Accetta l'invito e registrati
                </a>
              </p>
              <p style="font-size:13px;color:#71717a;">
                Se il pulsante non funziona, copia e incolla questo link nel browser:<br />
                <a href="{WebUtility.HtmlEncode(signupUrl)}">{WebUtility.HtmlEncode(signupUrl)}</a>
              </p>
              <p style="font-size:13px;color:#71717a;">
                L'invito scade il <strong>{expiresLabel}</strong>.
                Dopo la registrazione su Auth0 completa l'attivazione dalla console fornitore.
              </p>
              <p style="font-size:12px;color:#a1a1aa;">CasaZen — gestione affitti brevi</p>
            </body>
            </html>
            """;

        return (subject, html);
    }

    public static string BuildSignupUrl(string publicSiteBaseUrl, SupplierInviteRecord invite) =>
        $"{publicSiteBaseUrl.TrimEnd('/')}/register?inviteToken={invite.Id}&email={Uri.EscapeDataString(invite.Email)}&comune={Uri.EscapeDataString(invite.ComuneCode)}";
}
