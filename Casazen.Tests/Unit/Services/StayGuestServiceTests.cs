using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The guest columns of the check-in (E03/F01/US01/T01, NF-40, NF-46): the tourist tax exemption is the host's decision, and the new personal
/// columns are erased with the guest and, for the exemption, encrypted at rest.
/// </summary>
public class StayGuestServiceTests
{
    // One provider for the whole class: EF keeps one model per provider (see OctorateConnectionEncryptionTests).
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task StayGuest_GuestPortalSave_KeepsHostTaxExemption_Test()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync(); // the seeded exemption reasons
        var booking = await SeedBookingAsync(db);
        var service = new StayGuestService(db, new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance));

        // The host chooses the exemption.
        var byHost = await service.ReplaceAsync(booking, [Guest(TouristTaxExemptionReason.ResidentCode)], StayGuestAuthor.Host("auth0|host"));
        Assert.True(byHost.Success);
        Assert.Equal(TouristTaxExemptionReason.ResidentCode, (await db.StayGuests.SingleAsync()).TaxExemptionCode);

        // The guest portal sends another code, or none: the host's choice stays, and nothing of the input is read for it.
        var byGuest = await service.ReplaceAsync(booking, [Guest(TouristTaxExemptionReason.HealthCareCode)], StayGuestAuthor.GuestPortal);
        Assert.True(byGuest.Success);
        Assert.Equal(TouristTaxExemptionReason.ResidentCode, (await db.StayGuests.SingleAsync()).TaxExemptionCode);
        var byGuestWithout = await service.ReplaceAsync(booking, [Guest(null)], StayGuestAuthor.GuestPortal);
        Assert.True(byGuestWithout.Success);
        Assert.Equal(TouristTaxExemptionReason.ResidentCode, (await db.StayGuests.SingleAsync()).TaxExemptionCode);

        // The automatic reason (the age) can never be chosen.
        var automatic = await service.ReplaceAsync(booking, [Guest(TouristTaxExemptionReason.MinorAgeCode)], StayGuestAuthor.Host("auth0|host"));
        var error = Assert.Single(automatic.Errors);
        Assert.Equal(nameof(StayGuestInput.TaxExemptionCode), error.Field);
        Assert.Equal(CheckInValidationKeys.TaxExemptionInvalid, error.MessageKey);
        Assert.Equal(TouristTaxExemptionReason.ResidentCode, (await db.StayGuests.SingleAsync()).TaxExemptionCode);

        // The host clears it: the input is the whole truth for the host.
        var cleared = await service.ReplaceAsync(booking, [Guest(null)], StayGuestAuthor.Host("auth0|host"));
        Assert.True(cleared.Success);
        Assert.Null((await db.StayGuests.SingleAsync()).TaxExemptionCode);
    }

    [Fact]
    public void StayGuest_NewPersonalColumns_ErasedWithGuest_Test()
    {
        var row = new StayGuest
        {
            Type = StayGuestType.SingleGuest,
            FirstName = "Anna",
            LastName = "Bianchi",
            DocumentNumber = "CA12345AB",
            DocumentIssuedOn = new DateOnly(2020, 3, 1),
            DocumentExpiresOn = new DateOnly(2030, 3, 1),
            ResidenceInItaly = true,
            ResidenceComuneCode = "058091",
            ResidenceComuneName = "Roma",
            ResidenceProvince = "RM",
            ResidenceCountryCode = "100000100",
            ResidenceCountryName = "Italia",
            TourismType = "holiday",
            TravelWay = "train",
            EducationLevel = "degree",
            TaxExemptionCode = TouristTaxExemptionReason.HealthCareCode,
        };

        var changed = GuestDataEraser.AnonymizeStayGuest(row, new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc));

        Assert.True(changed);
        Assert.Null(row.DocumentIssuedOn);
        Assert.Null(row.DocumentExpiresOn);
        Assert.Null(row.ResidenceInItaly);
        Assert.Null(row.ResidenceComuneCode);
        Assert.Null(row.ResidenceComuneName);
        Assert.Null(row.ResidenceProvince);
        Assert.Null(row.ResidenceCountryCode);
        Assert.Null(row.ResidenceCountryName);
        Assert.Null(row.TourismType);
        Assert.Null(row.TravelWay);
        Assert.Null(row.EducationLevel);
        Assert.Null(row.TaxExemptionCode);

        // The kind and the position stay, so the stay keeps its shape; a second pass changes nothing.
        Assert.Equal(StayGuestType.SingleGuest, row.Type);
        Assert.False(GuestDataEraser.AnonymizeStayGuest(row, new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StayGuest_TaxExemptionCode_StoredEncrypted_Test()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var column = db.Model.FindEntityType(typeof(StayGuest))!.FindProperty(nameof(StayGuest.TaxExemptionCode))!;

        Assert.Contains(
            EncryptedColumns.All,
            c => c.EntityType == typeof(StayGuest) && c.Property == nameof(StayGuest.TaxExemptionCode)
                && c.Purpose == EncryptedColumns.GuestDocumentPurpose);
        var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
        Assert.Null(column.GetMaxLength());

        var stored = (string)converter.ConvertToProvider(TouristTaxExemptionReason.HealthCareCode)!;

        // What the column holds is a payload with nothing of the reason in it, read back only through the converter.
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(TouristTaxExemptionReason.HealthCareCode, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("health", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TouristTaxExemptionReason.HealthCareCode, converter.ConvertFromProvider(stored));
    }

    private static StayGuestInput Guest(string? taxExemptionCode) => new()
    {
        Type = nameof(StayGuestType.SingleGuest),
        FirstName = "Anna",
        LastName = "Bianchi",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1990, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Roma",
        BirthProvince = "RM",
        CitizenshipName = "Italia",
        DocumentType = nameof(GuestDocumentType.IdentityCard),
        DocumentNumber = "CA12345AB",
        DocumentIssuePlaceName = "Roma",
        ResidenceInItaly = true,
        ResidenceComuneName = "Roma",
        ResidenceProvince = "RM",
        TaxExemptionCode = taxExemptionCode,
    };

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Booking> SeedBookingAsync(AppDbContext db)
    {
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Test Org", Slug = $"org-{Guid.NewGuid():N}", DisplayName = "Test Org" };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 2,
        };
        var guest = new Casazen.Core.Entities.Guest
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            PropertyId = property.Id,
            Property = property,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        db.AddRange(org, property, guest, booking);
        await db.SaveChangesAsync();
        return booking;
    }
}
