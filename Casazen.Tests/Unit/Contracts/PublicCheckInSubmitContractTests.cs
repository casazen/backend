using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Web.DTOs.CheckIn;
using Xunit;

namespace Casazen.Tests.Unit.Contracts;

/// <summary>
/// Contract between the guest portal form (frontend <c>PublicCheckInSubmitRequest</c> / <c>StayGuestSubmit</c>) and the
/// API DTOs (A5-04: the API started requiring <c>gender</c> and the form never sent it; CO-12: one entry per guest of
/// the stay). The frontend mirror is <c>src/features/checkin/__tests__/checkin-contract.test.ts</c>: it checks that the
/// form produces exactly <c>Fixtures/public-checkin-submit.frontend.json</c> and sends every field listed here.
/// </summary>
public class PublicCheckInSubmitContractTests
{
    /// <summary>Same list as <c>BACKEND_REQUIRED_FIELDS</c> in the frontend contract test: change both together.</summary>
    private static readonly string[] FrontendRequiredFields = ["gdprConsent", "guests"];

    /// <summary>Same list as <c>BACKEND_REQUIRED_GUEST_FIELDS</c> in the frontend contract test.</summary>
    private static readonly string[] FrontendRequiredGuestFields =
    [
        "bornInItaly",
        "citizenshipName",
        "dateOfBirth",
        "firstName",
        "gender",
        "lastName",
        "type",
    ];

    [Fact]
    public void RequiredProperties_Always_MatchFrontendContractList()
    {
        Assert.Equal(FrontendRequiredFields, RequiredJsonNames(typeof(PublicCheckInSubmitRequest)));
        Assert.Equal(FrontendRequiredGuestFields, RequiredJsonNames(typeof(StayGuestSubmitDto)));
    }

    [Fact]
    public void FrontendSamplePayload_Deserialized_PassesValidationWithEveryRequiredField()
    {
        var json = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "public-checkin-submit.frontend.json"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var request = JsonSerializer.Deserialize<PublicCheckInSubmitRequest>(json, options);

        Assert.NotNull(request);
        AssertValid(request);
        Assert.True(request.GdprConsent);
        Assert.Equal(2, request.Guests.Count);
        foreach (var guest in request.Guests)
            AssertValid(guest);
        Assert.Equal(("HeadOfFamily", Gender.Female), (request.Guests[0].Type, request.Guests[0].Gender));
        Assert.Equal(("FamilyMember", Gender.Male), (request.Guests[1].Type, request.Guests[1].Gender));

        using var document = JsonDocument.Parse(json);
        foreach (var field in FrontendRequiredFields)
            Assert.True(document.RootElement.TryGetProperty(field, out _), $"Frontend sample misses required field '{field}'.");
        foreach (var guest in document.RootElement.GetProperty("guests").EnumerateArray())
        {
            foreach (var field in FrontendRequiredGuestFields)
                Assert.True(guest.TryGetProperty(field, out _), $"Frontend sample guest misses required field '{field}'.");
        }
    }

    private static string[] RequiredJsonNames(Type type) =>
        type.GetProperties()
            .Where(property => property.GetCustomAttribute<RequiredAttribute>() is not null)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertValid(object instance)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        Assert.True(valid, string.Join(", ", results.SelectMany(result => result.MemberNames)));
    }
}
