using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-20 (A8-16): the OTA partner credentials stored before the freeze stay encrypted at rest (Data Protection value
/// converter of <see cref="AppDbContext"/>, key ring persisted by FD-07) with the context the application resolves.
/// </summary>
public class OtaIntegrationSecretsEncryptionTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string DataProtectionPayloadPrefix = "CfDJ8";

    [PostgresFact]
    public async Task SaveChanges_OtaIntegrationSecrets_AreStoredEncryptedAndReadBackInClear()
    {
        var property = await factory.SeedPropertyAsync();
        var integration = new OtaIntegration
        {
            PropertyId = property.Id,
            Platform = "Airbnb",
            ExternalPropertyId = "listing-fd20",
            ApiKey = "sk_live_fd20_api_key",
            ApiSecret = "sk_live_fd20_api_secret",
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OtaIntegrations.Add(integration);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rawKey = await RawColumnAsync(db, "ApiKey", integration.Id);
            var rawSecret = await RawColumnAsync(db, "ApiSecret", integration.Id);

            // Data Protection payloads (base64url of the 0x09F0C9F0 magic header), never the plain value.
            Assert.StartsWith(DataProtectionPayloadPrefix, rawKey);
            Assert.StartsWith(DataProtectionPayloadPrefix, rawSecret);
            Assert.DoesNotContain("sk_live_fd20", rawKey);
            Assert.DoesNotContain("sk_live_fd20", rawSecret);

            var entity = db.Model.FindEntityType(typeof(OtaIntegration))!;
            Assert.IsType<EncryptedStringConverter>(entity.FindProperty(nameof(OtaIntegration.ApiKey))!.GetValueConverter());
            Assert.IsType<EncryptedStringConverter>(entity.FindProperty(nameof(OtaIntegration.ApiSecret))!.GetValueConverter());

            var loaded = await db.OtaIntegrations.AsNoTracking().SingleAsync(o => o.Id == integration.Id);
            Assert.Equal("sk_live_fd20_api_key", loaded.ApiKey);
            Assert.Equal("sk_live_fd20_api_secret", loaded.ApiSecret);
        }
    }

    // Raw SQL scalar query: no value converter, i.e. what is really in the column.
    private static Task<string> RawColumnAsync(AppDbContext db, string column, Guid id) =>
        column switch
        {
            "ApiKey" => db.Database
                .SqlQuery<string>($"SELECT \"ApiKey\" AS \"Value\" FROM \"OtaIntegrations\" WHERE \"Id\" = {id}")
                .SingleAsync(),
            "ApiSecret" => db.Database
                .SqlQuery<string>($"SELECT \"ApiSecret\" AS \"Value\" FROM \"OtaIntegrations\" WHERE \"Id\" = {id}")
                .SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
}
