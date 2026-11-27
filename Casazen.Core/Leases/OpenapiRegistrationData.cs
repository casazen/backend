using System.Text.Json;

namespace Casazen.Core.Leases;

public sealed record OpenapiFieldOption(string Code, string Description);
public sealed record OpenapiRegistrationField(string Key, string Name, string Label, string Type, bool Required,
    string Help, IReadOnlyList<OpenapiFieldOption> Options, string Group);
public sealed record OpenapiRegistrationSchema(string DocumentId, string VerifiedOn,
    IReadOnlyList<OpenapiRegistrationField> Fields, string Validation)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static OpenapiRegistrationSchema Current { get; } = Load();
    private static OpenapiRegistrationSchema Load()
    {
        using var stream = typeof(OpenapiRegistrationSchema).Assembly.GetManifestResourceStream(
            "Casazen.Core.Leases.OpenapiRegistrationSchema.json")
            ?? throw new InvalidOperationException("Missing verified DocuEngine schema.");
        return JsonSerializer.Deserialize<OpenapiRegistrationSchema>(stream, Json)!;
    }
}

public sealed class OpenapiRegistrationData
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Guid> Documents { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static OpenapiRegistrationData Read(string? value) => string.IsNullOrEmpty(value)
        ? new() : JsonSerializer.Deserialize<OpenapiRegistrationData>(value, Json) ?? new();
    public string Serialize() => JsonSerializer.Serialize(this, Json);
}

public sealed record OpenapiRegistrationIssue(string Field, string Message);
public sealed record OpenapiPreparationResult(OpenapiRegistrationSchema Schema, OpenapiRegistrationData Data,
    IReadOnlyList<OpenapiRegistrationIssue> Issues, bool Ready, bool Paid);
