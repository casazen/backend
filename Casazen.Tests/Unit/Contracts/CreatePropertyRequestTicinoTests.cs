using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Contracts;

/// <summary>
/// E29/F02/US01/T02 (NF-129, acceptance criterion 4): the request that creates a property accepts the «Numero identificativo LEAR / NO-LEAR» of the Ticino police portal with the
/// same validation and normalisation as the update, so the structure number is entered when the property is made and not only when the file turns out to lack it.
/// </summary>
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

    /// <summary>Creating a property with a structure number stores it normalised: spaces gone, uppercase; blank is none; left out is none.</summary>
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

    /// <summary>The same decision as the update: a value that is not 3 to 20 letters and digits with single hyphens is refused with the message key of the update.</summary>
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

    /// <summary>A value too long for the column is refused as well, before it can be cut.</summary>
    [Fact]
    public void Validation_ANumberOver40Characters_IsRefused()
    {
        var request = Read(Member(new string('A', 41)));

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(nameof(CreatePropertyRequest.TicinoStructureId)));
    }
}
