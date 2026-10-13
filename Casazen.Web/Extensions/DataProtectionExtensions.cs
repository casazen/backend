using System.Security.Cryptography.X509Certificates;
using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Web.Extensions;

/// <summary>
/// Data Protection keys persisted in the database (table <c>DataProtectionKeys</c>) so that the values encrypted with
/// them (the encrypted columns: OTA secrets, iCal URLs, guest identity data, Questura credentials) survive redeploys
/// (FD-07, A9-04, CO-14). The key ring is encrypted at rest with an X.509 certificate supplied through configuration:
/// <list type="bullet">
/// <item><c>DataProtection:CertificatePfxBase64</c> + <c>DataProtection:CertificatePassword</c>: current certificate.</item>
/// <item><c>DataProtection:PreviousCertificatePfxBase64</c> + <c>DataProtection:PreviousCertificatePassword</c>:
/// optional, still able to decrypt keys written before a certificate rotation.</item>
/// </list>
/// Without a certificate the keys would be stored in clear next to the data they protect: whoever reads the database
/// could decrypt everything. The certificate is therefore <b>required</b> outside Development and Testing (CO-14): the
/// startup fails without it (<see cref="RequiredConfiguration.IsEnforced"/>). Runbooks: docs/runbooks/encryption.md,
/// docs/runbooks/storage.md.
/// </summary>
public static class DataProtectionExtensions
{
    public const string ApplicationName = "Casazen";

    public const string CertificateVariable = "DataProtection__CertificatePfxBase64";
    public const string CertificatePasswordVariable = "DataProtection__CertificatePassword";

    /// <exception cref="InvalidOperationException">
    /// No key-encryption certificate outside Development and Testing, or a certificate that cannot be loaded.
    /// </exception>
    public static IServiceCollection AddCasazenDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<AppDbContext>();

        var current = LoadCertificate(configuration, "DataProtection:CertificatePfxBase64", "DataProtection:CertificatePassword");
        if (current is null)
        {
            if (RequiredConfiguration.IsEnforced(environment))
            {
                throw new InvalidOperationException(
                    $"{CertificateVariable} is missing (environment {environment.EnvironmentName}): the Data Protection " +
                    "keys, which encrypt the guest identity data, the Questura credentials and the other encrypted " +
                    "columns, would be stored in clear in the same database. Set " + CertificateVariable + " and " +
                    CertificatePasswordVariable + ", see docs/runbooks/encryption.md.");
            }

            return services;
        }

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
        else
        {
            // Development/Testing only: elsewhere the startup already failed (AddCasazenDataProtection).
            app.Logger.LogWarning(
                "Data Protection keys are persisted in the database WITHOUT at-rest encryption (allowed only in " +
                "Development and Testing). Set " + CertificateVariable + " and " + CertificatePasswordVariable +
                " (docs/runbooks/encryption.md).");
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
