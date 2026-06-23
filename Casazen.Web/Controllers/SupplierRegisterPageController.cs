using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[AllowAnonymous]
[Route("/register")]
public class SupplierRegisterPageController : Controller
{
    private readonly IConfiguration _config;

    public SupplierRegisterPageController(IConfiguration config) => _config = config;

    [HttpGet("")]
    [Produces("text/html")]
    public IActionResult Index(
        [FromQuery] string inviteToken,
        [FromQuery] string email,
        [FromQuery] string comune)
    {
        var inviteTokenAttr = string.IsNullOrEmpty(inviteToken) ? "" : $"value=\"{WebUtility.HtmlEncode(inviteToken)}\"";
        var emailAttr = string.IsNullOrEmpty(email) ? "" : $"value=\"{WebUtility.HtmlEncode(email)}\"";
        var comuneAttr = string.IsNullOrEmpty(comune) ? "" : $"value=\"{WebUtility.HtmlEncode(comune)}\"";

        var publicSiteBaseUrl = (_config["App:PublicSiteBaseUrl"] ?? "").TrimEnd('/');
        var loginUrl = $"{publicSiteBaseUrl}/login";

        var html = $@"<!DOCTYPE html>
<html lang=""it"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>CasaZen — Registrazione Fornitore</title>
<style>
  *,*::before,*::after{{box-sizing:border-box;margin:0;padding:0}}
  body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:linear-gradient(135deg,#eff6ff,#e0e7ff);min-height:100vh;display:flex;align-items:center;justify-content:center;padding:1rem}}
  .card{{background:#fff;border-radius:12px;box-shadow:0 4px 24px rgba(0,0,0,.08);width:100%;max-width:440px;padding:2rem}}
  .icon{{width:64px;height:64px;margin:0 auto 1rem;border-radius:50%;background:#0d8abc;color:#fff;display:flex;align-items:center;justify-content:center;font-size:28px}}
  h1{{font-size:1.5rem;text-align:center;color:#18181b;margin-bottom:.5rem}}
  .desc{{text-align:center;color:#71717a;font-size:.875rem;margin-bottom:1.5rem}}
  .field{{margin-bottom:1rem}}
  label{{display:block;font-size:.875rem;font-weight:500;color:#3f3f46;margin-bottom:.375rem}}
  input{{width:100%;padding:.625rem .75rem;border:1px solid #d4d4d8;border-radius:8px;font-size:1rem;transition:border-color .15s}}
  input:focus{{outline:none;border-color:#0d8abc;box-shadow:0 0 0 3px rgba(13,138,188,.15)}}
  input:disabled{{background:#f4f4f5;color:#71717a}}
  .btn{{width:100%;padding:.75rem;background:#0d8abc;color:#fff;border:none;border-radius:8px;font-size:1rem;font-weight:600;cursor:pointer;transition:background .15s}}
  .btn:hover{{background:#0b7aa3}}
  .btn:disabled{{opacity:.6;cursor:not-allowed}}
  .error{{color:#dc2626;font-size:.875rem;margin-bottom:.75rem;display:none}}
  .error.show{{display:block}}
  .footer{{text-align:center;font-size:.75rem;color:#a1a1aa;margin-top:1rem}}
  .footer a{{color:#0d8abc}}
  .success-icon{{width:64px;height:64px;margin:0 auto 1rem;border-radius:50%;background:#16a34a;color:#fff;display:flex;align-items:center;justify-content:center;font-size:32px}}
  .spinner{{display:inline-block;width:1rem;height:1rem;border:2px solid rgba(255,255,255,.3);border-top-color:#fff;border-radius:50%;animation:spin .6s linear infinite;margin-right:.5rem;vertical-align:middle}}
  @keyframes spin{{to{{transform:rotate(360deg)}}}}
</style>
</head>
<body>
<div class=""card"" id=""form-card"">
  <div class=""icon"">&#127968;</div>
  <h1>Registrazione Fornitore</h1>
  <p class=""desc"">Completa i dati per attivare il tuo account fornitore su CasaZen.</p>
  <form id=""register-form"">
    <input type=""hidden"" name=""inviteToken"" {inviteTokenAttr} />
    <div class=""field"">
      <label for=""email"">Email</label>
      <input id=""email"" name=""email"" type=""email"" {emailAttr} disabled />
    </div>
    <div class=""field"">
      <label for=""comune"">Comune</label>
      <input id=""comune"" name=""comune"" {comuneAttr} disabled />
    </div>
    <div class=""field"">
      <label for=""legalName"">Ragione sociale</label>
      <input id=""legalName"" name=""legalName"" required maxlength=""300"" placeholder=""Es. Rossi Impianti S.r.l."" />
    </div>
    <div class=""field"">
      <label for=""phone"">Telefono</label>
      <input id=""phone"" name=""phone"" type=""tel"" required maxlength=""50"" placeholder=""+39 123 456 7890"" />
    </div>
    <p class=""error"" id=""error""></p>
    <button type=""submit"" class=""btn"" id=""submit-btn"">Completa registrazione</button>
  </form>
  <p class=""footer"">
    Hai gi&agrave; un account? <a href=""{WebUtility.HtmlEncode(publicSiteBaseUrl)}/login"">Accedi</a>
  </p>
</div>

<div class=""card"" id=""success-card"" style=""display:none;text-align:center"">
  <div class=""success-icon"">&#10003;</div>
  <h1>Registrazione completata</h1>
  <p class=""desc"">Il tuo profilo fornitore &egrave; stato creato. Ora accedi con la stessa email per entrare nella console.</p>
  <a href=""{WebUtility.HtmlEncode(loginUrl)}"" class=""btn"" style=""display:inline-block;text-decoration:none"">Accedi a CasaZen</a>
  <p class=""footer"" style=""margin-top:1rem"">Dopo l'accesso, completa l'attivazione del profilo dalla console fornitore.</p>
</div>

<script>
var form = document.getElementById('register-form');
var error = document.getElementById('error');
var btn = document.getElementById('submit-btn');
var formCard = document.getElementById('form-card');
var successCard = document.getElementById('success-card');

form.addEventListener('submit', async function(e) {{
  e.preventDefault();
  error.classList.remove('show');
  btn.disabled = true;
  btn.innerHTML = '<span class=""spinner""></span>Registrazione in corso...';

  var body = {{
    email: document.getElementById('email').value,
    legalName: document.getElementById('legalName').value.trim(),
    phone: document.getElementById('phone').value.trim(),
    comuneCode: document.getElementById('comune').value
  }};
  var token = document.querySelector('input[name=""inviteToken""]').value;
  if (token) body.inviteToken = token;

  try {{
    var res = await fetch('/api/suppliers/register', {{
      method: 'POST',
      headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify(body)
    }});
    if (!res.ok) {{
      var errData = await res.json().catch(function(){{ return null; }});
      throw new Error((errData && errData.error) || (errData && errData.title) || 'Errore del server. Riprova.');
    }}
    formCard.style.display = 'none';
    successCard.style.display = '';
  }} catch (err) {{
    error.textContent = err.message || 'Errore di rete. Riprova.';
    error.classList.add('show');
  }} finally {{
    btn.disabled = false;
    btn.textContent = 'Completa registrazione';
  }}
}});

</script>
</body>
</html>";

        return Content(html, "text/html; charset=utf-8");
    }
}
