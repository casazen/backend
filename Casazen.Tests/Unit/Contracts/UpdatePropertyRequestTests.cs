using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Contracts;

/// <summary>
/// <c>PUT /api/properties/{id}</c> has PATCH semantics (A2-04): a field left out of the body keeps its stored value.
/// The body is read as MVC reads it (web JSON defaults, enums as strings).
/// </summary>
public class UpdatePropertyRequestTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Guid PolicyId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Property StoredProperty() => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = "auth0|owner",
        OrgId = Guid.NewGuid(),
        Name = "Casa al mare",
        Description = "Vista mare",
        Address = "Via Roma 1",
        City = "Rimini",
        PostalCode = "47921",
        Latitude = 44.06m,
        Longitude = 12.57m,
        Bedrooms = 2,
        Bathrooms = 1,
        MaxGuests = 4,
        NightlyRate = 120m,
        CleaningFee = 60m,
        DamageDeposit = 300m,
        Amenities = [PropertyAmenity.WiFi, PropertyAmenity.Kitchen],
        PhotoUrls = ["https://cdn.example/1.jpg"],
        HouseRules = "Niente feste",
        CinCode = "IT099014C2ABCDEFGH",
        Timezone = "Europe/Rome",
        CancellationPolicyId = PolicyId,
        IsActive = true,
        Slug = "casa-al-mare",
    };

    private static UpdatePropertyRequest Read(string json) =>
        JsonSerializer.Deserialize<UpdatePropertyRequest>(json, WebJson)!;

    [Fact]
    public void ApplyTo_OnlyNameSent_KeepsEveryOtherField()
    {
        var property = StoredProperty();
        var before = StoredProperty();

        Read("""{ "name": "Casa al mare rinnovata" }""").ApplyTo(property);

        Assert.Equal("Casa al mare rinnovata", property.Name);
        Assert.Equal(before.CleaningFee, property.CleaningFee);
        Assert.Equal(before.DamageDeposit, property.DamageDeposit);
        Assert.Equal(before.HouseRules, property.HouseRules);
        Assert.Equal(before.Timezone, property.Timezone);
        Assert.Equal(PolicyId, property.CancellationPolicyId);
        Assert.Equal(before.CinCode, property.CinCode);
        Assert.Equal(before.Slug, property.Slug);
        Assert.Equal(before.NightlyRate, property.NightlyRate);
        Assert.Equal(before.Bedrooms, property.Bedrooms);
        Assert.Equal(before.Amenities, property.Amenities);
        Assert.Equal(before.PhotoUrls, property.PhotoUrls);
        Assert.Equal(before.Description, property.Description);
        Assert.Equal(before.City, property.City);
        Assert.True(property.IsActive);
    }

    [Fact]
    public void ApplyTo_EveryFieldSent_AppliesThem()
    {
        var property = StoredProperty();
        var otherPolicy = Guid.NewGuid();

        Read($$"""
            {
              "name": "Monolocale centro", "description": "", "address": "Via Po 2", "city": "Torino",
              "postalCode": "10123", "latitude": 45.07, "longitude": 7.68, "bedrooms": 0, "bathrooms": 1,
              "maxGuests": 2, "nightlyRate": 80.5, "cleaningFee": 35, "damageDeposit": 0,
              "amenities": ["Heating"], "houseRules": "", "timezone": "Europe/Rome",
              "cancellationPolicyId": "{{otherPolicy}}", "isActive": false, "cinCode": "IT001272A1ABCDEFGH",
              "slug": "Monolocale-Centro"
            }
            """).ApplyTo(property);

        Assert.Equal("Monolocale centro", property.Name);
        Assert.Equal(string.Empty, property.Description);
        Assert.Equal(0, property.Bedrooms);
        Assert.Equal(80.5m, property.NightlyRate);
        Assert.Equal(35m, property.CleaningFee);
        Assert.Equal(0m, property.DamageDeposit);
        Assert.Equal([PropertyAmenity.Heating], property.Amenities);
        Assert.Equal(string.Empty, property.HouseRules);
        Assert.Equal(otherPolicy, property.CancellationPolicyId);
        // PC-03, A2-05: isActive is no longer writable through the generic update, the property stays visible.
        Assert.True(property.IsActive);
        Assert.Equal("IT001272A1ABCDEFGH", property.CinCode);
        Assert.Equal("monolocale-centro", property.Slug);
        // Not in the body: kept.
        Assert.Equal(["https://cdn.example/1.jpg"], property.PhotoUrls);
    }

    [Fact]
    public void ApplyTo_IsActiveFalseSent_IsIgnoredAndKeepsThePropertyVisibleAndUnpaused()
    {
        // PC-03, A2-05: the old form checkbox / "Pausa" toggle sent { isActive: false } and the property vanished
        // from its own host's list and detail. Pausing is the dedicated pause/activate action only.
        var property = StoredProperty();

        Read("""{ "isActive": false, "isPaused": true, "pausedAt": "2026-01-01T00:00:00Z" }""").ApplyTo(property);

        Assert.True(property.IsActive);
        Assert.False(property.IsPaused);
        Assert.Null(property.PausedAt);
    }

    [Fact]
    public void ApplyTo_UnitSent_IsStoredTrimmedWithSpacesCollapsed()
    {
        var property = StoredProperty();

        Read("""{ "unit": "  Scala  B   int. 5 " }""").ApplyTo(property);

        Assert.Equal("Scala B int. 5", property.Unit);
    }

    [Fact]
    public void ApplyTo_UnitLeftOut_KeepsTheStoredUnit()
    {
        var property = StoredProperty();
        property.Unit = "int. 2";

        Read("""{ "name": "Altro nome" }""").ApplyTo(property);

        Assert.Equal("int. 2", property.Unit);
    }

    [Theory]
    [InlineData("""{ "unit": null }""")]
    [InlineData("""{ "unit": "   " }""")]
    public void ApplyTo_UnitSentBlankOrNull_ClearsIt(string body)
    {
        var property = StoredProperty();
        property.Unit = "int. 2";

        Read(body).ApplyTo(property);

        Assert.Null(property.Unit);
    }

    [Fact]
    public void ApplyTo_CoordinatesWithMoreThanSixDecimals_AreRoundedToTheStoredPrecision()
    {
        var property = StoredProperty();

        Read("""{ "latitude": 41.90278249, "longitude": 12.49636651 }""").ApplyTo(property);

        Assert.Equal(41.902782m, property.Latitude);
        Assert.Equal(12.496367m, property.Longitude);
    }

    [Theory]
    [InlineData("""{ "latitude": 90.5 }""", "PropertyLatitudeRange")]
    [InlineData("""{ "latitude": -91 }""", "PropertyLatitudeRange")]
    [InlineData("""{ "longitude": 181 }""", "PropertyLongitudeRange")]
    [InlineData("""{ "longitude": -4500.5 }""", "PropertyLongitudeRange")]
    [InlineData("""{ "unit": "123456789012345678901234567890X" }""", "PropertyUnitTooLong")]
    public void Validate_CoordinateOutOfRangeOrUnitTooLong_ReportsTheResourceKey(string body, string expectedKey)
    {
        var request = Read(body);
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.ErrorMessage == expectedKey);
    }

    [Fact]
    public void Validate_CoordinatesAtTheLimits_AreValid()
    {
        var request = Read("""{ "latitude": -90, "longitude": 180, "unit": "int. 5" }""");

        Assert.True(Validator.TryValidateObject(request, new ValidationContext(request), [], validateAllProperties: true));
    }

    [Fact]
    public void ToProperty_UnitAndCoordinates_AreNormalizedAndRounded()
    {
        var request = JsonSerializer.Deserialize<CreatePropertyRequest>(
            """{ "name": "Casa", "address": "Via Roma 1", "unit": " int.  5 ", "city": "Rimini", "latitude": 41.9027825, "longitude": 12.4963664 }""",
            WebJson)!;

        var property = request.ToProperty("auth0|owner");

        Assert.Equal("int. 5", property.Unit);
        Assert.Equal(41.902783m, property.Latitude);
        Assert.Equal(12.496366m, property.Longitude);
    }

    [Fact]
    public void Validate_CreateWithLatitudeOutOfRange_ReportsTheResourceKey()
    {
        var request = JsonSerializer.Deserialize<CreatePropertyRequest>(
            """{ "name": "Casa", "address": "Via Roma 1", "city": "Rimini", "latitude": 120 }""", WebJson)!;
        var results = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true));
        Assert.Contains(results, r => r.ErrorMessage == "PropertyLatitudeRange");
    }

    [Fact]
    public void ApplyTo_PhotoUrlsSent_IsIgnoredBecauseTheGalleryHasItsOwnEndpoints()
    {
        // PC-04, A2-26: a client could otherwise point the public page at any URL, or at another property's photo
        // (which a later delete from the gallery would then remove from the storage).
        var property = StoredProperty();

        Read("""{ "photoUrls": ["https://evil.example/x.jpg", "https://cdn.example/other-property/2.jpg"] }""").ApplyTo(property);

        Assert.Equal(["https://cdn.example/1.jpg"], property.PhotoUrls);
    }

    [Fact]
    public void ToProperty_PhotoUrlsSent_StartsWithAnEmptyGallery()
    {
        var request = JsonSerializer.Deserialize<CreatePropertyRequest>(
            """{ "name": "Casa", "address": "Via Roma 1", "city": "Rimini", "photoUrls": ["https://evil.example/x.jpg"] }""",
            WebJson)!;

        Assert.Empty(request.ToProperty("auth0|owner").PhotoUrls);
    }

    [Fact]
    public void ApplyTo_NullableFieldsSentAsNull_ClearsThem()
    {
        var property = StoredProperty();

        Read("""{ "cancellationPolicyId": null, "cinCode": null, "slug": null }""").ApplyTo(property);

        Assert.Null(property.CancellationPolicyId);
        Assert.Null(property.CinCode);
        Assert.Null(property.Slug);
    }

    [Fact]
    public void ApplyTo_RequiredFieldsSentAsNull_KeepsThem()
    {
        var property = StoredProperty();

        Read("""{ "name": null, "cleaningFee": null, "timezone": null, "isActive": null }""").ApplyTo(property);

        Assert.Equal("Casa al mare", property.Name);
        Assert.Equal(60m, property.CleaningFee);
        Assert.Equal("Europe/Rome", property.Timezone);
        Assert.True(property.IsActive);
    }

    [Fact]
    public void ApplyTo_NeverTouchesServerManagedFields()
    {
        var property = StoredProperty();
        var (id, ownerId, orgId) = (property.Id, property.OwnerId, property.OrgId);

        Read($$"""{ "id": "{{Guid.NewGuid()}}", "ownerId": "auth0|attacker", "orgId": "{{Guid.NewGuid()}}" }""").ApplyTo(property);

        Assert.Equal(id, property.Id);
        Assert.Equal(ownerId, property.OwnerId);
        Assert.Equal(orgId, property.OrgId);
    }

    [Theory]
    [InlineData("""{ "name": "   " }""", nameof(UpdatePropertyRequest.Name))]
    [InlineData("""{ "address": "" }""", nameof(UpdatePropertyRequest.Address))]
    [InlineData("""{ "bedrooms": -1 }""", nameof(UpdatePropertyRequest.Bedrooms))]
    [InlineData("""{ "bathrooms": 0 }""", nameof(UpdatePropertyRequest.Bathrooms))]
    [InlineData("""{ "cleaningFee": -0.5 }""", nameof(UpdatePropertyRequest.CleaningFee))]
    [InlineData("""{ "damageDeposit": 50000.01 }""", nameof(UpdatePropertyRequest.DamageDeposit))]
    [InlineData("""{ "timezone": "Mars/Olympus" }""", nameof(UpdatePropertyRequest.Timezone))]
    [InlineData("""{ "houseRules": "x" }""", null)]
    public void Validate_SentFields_AreValidatedAndAbsentOnesAreNot(string json, string? invalidMember)
    {
        var request = Read(json);
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        if (invalidMember is null)
            Assert.Empty(results);
        else
            Assert.Contains(invalidMember, Assert.Single(results).MemberNames);
    }

    [Fact]
    public void Validate_StudioWithoutBedrooms_IsValid()
    {
        var request = Read("""{ "bedrooms": 0, "bathrooms": 1 }""");
        var results = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true));
    }

    // ─── Minimum stay and weekend surcharge (DB-03) ─────────────────────────────────────────────────

    [Fact]
    public void ApplyTo_MinNightsAndSurchargeLeftOut_KeepsTheStoredValues()
    {
        var property = StoredProperty();
        property.MinNights = 3;
        property.WeekendSurchargePercent = 15m;

        Read("""{ "name": "Casa al mare rinnovata" }""").ApplyTo(property);

        Assert.Equal(3, property.MinNights);
        Assert.Equal(15m, property.WeekendSurchargePercent);
    }

    [Fact]
    public void ApplyTo_MinNightsAndSurchargeSent_AppliesThem()
    {
        var property = StoredProperty();

        Read("""{ "minNights": 2, "weekendSurchargePercent": 12.5 }""").ApplyTo(property);

        Assert.Equal(2, property.MinNights);
        Assert.Equal(12.5m, property.WeekendSurchargePercent);
    }

    [Fact]
    public void ApplyTo_MinNightsSentAsNull_RemovesTheMinimum()
    {
        var property = StoredProperty();
        property.MinNights = 4;

        var request = Read("""{ "minNights": null }""");
        request.ApplyTo(property);

        Assert.True(request.MinNightsSent);
        Assert.Null(property.MinNights);
    }

    [Fact]
    public void ApplyTo_SurchargeSentAsZero_TurnsItOffAndNullKeepsIt()
    {
        var property = StoredProperty();
        property.WeekendSurchargePercent = 15m;

        Read("""{ "weekendSurchargePercent": null }""").ApplyTo(property);
        Assert.Equal(15m, property.WeekendSurchargePercent);

        Read("""{ "weekendSurchargePercent": 0 }""").ApplyTo(property);
        Assert.Equal(0m, property.WeekendSurchargePercent);
    }

    [Theory]
    [InlineData("""{ "minNights": 0 }""", nameof(UpdatePropertyRequest.MinNights))]
    [InlineData("""{ "minNights": 31 }""", nameof(UpdatePropertyRequest.MinNights))]
    [InlineData("""{ "minNights": -2 }""", nameof(UpdatePropertyRequest.MinNights))]
    [InlineData("""{ "weekendSurchargePercent": -1 }""", nameof(UpdatePropertyRequest.WeekendSurchargePercent))]
    [InlineData("""{ "weekendSurchargePercent": 100.5 }""", nameof(UpdatePropertyRequest.WeekendSurchargePercent))]
    [InlineData("""{ "minNights": null }""", null)]
    [InlineData("""{ "minNights": 1 }""", null)]
    [InlineData("""{ "minNights": 30 }""", null)]
    [InlineData("""{ "weekendSurchargePercent": 100 }""", null)]
    [InlineData("""{ "weekendSurchargePercent": 0 }""", null)]
    public void Validate_StayRules_AreBoundedAndNullIsAllowed(string json, string? invalidMember)
    {
        var request = Read(json);
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        if (invalidMember is null)
            Assert.Empty(results);
        else
            Assert.Contains(invalidMember, Assert.Single(results).MemberNames);
    }

    [Fact]
    public void ToProperty_CreateWithStayRules_CarriesThemAndTheDefaultsChangeNothing()
    {
        var withRules = new CreatePropertyRequest
        {
            Name = "Casa",
            Address = "Via Roma 1",
            City = "Rimini",
            MinNights = 3,
            WeekendSurchargePercent = 15m,
        }.ToProperty("auth0|owner");
        var withDefaults = new CreatePropertyRequest { Name = "Casa", Address = "Via Roma 1", City = "Rimini" }.ToProperty("auth0|owner");

        Assert.Equal(3, withRules.MinNights);
        Assert.Equal(15m, withRules.WeekendSurchargePercent);
        Assert.Null(withDefaults.MinNights);
        Assert.Equal(0m, withDefaults.WeekendSurchargePercent);
    }
}
