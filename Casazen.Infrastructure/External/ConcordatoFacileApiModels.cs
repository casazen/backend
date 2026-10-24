using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Practice detail from the ConcordatoFacile status API.
/// </summary>
public sealed record PracticeDto
{
    [JsonPropertyName("practiceId")]
    public required string PracticeId { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(CfPracticeStatusJsonConverter))]
    public required CfPracticeStatus Status { get; init; }

    [JsonPropertyName("statusChangedAt")]
    public required DateTime StatusChangedAt { get; init; }

    [JsonPropertyName("submittedAt")]
    public DateTime? SubmittedAt { get; init; }

    [JsonPropertyName("comune")]
    public string? Comune { get; init; }

    [JsonPropertyName("comuneDiffersFromLink")]
    public bool ComuneDiffersFromLink { get; init; }

    [JsonPropertyName("closeReason")]
    public string? CloseReason { get; init; }

    [JsonPropertyName("leaseId")]
    public Guid? LeaseId { get; init; }
}

/// <summary>
/// JSON converter for <see cref="CfPracticeStatus"/> that maps unknown string values to <see cref="CfPracticeStatus.Unknown"/>
/// without throwing (ADR-008-R13, ADR-008-R21). ConcordatoFacile may add new status values over time.
/// </summary>
internal sealed class CfPracticeStatusJsonConverter : JsonConverter<CfPracticeStatus>
{
    public override CfPracticeStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string for CfPracticeStatus, got {reader.TokenType}");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return CfPracticeStatus.Unknown;
        }

        // Try to parse the value; if it fails or is not defined, return Unknown
        if (Enum.TryParse<CfPracticeStatus>(value, ignoreCase: true, out var status) && Enum.IsDefined(status))
        {
            return status;
        }

        return CfPracticeStatus.Unknown;
    }

    public override void Write(Utf8JsonWriter writer, CfPracticeStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
