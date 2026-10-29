using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Generates HMAC-SHA256 signed links to the ConcordatoFacile partner portal (ADR-007).
/// Links expire in 15 minutes and include lease identification and municipality data.
/// The link is made only for a lease whose current privacy notice was accepted (CF-BE): the platform admin's bypass is about the payment,
/// not about the privacy. The clock can be passed, so that the vector of the specification is checked on this very service.
/// </summary>
public sealed class ConcordatoFacileLinkService(
    AppDbContext db,
    IConcordatoFacileAccessService accessService,
    IConcordatoFacilePrivacyService privacyService,
    IOptions<ConcordatoFacileOptions> options,
    ILogger<ConcordatoFacileLinkService> logger,
    TimeProvider? timeProvider = null) : IConcordatoFacileLinkService
{
    private readonly byte[] _secretKey = GetSecretKey(options);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static byte[] GetSecretKey(IOptions<ConcordatoFacileOptions> options)
    {
        var secret = Environment.GetEnvironmentVariable("CONCORDATOFACILE_LINK_SECRET")
            ?? options.Value.LinkSecret;

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "ConcordatoFacile link secret is not configured. " +
                "Set CONCORDATOFACILE_LINK_SECRET environment variable or ConcordatoFacile:LinkSecret in configuration.");
        }

        return Encoding.UTF8.GetBytes(secret);
    }

    public async Task<CfLinkResult> GenerateSignedLinkAsync(
        Guid leaseId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        // ADR-007-R10: Verify access via IConcordatoFacileAccessService
        var accessResult = await accessService.CheckAccessAsync(leaseId, user, cancellationToken);
        if (!accessResult.HasAccess)
        {
            throw new UnauthorizedAccessException(
                $"User does not have ConcordatoFacile access for lease {leaseId}.");
        }

        // CF-BE: the form opens only after the current privacy notice is accepted for the lease, for the admin too. 409
        // concordatofacile_privacy_not_accepted; nothing is signed and no event is written before this point.
        await privacyService.EnsureAcceptedAsync(leaseId, cancellationToken);

        // Load lease with property
        var lease = await db.LeaseContracts
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);

        if (lease is null)
        {
            throw new DomainRuleException("lease_not_found", "LeaseNotFound");
        }

        // ADR-007-R7: Retrieve municipality from Comuni table via Property.ComuneIstatCode
        if (string.IsNullOrWhiteSpace(lease.Property.ComuneIstatCode))
        {
            throw new DomainRuleException(
                "property_municipality_unknown",
                "PropertyMunicipalityNotSet");
        }

        var comune = await db.Comuni
            .Where(c => c.IstatCode == lease.Property.ComuneIstatCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (comune is null)
        {
            throw new DomainRuleException(
                "property_municipality_unknown",
                "PropertyMunicipalityNotInIstatList");
        }

        // ADR-007-R1: Calculate expiresAt = UtcNow + 15 minutes, format yyyy-MM-ddTHH:mm:ssZ
        var now = _clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddMinutes(15).ToString("yyyy-MM-ddTHH:mm:ssZ");

        // ADR-007-R1, ADR-007-R2: Compute HMAC-SHA256 signature
        var signature = ComputeHmacSignature(leaseId, expiresAt);

        // ADR-007-R6: Build URL with URL-encoded parameters (encoding AFTER signature calculation)
        var url = BuildSignedUrl(
            FormBaseUrl(options.Value), leaseId, expiresAt, signature, comune.Name, comune.ProvinceCode, TestModuleBypassSecret(options.Value));

        // ADR-007-R8: Record LeaseEvent (NO URL or signature in payload)
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.ConcordatoFacileLinkGenerated,
            OccurredAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        // ADR-007-R5: Log generation without secret, signature, or full URL. The caller is named by the subject of its token (an opaque
        // id), never by its display name, which can be an e-mail address (CF-BE: no personal data in the logs).
        logger.LogInformation(
            "Generated ConcordatoFacile link for lease {LeaseId} by user {UserId}",
            leaseId,
            user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");

        return new CfLinkResult(url);
    }

    /// <summary>
    /// Computes HMAC-SHA256 signature per ADR-007-R1 and ADR-007-R2.
    /// Input: leaseId.ToLowerInvariant() + "." + expiresAt (pre-URL-encoding).
    /// Output: hex lowercase.
    /// </summary>
    private string ComputeHmacSignature(Guid leaseId, string expiresAt)
    {
        var input = $"{leaseId.ToString().ToLowerInvariant()}.{expiresAt}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hash = HMACSHA256.HashData(_secretKey, inputBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The real module of ConcordatoFacile (specification v0.2 §2: "Sempre https://app.concordatofacile.it/").</summary>
    private const string RealFormUrl = "https://app.concordatofacile.it/";

    /// <summary>
    /// The address the link opens. Always the real module of ConcordatoFacile, <c>https://app.concordatofacile.it/</c> (specification
    /// v0.2 §2: "Sempre https://app.concordatofacile.it/"), except (a) with <c>FormMode=Simulated</c> (CF-SIM, test environment only): the
    /// page <c>/guided-form</c> of the web app, which plays the module, without a trailing slash, and (b) with <c>FormUrl</c> (CF-URL, test
    /// environment only, refused in Production): the test module that ConcordatoFacile prepares for CasaZen. The parameters and the
    /// signature are the same in every case, so the signing code stays the code that ConcordatoFacile verifies.
    /// </summary>
    private static string FormBaseUrl(ConcordatoFacileOptions concordatoFacile)
    {
        if (concordatoFacile.FormMode != ConcordatoFacileFormMode.Simulated)
        {
            return string.IsNullOrWhiteSpace(concordatoFacile.FormUrl) ? RealFormUrl : concordatoFacile.FormUrl.Trim();
        }

        if (string.IsNullOrWhiteSpace(concordatoFacile.BaseUrl))
        {
            throw new InvalidOperationException(
                "ConcordatoFacile:BaseUrl (the address of the web app) is required with ConcordatoFacile:FormMode=Simulated.");
        }

        return $"{concordatoFacile.BaseUrl.Trim().TrimEnd('/')}/guided-form";
    }

    /// <summary>
    /// CF-BYPASS: the secret that lets a browser through the protection of the test module of ConcordatoFacile (a Vercel deployment),
    /// only when the link opens that module (<c>FormUrl</c> set, not the simulator); in any other case none, whatever is configured, so
    /// that the real module never receives it. The startup refuses the secret in Production besides.
    /// </summary>
    private static string? TestModuleBypassSecret(ConcordatoFacileOptions concordatoFacile) =>
        concordatoFacile.FormMode != ConcordatoFacileFormMode.Simulated
        && !string.IsNullOrWhiteSpace(concordatoFacile.FormUrl)
        && !string.IsNullOrWhiteSpace(concordatoFacile.FormBypassSecret)
            ? concordatoFacile.FormBypassSecret.Trim()
            : null;

    /// <summary>
    /// Builds the signed URL per ADR-007-R6.
    /// Each parameter value is URL-encoded individually AFTER signature calculation. With a <paramref name="bypassSecret"/> (the test
    /// module only) the two parameters of Vercel follow, outside the signature: the secret of the protection and the request to keep it
    /// in a cookie, so that the next pages of the module open too.
    /// </summary>
    private static string BuildSignedUrl(
        string baseUrl,
        Guid leaseId,
        string expiresAt,
        string signature,
        string comuneName,
        string provinciaCode,
        string? bypassSecret)
    {
        var queryParams = new List<string>
        {
            $"partner={HttpUtility.UrlEncode("casazen")}",
            $"leaseId={HttpUtility.UrlEncode(leaseId.ToString().ToLowerInvariant())}",
            $"expiresAt={HttpUtility.UrlEncode(expiresAt)}",
            $"signature={HttpUtility.UrlEncode(signature)}",
            $"comune={HttpUtility.UrlEncode(comuneName)}",
            $"provincia={HttpUtility.UrlEncode(provinciaCode)}"
        };

        if (bypassSecret is not null)
        {
            queryParams.Add($"x-vercel-protection-bypass={HttpUtility.UrlEncode(bypassSecret)}");
            queryParams.Add("x-vercel-set-bypass-cookie=true");
        }

        return $"{baseUrl}?{string.Join("&", queryParams)}";
    }
}
