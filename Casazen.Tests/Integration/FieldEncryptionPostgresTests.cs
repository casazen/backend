using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-14 (A5-30) on a real PostgreSQL database: the guest identity document fields and the Questura credentials are
/// stored encrypted (the database never holds the clear value), read back through EF, values written in clear before
/// CO-14 are encrypted once by the startup step, a key rotation keeps old values readable, and two contexts with
/// different Data Protection providers in the same process each use their own (the limit noted by FD-20).
/// </summary>
public class FieldEncryptionPostgresTests
{
    private const string DocumentNumber = "CA12345AB";
    private const string IssuePlace = "Comune di Firenze";
    private const string StayDocumentNumber = "YA7654321";
    private const string StayIssuePlace = "Repubblica di San Marino";
    private const string Username = "RM000123";
    private const string Password = "p4ss word!";
    private const string WsKey = "AAbbCCddEEff-1234567890";

    [PostgresFact]
    public async Task SaveChanges_GuestDocumentAndCredentials_TheDatabaseNeverHoldsTheClearValues()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("enc");
        await using var dataProtection = BuildDataProtection(database);
        var seed = await SeedAsync(database, Provider(dataProtection));

        await using var stored = database.CreateContext();
        var rows = new[]
        {
            await RawGuestAsync(stored, seed.GuestId),
            await RawStayGuestAsync(stored, seed.StayGuestId),
            await RawCredentialsAsync(stored, seed.PropertyId),
        };
        foreach (var clear in new[] { DocumentNumber, IssuePlace, StayDocumentNumber, StayIssuePlace, Username, Password, WsKey })
            Assert.All(rows, row => Assert.DoesNotContain(clear, row));

        // Every encrypted column holds a Data Protection payload.
        var guest = await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
        var credentials = await stored.PropertyQuesturaCredentials.AsNoTracking().SingleAsync(c => c.PropertyId == seed.PropertyId);
        Assert.All(
            new[] { guest.DocumentNumber, guest.DocumentIssuingCountry, credentials.Username, credentials.Password, credentials.WsKey },
            value => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, value));
        // Not encrypted: name and e-mail stay searchable (TN-1 lookup).
        Assert.Equal("Giulia", guest.FirstName);
    }

    [PostgresFact]
    public async Task Read_EncryptedColumns_RoundTripThroughEfAfterARestart()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("enc");
        Seed seed;
        await using (var firstRun = BuildDataProtection(database))
            seed = await SeedAsync(database, Provider(firstRun));

        // A new process: new provider, same key ring (DataProtectionKeys table).
        await using var afterRestart = BuildDataProtection(database);
        await using var db = NewContext(database, Provider(afterRestart));
        var guest = await db.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
        var stay = await db.StayGuests.AsNoTracking().SingleAsync(s => s.Id == seed.StayGuestId);
        var credentials = await db.PropertyQuesturaCredentials.AsNoTracking().SingleAsync(c => c.PropertyId == seed.PropertyId);

        Assert.Equal((DocumentNumber, IssuePlace), (guest.DocumentNumber, guest.DocumentIssuingCountry));
        Assert.Equal((StayDocumentNumber, StayIssuePlace), (stay.DocumentNumber, stay.DocumentIssuePlaceName));
        Assert.Equal((Username, Password, WsKey), (credentials.Username, credentials.Password, credentials.WsKey));
        // Empty values stay empty (no payload for "nothing").
        Assert.Equal(string.Empty, (await db.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.EmptyGuestId)).DocumentNumber);
    }

    [PostgresFact]
    public async Task Migration_ValuesStoredInClearBeforeCo14_AreEncryptedOnceByTheStartupStep()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("enc");
        Seed seed;
        await using (var before = database.CreateContext())
        {
            before.GetService<IMigrator>().Migrate(PreviousMigration(before));
            seed = await SeedAsync(before, credentialsBySql: true);
        }

        await using (var migrate = database.CreateContext())
            await migrate.Database.MigrateAsync();

        await using var dataProtection = BuildDataProtection(database);
        await using var stored = database.CreateContext();
        Assert.Contains(DocumentNumber, await RawGuestAsync(stored, seed.GuestId));
        // The credentials rows get their tenant from the property, the password column keeps its value.
        var legacyCredentials = await stored.PropertyQuesturaCredentials.AsNoTracking().SingleAsync(c => c.PropertyId == seed.PropertyId);
        Assert.Equal((seed.OrgId, Password), (legacyCredentials.OrgId, legacyCredentials.Password));

        // Before the step the clear values are readable as they are.
        await using (var reader = NewContext(database, Provider(dataProtection)))
        {
            var legacy = await reader.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
            Assert.Equal(DocumentNumber, legacy.DocumentNumber);
        }

        await using (var db = NewContext(database, Provider(dataProtection)))
        {
            var rewritten = await EncryptedColumns.EncryptLegacyPlaintextAsync(db, NullLogger.Instance);
            // Guest with a document, stay guest, credentials (the guest without a document has nothing to encrypt).
            Assert.Equal(3, rewritten);
        }

        var guestRow = await RawGuestAsync(stored, seed.GuestId);
        var stayRow = await RawStayGuestAsync(stored, seed.StayGuestId);
        var credentialsRow = await RawCredentialsAsync(stored, seed.PropertyId);
        foreach (var clear in new[] { DocumentNumber, IssuePlace, StayDocumentNumber, StayIssuePlace, Username, Password, WsKey })
            Assert.All(new[] { guestRow, stayRow, credentialsRow }, row => Assert.DoesNotContain(clear, row));

        await using (var reader = NewContext(database, Provider(dataProtection)))
        {
            var guest = await reader.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId);
            var stay = await reader.StayGuests.AsNoTracking().SingleAsync(s => s.Id == seed.StayGuestId);
            var credentials = await reader.PropertyQuesturaCredentials.AsNoTracking().SingleAsync(c => c.PropertyId == seed.PropertyId);
            Assert.Equal((DocumentNumber, IssuePlace, "Giulia"), (guest.DocumentNumber, guest.DocumentIssuingCountry, guest.FirstName));
            Assert.Equal((StayDocumentNumber, StayIssuePlace), (stay.DocumentNumber, stay.DocumentIssuePlaceName));
            Assert.Equal((Username, Password, WsKey), (credentials.Username, credentials.Password, credentials.WsKey));
        }

        // Idempotent: a second startup rewrites nothing, the payloads stay as they are.
        await using (var again = NewContext(database, Provider(dataProtection)))
            Assert.Equal(0, await EncryptedColumns.EncryptLegacyPlaintextAsync(again, NullLogger.Instance));
        Assert.Equal(guestRow, await RawGuestAsync(stored, seed.GuestId));
    }

    [PostgresFact]
    public async Task EncryptLegacyPlaintext_ContextWithoutDataProtection_StopsTheStartup()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("enc");
        await using var withoutProvider = database.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => EncryptedColumns.EncryptLegacyPlaintextAsync(withoutProvider, NullLogger.Instance));
    }

    [PostgresFact]
    public async Task KeyRotation_NewDefaultKey_OldValuesStayReadableAndNewWritesCarryTheNewKeyId()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("enc");
        await using var dataProtection = BuildDataProtection(database);
        var provider = Provider(dataProtection);
        var seed = await SeedAsync(database, provider);

        await using var stored = database.CreateContext();
        var oldKey = KeyIdOf((await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId)).DocumentNumber);

        // Rotation (automatic every 90 days, or forced from the runbook): a new key becomes the default one.
        var keyManager = dataProtection.GetRequiredService<IKeyManager>();
        var newKey = keyManager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90)).KeyId;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(2, await stored.DataProtectionKeys.CountAsync());
        await WaitUntilDefaultKeyAsync(provider, newKey);

        Guid newGuestId;
        await using (var db = NewContext(database, provider))
        {
            // The old value is read with the key id it carries.
            var old = await db.Guests.SingleAsync(g => g.Id == seed.GuestId);
            Assert.Equal(DocumentNumber, old.DocumentNumber);

            // New writes use the new key; re-saving a value moves it to the new key.
            var added = NewGuest(seed.OrgId, "ZZ0000001");
            db.Guests.Add(added);
            db.Entry(old).Property(g => g.DocumentNumber).IsModified = true;
            await db.SaveChangesAsync();
            newGuestId = added.Id;
        }

        stored.ChangeTracker.Clear();
        Assert.Equal(newKey, KeyIdOf((await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == newGuestId)).DocumentNumber));
        Assert.Equal(newKey, KeyIdOf((await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId)).DocumentNumber));
        // Values not rewritten keep the old key and stay readable.
        var stayPayload = (await stored.StayGuests.AsNoTracking().SingleAsync(s => s.Id == seed.StayGuestId)).DocumentNumber;
        Assert.Equal(oldKey, KeyIdOf(stayPayload));
        await using var reader = NewContext(database, provider);
        Assert.Equal(StayDocumentNumber, (await reader.StayGuests.AsNoTracking().SingleAsync(s => s.Id == seed.StayGuestId)).DocumentNumber);
    }

    // The limit noted by FD-20: EF cached one model per context type, so every context of the process encrypted with
    // the provider of the first one. Two providers in the same process now each encrypt with their own.
    [PostgresFact]
    public async Task TwoContexts_DifferentProvidersInTheSameProcess_EachEncryptsWithItsOwnProvider()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("enc");
        await using var dataProtection = BuildDataProtection(database);
        var first = Provider(dataProtection);
        var second = new EphemeralDataProtectionProvider();

        // A context without provider first (design time, tests): it must not fix the model of the others.
        await using (var plain = database.CreateContext())
            Assert.Null(DocumentNumberConverter(plain));

        var seed = await SeedAsync(database, first);
        Guid secondGuestId;
        await using (var secondContext = NewContext(database, second))
        {
            Assert.IsType<EncryptedStringConverter>(DocumentNumberConverter(secondContext));
            var guest = NewGuest(seed.OrgId, "ZZ0000002");
            secondContext.Guests.Add(guest);
            await secondContext.SaveChangesAsync();
            secondGuestId = guest.Id;
        }

        await using var stored = database.CreateContext();
        var firstPayload = (await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId)).DocumentNumber;
        var secondPayload = (await stored.Guests.AsNoTracking().SingleAsync(g => g.Id == secondGuestId)).DocumentNumber;
        var firstProtector = first.CreateProtector(EncryptedColumns.GuestDocumentPurpose);
        var secondProtector = second.CreateProtector(EncryptedColumns.GuestDocumentPurpose);

        Assert.Equal(DocumentNumber, firstProtector.Unprotect(firstPayload));
        Assert.Equal("ZZ0000002", secondProtector.Unprotect(secondPayload));
        Assert.ThrowsAny<CryptographicException>(() => firstProtector.Unprotect(secondPayload));
        Assert.ThrowsAny<CryptographicException>(() => secondProtector.Unprotect(firstPayload));

        await using var firstContext = NewContext(database, first);
        await using var sameProvider = NewContext(database, first);
        await using var otherProvider = NewContext(database, second);
        Assert.Same(firstContext.Model, sameProvider.Model);
        Assert.NotSame(firstContext.Model, otherProvider.Model);
        Assert.Equal(DocumentNumber, (await firstContext.Guests.AsNoTracking().SingleAsync(g => g.Id == seed.GuestId)).DocumentNumber);
        Assert.Equal("ZZ0000002", (await otherProvider.Guests.AsNoTracking().SingleAsync(g => g.Id == secondGuestId)).DocumentNumber);
    }

    private sealed record Seed(Guid OrgId, Guid PropertyId, Guid GuestId, Guid EmptyGuestId, Guid StayGuestId);

    private static async Task<Seed> SeedAsync(PostgresTestDatabase database, IDataProtectionProvider provider)
    {
        await using var db = NewContext(database, provider);
        return await SeedAsync(db, credentialsBySql: false);
    }

    /// <summary>
    /// Org, property, a guest with a document and one without, a booking with its stay guest, and the Questura
    /// credentials of the property: through the model, or by SQL with the columns before CO-14.
    /// </summary>
    private static async Task<Seed> SeedAsync(AppDbContext db, bool credentialsBySql)
    {
        var org = new OrgEntity
        {
            Name = "Org CO-14",
            Slug = $"co14-{Guid.NewGuid():N}",
            DisplayName = "Org CO-14",
            ContactEmail = "co14@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = "auth0|co14",
            OrgId = org.Id,
            Name = "Casa CO-14",
            Description = "Casa",
            Address = "Via Roma 1",
            City = "Firenze",
            PostalCode = "50100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        var guest = NewGuest(org.Id, DocumentNumber);
        var empty = NewGuest(org.Id, string.Empty);
        empty.DocumentIssuingCountry = string.Empty;
        var checkIn = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 300m,
            TotalPrice = 300m,
            FreeRefundDeadline = checkIn.AddDays(-7),
        };
        var stay = new StayGuest
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            Position = 0,
            Type = StayGuestType.SingleGuest,
            FirstName = "Giulia",
            LastName = "Bianchi",
            DocumentType = GuestDocumentType.Passport,
            DocumentNumber = StayDocumentNumber,
            DocumentIssuePlaceName = StayIssuePlace,
        };
        if (credentialsBySql)
        {
            // CO-15: the model's "Guests" and "StayGuests" no longer match the table at this point ("AlloggiatiDataErasedAt"
            // and "AnonymizedAt" do not exist yet, "DataRetentionUntil" still required on Guests): write them by SQL with
            // the columns before CO-15, like the credentials below are written with the columns before CO-14. The guests
            // must exist before the booking and the stay guest, which have a foreign key to them.
            db.AddRange(org, property);
            await db.SaveChangesAsync();
            await InsertGuestBeforeCo15Async(db, guest);
            await InsertGuestBeforeCo15Async(db, empty);
            // CO-21 (unrelated to CO-15) added 4 nullable Bookings columns after this migration point too. They have no
            // constraint of their own, so it is simplest to create them here just for the insert below and drop them
            // again right after: the real migration (already ahead in the migrations list) recreates them properly.
            await AddBookingsColumnsBeforeCo21Async(db);
            db.Add(booking);
            await db.SaveChangesAsync();
            await DropBookingsColumnsBeforeCo21Async(db);
            await InsertStayGuestBeforeCo15Async(db, stay);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "PropertyQuesturaCredentials" ("Id", "PropertyId", "Username", "PasswordEncrypted", "WsKey", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {property.Id}, {Username}, {Password}, {WsKey}, {DateTime.UtcNow})
                """);
        }
        else
        {
            db.AddRange(org, property, guest, empty, booking, stay);
            db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
            {
                PropertyId = property.Id,
                OrgId = org.Id,
                Username = Username,
                Password = Password,
                WsKey = WsKey,
            });
            await db.SaveChangesAsync();
        }

        return new Seed(org.Id, property.Id, guest.Id, empty.Id, stay.Id);
    }

    /// <summary>
    /// A guest row as the table accepted it before CO-15 (no "AlloggiatiDataErasedAt" column yet, "DataRetentionUntil"
    /// still required, matching the entity's own former default of created-at + 7 years): used only for the "before"
    /// seed above, so the entity's other columns keep going through the model.
    /// </summary>
    private static Task InsertGuestBeforeCo15Async(AppDbContext db, Guest guest) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Guests" (
                "Id", "OrgId", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "DateOfBirth", "PlaceOfBirth", "Nationality", "DocumentType", "DocumentNumber", "DocumentIssueDate",
                "DocumentExpiryDate", "DocumentIssuingCountry", "DocumentScanUrl", "DataProcessingConsentDate",
                "ConsentIpAddress", "DataRetentionExpiryDate", "ErasureRequested", "ErasureRequestedDate",
                "DataAnonymizedDate", "Notes", "Gender", "ConsentDate", "ConsentVersion", "MarketingConsent",
                "MarketingConsentDate", "DataRetentionUntil", "DataProcessingPurpose", "IsDeleted", "DeletedAt",
                "DeletionReason", "CreatedAt", "UpdatedAt")
            VALUES ({guest.Id}, {guest.OrgId}, {guest.FirstName}, {guest.LastName}, {guest.Email}, {guest.PhoneNumber},
                {guest.Address}, {guest.City}, {guest.PostalCode}, {guest.Country}, {guest.DateOfBirth}, {guest.PlaceOfBirth},
                {guest.Nationality}, {(int?)guest.DocumentType}, {guest.DocumentNumber}, {guest.DocumentIssueDate},
                {guest.DocumentExpiryDate}, {guest.DocumentIssuingCountry}, {guest.DocumentScanUrl},
                {guest.DataProcessingConsentDate}, {guest.ConsentIpAddress}, {guest.DataRetentionExpiryDate},
                {guest.ErasureRequested}, {guest.ErasureRequestedDate}, {guest.DataAnonymizedDate}, {guest.Notes},
                {(int?)guest.Gender}, {guest.ConsentDate}, {guest.ConsentVersion}, {guest.MarketingConsent},
                {guest.MarketingConsentDate}, {guest.CreatedAt.AddYears(7)}, {guest.DataProcessingPurpose}, {guest.IsDeleted},
                {guest.DeletedAt}, {guest.DeletionReason}, {guest.CreatedAt}, {guest.UpdatedAt})
            """);

    /// <summary>
    /// Adds the 4 nullable "Bookings" columns of the CO-21 migration (<c>AddOtaStayFromICalBlock</c>), so the
    /// model-based insert of a booking works at this "before CO-15" migration point too. Paired with
    /// <see cref="DropBookingsColumnsBeforeCo21Async"/>.
    /// </summary>
    private static Task AddBookingsColumnsBeforeCo21Async(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Bookings" ADD COLUMN "ChannelLabel" character varying(60);
        ALTER TABLE "Bookings" ADD COLUMN "ICalFeedId" uuid;
        ALTER TABLE "Bookings" ADD COLUMN "OtaReviewRaisedAt" timestamp with time zone;
        ALTER TABLE "Bookings" ADD COLUMN "OtaReviewReason" integer;
        """);

    private static Task DropBookingsColumnsBeforeCo21Async(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Bookings" DROP COLUMN "ChannelLabel";
        ALTER TABLE "Bookings" DROP COLUMN "ICalFeedId";
        ALTER TABLE "Bookings" DROP COLUMN "OtaReviewRaisedAt";
        ALTER TABLE "Bookings" DROP COLUMN "OtaReviewReason";
        """);

    /// <summary>A stay guest row as the table accepted it before CO-15 (no "AnonymizedAt" column yet).</summary>
    private static Task InsertStayGuestBeforeCo15Async(AppDbContext db, StayGuest stay) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StayGuests" (
                "Id", "BookingId", "OrgId", "GuestId", "Position", "Type", "FirstName", "LastName", "Gender",
                "DateOfBirth", "BornInItaly", "BirthComuneCode", "BirthComuneName", "BirthProvince", "BirthCountryCode",
                "BirthCountryName", "CitizenshipCode", "CitizenshipName", "DocumentType", "DocumentTypeCode",
                "DocumentNumber", "DocumentIssuePlaceCode", "DocumentIssuePlaceName", "DataSource", "EnteredByUserId",
                "CreatedAt", "UpdatedAt")
            VALUES ({stay.Id}, {stay.BookingId}, {stay.OrgId}, {stay.GuestId}, {stay.Position}, {(int)stay.Type},
                {stay.FirstName}, {stay.LastName}, {(int?)stay.Gender}, {stay.DateOfBirth}, {stay.BornInItaly},
                {stay.BirthComuneCode}, {stay.BirthComuneName}, {stay.BirthProvince}, {stay.BirthCountryCode},
                {stay.BirthCountryName}, {stay.CitizenshipCode}, {stay.CitizenshipName}, {(int?)stay.DocumentType},
                {stay.DocumentTypeCode}, {stay.DocumentNumber}, {stay.DocumentIssuePlaceCode}, {stay.DocumentIssuePlaceName},
                {(int)stay.DataSource}, {stay.EnteredByUserId}, {stay.CreatedAt}, {stay.UpdatedAt})
            """);

    private static Guest NewGuest(Guid orgId, string documentNumber) => new()
    {
        OrgId = orgId,
        FirstName = "Giulia",
        LastName = "Bianchi",
        Email = $"giulia.{Guid.NewGuid():N}@example.com",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = documentNumber,
        DocumentIssuingCountry = IssuePlace,
    };

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + nameof(EncryptGuestDocumentAndQuesturaCredentials), StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    // Whole rows as the database stores them (context without converters).
    private static Task<string> RawGuestAsync(AppDbContext stored, Guid id) =>
        stored.Database.SqlQuery<string>($"""SELECT row_to_json(t)::text AS "Value" FROM "Guests" AS t WHERE t."Id" = {id}""").SingleAsync();

    private static Task<string> RawStayGuestAsync(AppDbContext stored, Guid id) =>
        stored.Database.SqlQuery<string>($"""SELECT row_to_json(t)::text AS "Value" FROM "StayGuests" AS t WHERE t."Id" = {id}""").SingleAsync();

    private static Task<string> RawCredentialsAsync(AppDbContext stored, Guid propertyId) =>
        stored.Database.SqlQuery<string>(
            $"""SELECT row_to_json(t)::text AS "Value" FROM "PropertyQuesturaCredentials" AS t WHERE t."PropertyId" = {propertyId}""").SingleAsync();

    /// <summary>
    /// The provider reloads its key ring in the background once a key is created: wait until it protects with the new
    /// key (it does not become the default one synchronously).
    /// </summary>
    private static async Task WaitUntilDefaultKeyAsync(IDataProtectionProvider provider, Guid keyId)
    {
        var probe = provider.CreateProtector("Casazen.Tests.KeyRotationProbe");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (KeyIdOf(probe.Protect("probe")) != keyId)
        {
            Assert.True(DateTime.UtcNow < deadline, "The new key never became the default key.");
            await Task.Delay(100);
        }
    }

    /// <summary>Id of the key that produced a Data Protection payload (bytes 4-19, after the magic header).</summary>
    private static Guid KeyIdOf(string payload)
    {
        var base64 = payload.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return new Guid(Convert.FromBase64String(base64).AsSpan(4, 16));
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter? DocumentNumberConverter(AppDbContext db) =>
        db.Model.FindEntityType(typeof(Guest))!.FindProperty(nameof(Guest.DocumentNumber))!.GetValueConverter();

    private static AppDbContext NewContext(PostgresTestDatabase database, IDataProtectionProvider provider) =>
        new(database.CreateOptions(), tenantContext: null, provider);

    private static IDataProtectionProvider Provider(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>();

    /// <summary>Data Protection as the application configures it: key ring in the database's DataProtectionKeys table.</summary>
    private static ServiceProvider BuildDataProtection(PostgresTestDatabase database)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database.ConnectionString));
        services.AddCasazenDataProtection(configuration, new TestHostEnvironment());
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Casazen.Web";
        public string ContentRootPath { get; set; } = System.AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
