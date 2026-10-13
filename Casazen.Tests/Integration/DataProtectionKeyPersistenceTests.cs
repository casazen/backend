using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-07 / A9-04: the Data Protection key ring lives in the <c>DataProtectionKeys</c> table, so a
/// secret protected before a redeploy (a new process, i.e. a new service provider) can still be read.
/// CO-14: outside Development and Testing the key ring must be encrypted with a certificate, the startup fails without.
/// </summary>
public class DataProtectionKeyPersistenceTests
{
    private const string Purpose = "Casazen.OtaIntegration.Secrets";

    [PostgresFact]
    public async Task Unprotect_AfterRestartOnSameKeyTable_ReturnsOriginalSecret()
    {
        await using var database = await CreateMigratedDatabaseAsync();

        string protectedValue;
        await using (var firstRun = BuildProvider(database, new Dictionary<string, string?>()))
            protectedValue = Protector(firstRun).Protect("sk_live_ota_secret");

        Assert.NotEmpty(await ReadKeyXmlAsync(database));

        await using var afterRestart = BuildProvider(database, new Dictionary<string, string?>());
        Assert.Equal("sk_live_ota_secret", Protector(afterRestart).Unprotect(protectedValue));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void AddCasazenDataProtection_WithoutCertificateOutsideDevelopment_StopsTheStartup(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddCasazenDataProtection(configuration, new TestHostEnvironment(environment)));

        Assert.Contains(DataProtectionExtensions.CertificateVariable, error.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void AddCasazenDataProtection_WithoutCertificateInDevelopmentOrTesting_IsAllowed(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection().AddCasazenDataProtection(configuration, new TestHostEnvironment(environment));

        Assert.Contains(services, d => d.ServiceType == typeof(IDataProtectionProvider));
    }

    [PostgresFact]
    public async Task Protect_WithCertificate_StoresEncryptedKeyReadableOnlyWithCertificate()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var withCertificate = CertificateSettings(CreateCertificatePfx("CN=casazen-dp-test"), "pfx-password");

        string protectedValue;
        // Production: the certificate is there, the startup goes on.
        await using (var firstRun = BuildProvider(database, withCertificate, "Production"))
            protectedValue = Protector(firstRun).Protect("sk_live_ota_secret");

        var keyXml = Assert.Single(await ReadKeyXmlAsync(database));
        Assert.Contains("encryptedSecret", keyXml);
        Assert.DoesNotContain("<value>", keyXml);

        await using (var afterRestart = BuildProvider(database, withCertificate))
            Assert.Equal("sk_live_ota_secret", Protector(afterRestart).Unprotect(protectedValue));

        await using var withoutCertificate = BuildProvider(database, new Dictionary<string, string?>());
        Assert.ThrowsAny<CryptographicException>(() => Protector(withoutCertificate).Unprotect(protectedValue));
    }

    [PostgresFact]
    public async Task Unprotect_AfterCertificateRotation_ReadsKeysOfPreviousCertificate()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var oldPfx = CreateCertificatePfx("CN=casazen-dp-old");
        var newPfx = CreateCertificatePfx("CN=casazen-dp-new");

        string protectedValue;
        await using (var beforeRotation = BuildProvider(database, CertificateSettings(oldPfx, "pfx-password")))
            protectedValue = Protector(beforeRotation).Protect("sk_live_ota_secret");

        var rotated = CertificateSettings(newPfx, "pfx-password");
        rotated["DataProtection:PreviousCertificatePfxBase64"] = Convert.ToBase64String(oldPfx);
        rotated["DataProtection:PreviousCertificatePassword"] = "pfx-password";
        await using var afterRotation = BuildProvider(database, rotated);

        Assert.Equal("sk_live_ota_secret", Protector(afterRotation).Unprotect(protectedValue));
    }

    private static IDataProtector Protector(IServiceProvider provider) =>
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static ServiceProvider BuildProvider(
        PostgresTestDatabase database,
        Dictionary<string, string?> settings,
        string environment = "Testing")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database.ConnectionString));
        services.AddCasazenDataProtection(configuration, new TestHostEnvironment(environment));
        return services.BuildServiceProvider();
    }

    private static async Task<PostgresTestDatabase> CreateMigratedDatabaseAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync("dp");
        database.MigrateToLatest();
        return database;
    }

    private static async Task<List<string>> ReadKeyXmlAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        return await db.DataProtectionKeys.Select(k => k.Xml!).ToListAsync();
    }

    private static Dictionary<string, string?> CertificateSettings(byte[] pfx, string password) => new()
    {
        ["DataProtection:CertificatePfxBase64"] = Convert.ToBase64String(pfx),
        ["DataProtection:CertificatePassword"] = password,
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Casazen.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static byte[] CreateCertificatePfx(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pfx, "pfx-password");
    }
}
