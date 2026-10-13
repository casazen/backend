using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The API's only JSON converter for enums (PL-07, A1-35, A7-31): reads and writes member names like
/// <see cref="JsonStringEnumConverter"/>, but refuses a value no member declares. On its own the standard converter
/// accepts any JSON integer (<c>7</c>) and any numeric string (<c>"7"</c>) and hands it over as an undefined value that
/// nothing downstream handles: an exhaustive <c>switch</c> throws (500) or the value is persisted as is.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Accepted: a member name (case-insensitive) or the number of a declared member, as before.</item>
/// <item>Refused: a number or numeric string outside the declared members. The <see cref="JsonException"/> becomes a
/// field error of the 400 <c>validation_error</c> ProblemDetails, with the localized message <c>EnumValueInvalid</c>
/// (the allowed names). A <c>[Flags]</c> enum accepts any combination of its declared bits.</item>
/// <item>Writing is unchanged (member names).</item>
/// </list>
/// Values parsed by hand from a string (query, route, string DTO field) use
/// <see cref="Casazen.Core.Validation.EnumNames.TryParseDefined{TEnum}"/> instead.
/// </remarks>
public sealed class DefinedEnumJsonConverterFactory(IStringLocalizer localizer) : JsonConverterFactory
{
    public const string MessageKey = "EnumValueInvalid";

    private readonly JsonStringEnumConverter _names = new();

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = _names.CreateConverter(typeToConvert, options);
        var converterType = typeof(DefinedEnumJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType, inner, localizer)!;
    }

    private sealed class DefinedEnumJsonConverter<TEnum>(JsonConverter<TEnum> inner, IStringLocalizer localizer)
        : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly bool IsFlags = typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false);

        private static readonly ulong DeclaredBits =
            IsFlags ? Enum.GetValues<TEnum>().Aggregate(0UL, (bits, value) => bits | ToBits(value)) : 0UL;

        private static readonly string AllowedNames = string.Join(", ", Enum.GetNames<TEnum>());

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            EnsureDefined(inner.Read(ref reader, typeToConvert, options));

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            inner.Write(writer, value, options);

        public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            EnsureDefined(inner.ReadAsPropertyName(ref reader, typeToConvert, options));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            inner.WriteAsPropertyName(writer, value, options);

        private TEnum EnsureDefined(TEnum value)
        {
            if (IsDeclared(value))
                return value;

            // Thrown with a message: System.Text.Json adds the path, MVC turns it into the field error of the 400.
            throw new JsonException(localizer[MessageKey, AllowedNames].Value);
        }

        private static bool IsDeclared(TEnum value) =>
            IsFlags ? (ToBits(value) & ~DeclaredBits) == 0 : Enum.IsDefined(value);

        // Any underlying type, negative values included (their two's complement bits).
        private static ulong ToBits(TEnum value) =>
            Type.GetTypeCode(typeof(TEnum)) == TypeCode.UInt64
                ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
                : unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }
}
