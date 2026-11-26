using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Ross1000;

public sealed record Ross1000Capacity(bool Open, int OccupiedRooms, int AvailableRooms, int AvailableBeds);
public sealed class Ross1000Problem(string code) : Exception(code);
public sealed record Ross1000Movement(XElement Xml, string[] RecordKeys, int Arrivals, int Departures)
{
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Xml.ToString(SaveOptions.DisableFormatting))));
}

public static class Ross1000MovementBuilder
{
    private const string Italy = "100000100";

    public static Ross1000Movement Build(string structure, DateOnly day, DateOnly effectiveFrom, Ross1000Capacity capacity,
        IEnumerable<Booking> bookings)
    {
        if (capacity is not { OccupiedRooms: >= 0, AvailableRooms: >= 0, AvailableBeds: >= 0 } ||
            capacity.OccupiedRooms > capacity.AvailableRooms || capacity.AvailableBeds < capacity.AvailableRooms ||
            (!capacity.Open && (capacity.AvailableRooms != 0 || capacity.AvailableBeds != 0)) ||
            (capacity.Open && capacity.AvailableRooms == 0))
            throw new Ross1000Problem("invalid_capacity");

        var arrivals = new List<XElement>();
        var departures = new List<XElement>();
        var keys = new List<string>();
        foreach (var booking in bookings.OrderBy(b => b.Id))
        {
            if (!booking.ArrivedAt.HasValue || booking.NoShowAt.HasValue || booking.Status == BookingStatus.Cancelled) continue;
            var arrival = RomeCalendar.DateInRome(booking.ArrivedAt.Value);
            var departure = booking.CheckedOutAt.HasValue ? RomeCalendar.DateInRome(booking.CheckedOutAt.Value) : (DateOnly?)null;
            if (arrival != day && departure != day) continue;
            if (arrival < effectiveFrom) throw new Ross1000Problem("legacy_checkout");
            if (booking.StayGuests.Count == 0 || booking.StayGuests.Count != booking.NumberOfGuests)
                throw new Ross1000Problem("guest_count_incomplete");
            string? head = null;
            StayGuestType? headType = null;
            foreach (var guest in booking.StayGuests.OrderBy(g => g.Position))
            {
                var id = GuestId(guest.Id);
                var type = guest.Type switch
                {
                    StayGuestType.SingleGuest => "16",
                    StayGuestType.HeadOfFamily => "17",
                    StayGuestType.HeadOfGroup => "18",
                    StayGuestType.FamilyMember => "19",
                    StayGuestType.GroupMember => "20",
                    _ => throw new Ross1000Problem("invalid_guest")
                };
                if (arrival == day)
                {
                    if (guest.Type is StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup)
                    {
                        head = id;
                        headType = guest.Type;
                    }
                    if (guest.Type == StayGuestType.SingleGuest) headType = null;
                    bool member = guest.Type is StayGuestType.FamilyMember or StayGuestType.GroupMember;
                    if (member && headType != (guest.Type == StayGuestType.FamilyMember ? StayGuestType.HeadOfFamily : StayGuestType.HeadOfGroup))
                        throw new Ross1000Problem("invalid_group");
                    arrivals.Add(Arrival(guest, id, type, member ? head : null));
                    keys.Add("arrivo/" + id);
                }
                if (departure == day)
                {
                    departures.Add(new XElement("partenza", new XElement("idswh", id), new XElement("tipoalloggiato", type),
                        new XElement("arrivo", DateCode(arrival))));
                    keys.Add("partenza/" + id);
                }
            }
        }

        if ((!capacity.Open && arrivals.Count > 0) || (arrivals.Count > 0 && capacity.OccupiedRooms == 0 && departures.Count == 0))
            throw new Ross1000Problem("invalid_capacity");
        var activity = new XElement("struttura", new XElement("apertura", capacity.Open ? "SI" : "NO"),
            new XElement("camereoccupate", capacity.OccupiedRooms), new XElement("cameredisponibili", capacity.AvailableRooms),
            new XElement("lettidisponibili", capacity.AvailableBeds));
        var xml = new XElement("movimentazione", new XElement("codice", structure), new XElement("prodotto", "CasaZen"),
            new XElement("movimento", new XElement("data", DateCode(day)), activity,
                arrivals.Count == 0 ? null : new XElement("arrivi", arrivals),
                departures.Count == 0 ? null : new XElement("partenze", departures)));
        return new Ross1000Movement(xml, keys.Order(StringComparer.Ordinal).ToArray(), arrivals.Count, departures.Count);
    }

    internal static string DateCode(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string GuestId(Guid id) => Convert.ToHexString(SHA256.HashData(id.ToByteArray()))[..20];

    private static XElement Arrival(StayGuest g, string id, string type, string? head)
    {
        string citizenship = Code(g.CitizenshipCode);
        string country = g.ResidenceInItaly switch { true => Italy, false => Code(g.ResidenceCountryCode), _ => throw new Ross1000Problem("missing_residence") };
        var place = g.ResidenceInItaly == true ? Code(g.ResidenceComuneCode) : string.Empty;

        if (!g.DateOfBirth.HasValue || g.Gender is not (Gender.Male or Gender.Female)) throw new Ross1000Problem("invalid_guest");
        return new XElement("arrivo", new XElement("idswh", id), new XElement("tipoalloggiato", type),
            head == null ? null : new XElement("idcapo", head), new XElement("sesso", g.Gender == Gender.Male ? "M" : "F"),
            new XElement("cittadinanza", citizenship), new XElement("statoresidenza", country), new XElement("luogoresidenza", place),
            new XElement("datanascita", DateCode(DateOnly.FromDateTime(g.DateOfBirth.Value))),
            new XElement("tipoturismo", g.TourismType switch { "business" => "Congressuale/Affari", "other" => "Altro motivo", _ => "Non specificato" }),
            new XElement("mezzotrasporto", g.TravelWay switch
            {
                "car" => "Auto",
                "train" => "Treno",
                "plane" => "Aereo",
                "bus" => "Pullman",
                "ship" => "Barca/Nave/Traghetto",
                "other" => "Altro mezzo",
                _ => "Non specificato"
            }));
    }

    private static string Code(string? value) => value is { Length: 9 } && value.All(char.IsAsciiDigit)
        ? value : throw new Ross1000Problem("invalid_place_code");
}
