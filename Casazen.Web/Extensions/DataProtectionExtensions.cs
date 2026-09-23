using System.Security.Cryptography.X509Certificates;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Web.Extensions;

/// <summary>
/// Data Protection keys persisted in the database (table <c>DataProtectionKeys</c>) so that secrets
/// encrypted with them (e.g. <c>OtaIntegration.ApiKey</c>) survive redeploys (FD-07, A9-04).
/// The key ring is encrypted at rest with an X.509 certificate supplied through configuration:
/// <list type="bullet">
/// <item><c>DataProtection:CertificatePfxBase64</c> + <c>DataProtection:CertificatePassword</c>: current certificate.</item>
/// <item><c>DataProtection:PreviousCertificatePfxBase64</c> + <c>DataProtection:PreviousCertificatePassword</c>:
/// optional, still able to decrypt keys written before a certificate rotation.</item>
/// </list>
/// Without a certificate the keys are stored unencrypted: anyone who can read the table can decrypt
/// the protected secrets. Runbook: docs/runbooks/storage.md.
/// </summary>
public static class DataProtectionExtensions
{
    public const string ApplicationName = "Casazen";

    public static IServiceCollection AddCasazenDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<AppDbContext>();

        var current = LoadCertificate(configuration, "DataProtection:CertificatePfxBase64", "DataProtection:CertificatePassword");
        if (current is null)
            return services;

        builder.ProtectKeysWithCertificate(current);
        var previous = LoadCertificate(configuration, "DataProtection:PreviousCertificatePfxBase64", "DataProtection:PreviousCertificatePassword");
        builder.UnprotectKeysWithAnyCertificate(previous is null ? [current] : [current, previous]);
        return services;
    }

    /// <summary>True when a key-encryption certificate is configured.</summary>
    public static bool HasKeyEncryptionCertificate(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["DataProtection:CertificatePfxBase64"]);

    /// <summary>Logs the at-rest protection of the key ring once at startup.</summary>
    public static WebApplication LogDataProtectionKeyProtection(this WebApplication app)
    {
        if (HasKeyEncryptionCertificate(app.Configuration))
        {
            app.Logger.LogInformation("Data Protection keys: persisted in the database, encrypted with the configured certificate.");
        }
        else if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            app.Logger.LogWarning(
                "Data Protection keys are persisted in the database WITHOUT at-rest encryption: whoever can read the " +
                "DataProtectionKeys table can decrypt the protected secrets. Set DataProtection__CertificatePfxBase64 and " +
                "DataProtection__CertificatePassword (docs/runbooks/storage.md).");
        }

        return app;
    }

    private static X509Certificate2? LoadCertificate(IConfiguration configuration, string pfxKey, string passwordKey)
    {
        var pfxBase64 = configuration[pfxKey];
        if (string.IsNullOrWhiteSpace(pfxBase64))
            return null;

        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(pfxBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{pfxKey} is not valid base64 (PKCS#12 / .pfx file).", ex);
        }

        var certificate = X509CertificateLoader.LoadPkcs12(pfx, configuration[passwordKey]);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"{pfxKey} must contain the private key.");

        return certificate;
    }
}
