using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Body of <c>GET /v1/leases/{leaseId}/practices</c> in the ConcordatoFacile status API v0.2 (§4): an object with the lease and its
/// practices, newest first and at most 20, not a bare array. A lease with no practices, that ConcordatoFacile does not know or that is
/// not CasaZen's answers 200 with <c>"practices": []</c>.
/// </summary>
public sealed record LeasePracticesDto
{
    [JsonPropertyName("leaseId")]
    public Guid? LeaseId { get; init; }

    [JsonPropertyName("practices")]
    public List<PracticeDto>? Practices { get; init; }
}

/// <summary>
/// A practice of the ConcordatoFacile status API v0.2 (§4, §5): an element of <c>practices</c>, or the whole body of
/// <c>GET /v1/practices/{practiceId}</c>, which adds <see cref="LeaseId"/>. Fields, statuses and close reasons that are not known here
/// are ignored, as the specification asks.
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
    public bool? ComuneDiffersFromLink { get; init; }

    [JsonPropertyName("closeReason")]
    public string? CloseReason { get; init; }

    [JsonPropertyName("leaseId")]
    public Guid? LeaseId { get; init; }
}

/// <summary>
/// JSON converter for <see cref="CfPracticeStatus"/> that maps unknown string values to <see cref="CfPracticeStatus.Unknown"/>
/// without throwing (ADR-008-R13, ADR-008-R21). ConcordatoFacile may add new status values over time (v0.2 §4: unknown states are
/// ignored); a <c>null</c> status is read the same way.
/// </summary>
internal sealed class CfPracticeStatusJsonConverter : JsonConverter<CfPracticeStatus>
{
    public override CfPracticeStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return CfPracticeStatus.Unknown;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string for CfPracticeStatus, got {reader.TokenType}");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return CfPracticeStatus.Unknown;
        }

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
