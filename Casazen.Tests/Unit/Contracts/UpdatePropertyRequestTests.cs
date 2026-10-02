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
}
