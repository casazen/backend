using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Casazen.Tests.Unit.Serialization;

/// <summary>
/// PL-07 (A1-35, A7-31): the API's enum converter refuses values no member declares, so an enum-typed request field
/// set to <c>7</c> or <c>"7"</c> is a 400 instead of reaching the services as an undefined value (500 or bad data).
/// </summary>
public class DefinedEnumJsonConverterFactoryTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    [Flags]
    public enum TestFlags
    {
        None = 0,
        A = 1,
        B = 2,
    }

    public sealed record Body(RentalType Type, RentalType? Optional = null);

    [Theory]
    [InlineData("""{"type":"LongTerm"}""", RentalType.LongTerm)]
    [InlineData("""{"type":"both"}""", RentalType.Both)] // case-insensitive
    [InlineData("""{"type":1}""", RentalType.LongTerm)] // number of a declared member: accepted as before
    public void Read_DeclaredMember_ReturnsValue(string json, RentalType expected)
    {
        var body = JsonSerializer.Deserialize<Body>(json, Options)!;

        Assert.Equal(expected, body.Type);
    }

    [Theory]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":-1}""")]
    [InlineData("""{"type":"7"}""")]
    [InlineData("""{"type":"Unknown"}""")]
    [InlineData("""{"type":"ShortTerm","optional":9}""")] // Nullable<TEnum> too
    public void Read_UndeclaredValue_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>(json, Options));
    }

    [Fact]
    public void Read_NullForNullableEnum_ReturnsNull()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"type":"ShortTerm","optional":null}""", Options)!;

        Assert.Null(body.Optional);
    }

    [Fact]
    public void Read_UndeclaredDictionaryKey_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Dictionary<RentalType, int>>("""{"7":1}""", Options));
    }

    [Theory]
    [InlineData("3", TestFlags.A | TestFlags.B)]
    [InlineData("\"A, B\"", TestFlags.A | TestFlags.B)]
    public void Read_FlagsCombinationOfDeclaredBits_ReturnsValue(string json, TestFlags expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<TestFlags>(json, Options));
    }

    [Fact]
    public void Read_FlagsWithUndeclaredBit_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TestFlags>("4", Options));
    }

    [Fact]
    public void Read_UndeclaredValue_MessageIsLocalizedWithAllowedNames()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var english = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>("""{"type":7}""", Options));
            CultureInfo.CurrentUICulture = new CultureInfo("it");
            var italian = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>("""{"type":7}""", Options));

            Assert.Equal("Value not allowed. Allowed values: ShortTerm, LongTerm, Both.", english.Message);
            Assert.Equal("Valore non ammesso. Valori consentiti: ShortTerm, LongTerm, Both.", italian.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Write_Value_WritesMemberName()
    {
        var json = JsonSerializer.Serialize(new Body(RentalType.Both, RentalType.ShortTerm), Options);

        Assert.Equal("""{"Type":"Both","Optional":"ShortTerm"}""", json);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var localizerFactory = new ServiceCollection()
            .AddLogging()
            .AddLocalization()
            .BuildServiceProvider()
            .GetRequiredService<IStringLocalizerFactory>();

        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = null,
            Converters = { new DefinedEnumJsonConverterFactory(localizerFactory.Create(typeof(SharedResources))) },
        };
    }
}
