using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>One kind of bed in a room and how many of them (NF-34): <c>{ "type": "Double", "count": 2 }</c> in the beds of a room.</summary>
public sealed record RoomBed(BedType Type, int Count);

/// <summary>
/// A room of a unit with its beds (NF-34, AC-NF34-1): the rooms are the detail the channels ask for behind the counters of the property
/// (<see cref="Property.Bedrooms"/>, <see cref="Property.Beds"/>). A property with no row has only the counters, which is every property that
/// existed before the table. Saving the rooms sets the counters from them, in the same save (<c>IPropertyRoomService</c>).
/// </summary>
/// <remarks>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2).</remarks>
[Table("PropertyRooms")]
public class PropertyRoom : ITenantOwned
{
    /// <summary>At most this many rooms a unit can list.</summary>
    public const int MaxPerProperty = 20;

    /// <summary>Fewest beds of one kind in a room (a kind that is not there is simply not listed).</summary>
    public const int MinBedsOfAKind = 1;

    /// <summary>Most beds of one kind in a room.</summary>
    public const int MaxBedsOfAKind = 10;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>Order in the list of the rooms, 0 first; the host reorders them.</summary>
    public int Position { get; set; }

    public PropertyRoomType Type { get; set; }

    /// <summary>The beds of the room as JSON (<see cref="RoomBeds"/>): a list of <see cref="RoomBed"/>; <c>[]</c> = a room with no bed.</summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string BedsJson { get; set; } = RoomBeds.None;

    /// <summary>The beds of the room, read from <see cref="BedsJson"/>.</summary>
    [NotMapped]
    public IReadOnlyList<RoomBed> Beds
    {
        get => RoomBeds.Parse(BedsJson);
        set => BedsJson = RoomBeds.Serialize(value);
    }
}

/// <summary>Reads and writes the beds of a room (<c>PropertyRooms.BedsJson</c>): kinds by name, so a column is readable without the enum.</summary>
public static class RoomBeds
{
    /// <summary>The beds of a room with none.</summary>
    public const string None = "[]";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The beds in a column. A column that cannot be read, or lists a kind that does not exist or a count below one, gives what can be read: never a failure.</summary>
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

    /// <summary>How many beds in all.</summary>
    public static int Total(IEnumerable<RoomBed> beds)
    {
        ArgumentNullException.ThrowIfNull(beds);
        return beds.Sum(bed => bed.Count);
    }
}
