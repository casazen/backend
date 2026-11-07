using System.ComponentModel.DataAnnotations;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

public class InternationalPhoneAttributeTests
{
    private sealed class Model
    {
        [InternationalPhone]
        public string? Phone { get; set; }
    }

    private static List<ValidationResult> Validate(string? phone)
    {
        var model = new Model { Phone = phone };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("+393331234567")]
    [InlineData("+39 333 1234567")]
    [InlineData("+39 (333) 123-4567")]
    public void IsValid_MissingOrInternational_Passes(string? phone)
    {
        Assert.Empty(Validate(phone));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+1")]
    [InlineData("+0123456789")]
    [InlineData("3331234567")]
    public void IsValid_ShortLeadingZeroOrNotInternational_FailsWithThePhoneKey(string phone)
    {
        var error = Assert.Single(Validate(phone));
        Assert.Equal(InternationalPhoneAttribute.MessageKey, error.ErrorMessage);
    }
}
