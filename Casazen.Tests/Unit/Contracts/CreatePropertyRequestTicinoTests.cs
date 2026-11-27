using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Contracts;

public class CreatePropertyRequestTicinoTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CreatePropertyRequest Read(string? structureIdMember)
    {
        var member = structureIdMember is null ? string.Empty : $",{structureIdMember}";
        var json = $$"""{ "name": "Casa Lugano", "address": "Via Nassa 1", "city": "Lugano", "postalCode": "6900", "bathrooms": 1, "maxGuests": 4, "nightlyRate": 150{{member}} }""";
        return JsonSerializer.Deserialize<CreatePropertyRequest>(json, WebJson)!;
    }

    private static string Member(string? value) => $"\"ticinoStructureId\": {JsonSerializer.Serialize(value)}";

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData(" nl-12345678 ", "NL-12345678")]
    [InlineData("xna9999", "XNA9999")]
    [InlineData("NL-99999999", "NL-99999999")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ToProperty_StoresTheStructureNumberNormalised(string? sent, string? expected)
    {
        var request = Read(Member(sent));

        Assert.Empty(Validate(request));

        Assert.Equal(expected, request.ToProperty("auth0|owner").TicinoStructureId);
    }

    [Fact]
    public void ToProperty_ANumberLeftOut_IsNone()
    {
        var request = Read(null);

        Assert.Empty(Validate(request));
        Assert.Null(request.TicinoStructureId);
        Assert.Null(request.ToProperty("auth0|owner").TicinoStructureId);
    }

    [Theory]
    [InlineData("not valid!")]
    [InlineData("NL--123")]
    [InlineData("-NL123")]
    [InlineData("AB")]
    [InlineData("NL-1234567890123456789")]
    public void Validation_ARefusedNumber_IsRefusedLikeTheUpdate(string value)
    {
        var create = Read(Member(value));
        var update = JsonSerializer.Deserialize<UpdatePropertyRequest>($"{{ {Member(value)} }}", WebJson)!;

        var created = Validate(create).Where(r => r.MemberNames.Contains(nameof(CreatePropertyRequest.TicinoStructureId))).ToList();
        var updated = Validate(update).Where(r => r.MemberNames.Contains(nameof(UpdatePropertyRequest.TicinoStructureId))).ToList();

        Assert.Equal("TicinoStructureIdInvalid", Assert.Single(created).ErrorMessage);
        Assert.Equal(updated.Select(r => r.ErrorMessage), created.Select(r => r.ErrorMessage));
    }

    [Fact]
    public void Validation_ANumberOver40Characters_IsRefused()
    {
        var request = Read(Member(new string('A', 41)));

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(nameof(CreatePropertyRequest.TicinoStructureId)));
    }
}
