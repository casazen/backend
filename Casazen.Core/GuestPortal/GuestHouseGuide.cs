using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Casazen.Core.GuestPortal;

public sealed record GuestHouseGuide
{
    [MaxLength(2000)] public string? Arrival { get; init; }
    [MaxLength(2000)] public string? House { get; init; }
    [MaxLength(2000)] public string? Departure { get; init; }
    [MaxLength(2000)] public string? Contacts { get; init; }
    [MaxLength(2000)] public string? LocalTips { get; init; }
    [MaxLength(2000)] public string? Access { get; init; }
    [MaxLength(1000)] public string? Wifi { get; init; }

    [MaxLength(1000)] public string? CheckInHours { get; init; }
    [MaxLength(1000)] public string? CheckOutHours { get; init; }
    [MaxLength(1000)] public string? Parking { get; init; }
    public GuestHouseGuideAutoReplies? AutoReplies { get; init; }

    public static string? RulesVersion(string? text) => string.IsNullOrWhiteSpace(text)
        ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static GuestHouseGuide Read(string? json) =>
        json is null ? new() : JsonSerializer.Deserialize<GuestHouseGuide>(json) ?? new();

    public string Write() => JsonSerializer.Serialize(this);

    public GuestHouseGuide ForVisitor(bool canSeeReserved) => this with
    {
        Wifi = canSeeReserved ? Wifi : null,
        Access = canSeeReserved ? Access : null,
        AutoReplies = null,
    };
}

public sealed record GuestHouseGuideAutoReplies(bool Enabled, bool CheckIn, bool CheckOut, bool Parking, bool Wifi);
