using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Web.DTOs.CheckIn;
using Xunit;

namespace Casazen.Tests.Unit.Contracts;

/// <summary>
/// Contract between the guest portal form (frontend <c>PublicCheckInSubmitRequest</c>) and the API DTO (A5-04:
/// the API started requiring <c>gender</c> and the form never sent it). The frontend mirror is
/// <c>src/features/checkin/__tests__/checkin-contract.test.ts</c>: it checks that the form produces exactly
/// <c>Fixtures/public-checkin-submit.frontend.json</c> and sends every field listed here.
/// </summary>
public class PublicCheckInSubmitContractTests
{
    /// <summary>Same list as <c>BACKEND_REQUIRED_FIELDS</c> in the frontend contract test: change both together.</summary>
    private static readonly string[] FrontendRequiredFields =
    [
        "dateOfBirth",
        "documentIssuingCountry",
        "documentNumber",
        "documentType",
        "firstName",
        "gdprConsent",
        "gender",
        "lastName",
        "nationality",
        "placeOfBirth",
    ];

    [Fact]
    public void RequiredProperties_Always_MatchFrontendContractList()
    {
        var required = typeof(PublicCheckInSubmitRequest)
            .GetProperties()
            .Where(property => property.GetCustomAttribute<RequiredAttribute>() is not null)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(FrontendRequiredFields, required);
    }

    [Fact]
    public void FrontendSamplePayload_Deserialized_PassesValidationWithEveryRequiredField()
    {
        var json = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "public-checkin-submit.frontend.json"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var request = JsonSerializer.Deserialize<PublicCheckInSubmitRequest>(json, options);

        Assert.NotNull(request);
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        Assert.True(valid, string.Join(", ", results.SelectMany(result => result.MemberNames)));
        Assert.Equal(Gender.Female, request.Gender);
        Assert.True(request.GdprConsent);

        using var document = JsonDocument.Parse(json);
        foreach (var field in FrontendRequiredFields)
            Assert.True(document.RootElement.TryGetProperty(field, out _), $"Frontend sample misses required field '{field}'.");
    }
}
