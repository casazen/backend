using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed record RoomBed(BedType Type, int Count);

[Table("PropertyRooms")]
public class PropertyRoom : ITenantOwned
{
    public const int MaxPerProperty = 20;

    public const int MinBedsOfAKind = 1;

    public const int MaxBedsOfAKind = 10;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public int Position { get; set; }

    public PropertyRoomType Type { get; set; }

    [Required]
    [Column(TypeName = "jsonb")]
    public string BedsJson { get; set; } = RoomBeds.None;

    [NotMapped]
    public IReadOnlyList<RoomBed> Beds
    {
        get => RoomBeds.Parse(BedsJson);
        set => BedsJson = RoomBeds.Serialize(value);
    }
}

public static class RoomBeds
{
    public const string None = "[]";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<RoomBed> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var stored = JsonSerializer.Deserialize<List<RoomBed>>(json, Options) ?? [];
            return [.. stored.Where(bed => bed is not null && Enum.IsDefined(bed.Type) && bed.Count >= PropertyRoom.MinBedsOfAKind)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<RoomBed> beds)
    {
        ArgumentNullException.ThrowIfNull(beds);
        return JsonSerializer.Serialize(beds.ToList(), Options);
    }

    public static int Total(IEnumerable<RoomBed> beds)
    {
        ArgumentNullException.ThrowIfNull(beds);
        return beds.Sum(bed => bed.Count);
    }
}
