using System.ComponentModel.DataAnnotations;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

public class CinCodeAttributeTests
{
    private sealed class Model
    {
        [CinCode]
        public string? CinCode { get; set; }
    }

    private static List<ValidationResult> Validate(string? cin)
    {
        var model = new Model { CinCode = cin };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IT058091C27G5FFZDZ")]
    [InlineData("IT-058091-C2-7G5FFZDZ")]
    [InlineData("it 058091 c2 7g5ffzdz")]
    public void IsValid_MissingOrOfficialFormat_Passes(string? cin)
    {
        Assert.Empty(Validate(cin));
    }

    [Theory]
    [InlineData("IT-12345-1234567890")]
    [InlineData("015146-CNI-01894")]
    public void IsValid_OldFormatOrRegionalCode_FailsWithLocalizableMessageKey(string cin)
    {
        var error = Assert.Single(Validate(cin));
        Assert.Equal(CinFormat.InvalidFormatMessageKey, error.ErrorMessage);
    }
}
