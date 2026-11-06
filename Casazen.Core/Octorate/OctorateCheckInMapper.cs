using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Core.Octorate;

/// <summary>The tourist tax values of one guest for Octorate (NF-46, AC-NF46-5): sent only when CasaZen allows it.</summary>
/// <param name="Price">The tax of the guest (<c>cityTaxPrice</c>).</param>
/// <param name="Exemption">The Octorate reason of the host's exemption (<c>cityTaxExemption</c>), only when the reason has one.</param>
/// <param name="Exclude">True for an exempt guest or a stay the channel collected (<c>excludeCityTax</c>).</param>
public sealed record OctorateCityTaxValues(decimal Price, string? Exemption, bool Exclude);

/// <summary>
/// The check-in of a stay as Octorate receives it (E03/F01/US01/T03, NF-41, NF-46), pure: the draft rule of the sender, the mapping of a
/// registered guest, the hash of what is sent and the tourist tax values. No database, no call.
/// </summary>
public static class OctorateCheckInMapper
{
    /// <summary>The Alloggiati code of Italy in the "Stati" table.</summary>
    public const string ItalyStateCode = "100000100";

    /// <summary>The country code Octorate's guest example uses for Italy.</summary>
    public const string ItalyCountry = "IT";

    /// <summary>
    /// The kind of guest of the Alloggiati record as Octorate names it (<c>accommodatedType</c>). <c>SINGLE_GUEST</c> was read back on the
    /// sandbox (2026-11-04); the other four follow the same naming of the Alloggiati categories and are not proven yet: an unknown value is
    /// refused by Octorate, which the link records as <c>octorate_guest_rejected</c>, never as a success. Every <see cref="StayGuestType"/>
    /// has a value (a test fails for one without).
    /// </summary>
    public static IReadOnlyDictionary<StayGuestType, string> AccommodatedTypes { get; } = new Dictionary<StayGuestType, string>
    {
        [StayGuestType.SingleGuest] = "SINGLE_GUEST",
        [StayGuestType.HeadOfFamily] = "HEAD_OF_FAMILY",
        [StayGuestType.FamilyMember] = "FAMILY_MEMBER",
        [StayGuestType.HeadOfGroup] = "HEAD_OF_GROUP",
        [StayGuestType.GroupMember] = "GROUP_MEMBER",
    };

    /// <summary>
    /// The police code of a kind of document when the guest has no code of the official table (<see cref="StayGuest.DocumentTypeCode"/>): the
    /// Alloggiati codes of the identity card, the ordinary passport and the driving licence (the sandbox reads <c>GENERIC_IDENTITY</c> back as
    /// <c>IDENT</c>).
    /// </summary>
    public static IReadOnlyDictionary<GuestDocumentType, string> DocumentPoliceCodes { get; } = new Dictionary<GuestDocumentType, string>
    {
        [GuestDocumentType.IdentityCard] = "IDENT",
        [GuestDocumentType.Passport] = "PASOR",
        [GuestDocumentType.DriversLicense] = "PATEN",
    };

    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>false</c> ONLY when the channel manager is the sender of the property: Octorate is the sender and transmits the guests to the police
    /// (AC-NF41-1, AC-NF41-5). <c>true</c> for <see cref="ComplianceSenderType.CasaZen"/> and <see cref="ComplianceSenderType.Manual"/>
    /// (AC-NF41-2), a branch reached only while <c>Octorate:SendGuestsWhenSenderIsNotChannelManager</c> is on.
    /// </summary>
    public static bool DraftFor(ComplianceSenderType sender) => sender != ComplianceSenderType.ChannelManager;

    /// <summary>The <c>accommodatedType</c> of a kind of guest.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A kind with no Octorate value.</exception>
    public static string AccommodatedTypeOf(StayGuestType type) =>
        AccommodatedTypes.TryGetValue(type, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(type), type, "A kind of guest with no Octorate value.");

    /// <summary>
    /// The guest as Octorate receives it: names, sex, birth, citizenship and residence (Italy as <c>IT</c> with the comune by name, abroad the
    /// state as entered), the document only for a single guest or a head (the police code of its type), the dates of the stay, the draft rule of
    /// the sender and the tax values when they may travel. No image, no stream, no bytes: the upsert has no member for them.
    /// </summary>
    public static OctorateCheckInGuestUpsert Map(
        StayGuest guest,
        DateOnly checkIn,
        DateOnly checkOut,
        bool draft,
        string? octorateGuestId,
        OctorateCityTaxValues? tax)
    {
        ArgumentNullException.ThrowIfNull(guest);
        var withDocument = AlloggiatiRecordRules.RequiresDocument(guest.Type);
        return new OctorateCheckInGuestUpsert
        {
            Position = guest.Position,
            OctorateGuestId = octorateGuestId,
            Draft = draft,
            AccommodatedType = AccommodatedTypeOf(guest.Type),
            FamilyName = guest.LastName,
            GivenName = guest.FirstName,
            Sex = guest.Gender switch
            {
                Gender.Male => "MALE",
                Gender.Female => "FEMALE",
                _ => null,
            },
            BirthDate = guest.DateOfBirth is { } birth ? DateOnly.FromDateTime(birth) : null,
            BirthCountry = guest.BornInItaly switch
            {
                true => ItalyCountry,
                false => NullIfBlank(guest.BirthCountryName),
                _ => null,
            },
            BirthCity = guest.BornInItaly == true ? NullIfBlank(guest.BirthComuneName) : null,
            Citizenship = IsItaly(guest.CitizenshipCode, guest.CitizenshipName) ? ItalyCountry : NullIfBlank(guest.CitizenshipName),
            ResidenceCountry = guest.ResidenceInItaly switch
            {
                true => ItalyCountry,
                false => NullIfBlank(guest.ResidenceCountryName),
                _ => null,
            },
            ResidenceCity = guest.ResidenceInItaly == true ? NullIfBlank(guest.ResidenceComuneName) : null,
            DocumentType = withDocument ? DocumentTypeOf(guest) : null,
            DocumentCode = withDocument ? NullIfBlank(guest.DocumentNumber) : null,
            DocumentIssuePlace = withDocument ? NullIfBlank(guest.DocumentIssuePlaceName) : null,
            DocumentIssueDate = withDocument ? guest.DocumentIssuedOn : null,
            DocumentExpire = withDocument ? guest.DocumentExpiresOn : null,
            CheckIn = checkIn,
            CheckOut = checkOut,
            CityTaxPrice = tax?.Price,
            CityTaxExemption = tax?.Exemption,
            ExcludeCityTax = tax?.Exclude,
        };
    }

    /// <summary>
    /// The SHA-256 (hex) of the canonical payload of a guest, <c>draft</c> and the tax values included, the Octorate id and the position
    /// excluded (they are not content): a different hash is a change to send.
    /// </summary>
    public static string Hash(OctorateCheckInGuestUpsert upsert)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        var canonical = JsonSerializer.Serialize(upsert with { OctorateGuestId = null, Position = 0 }, HashJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// The tourist tax values of each position, only when they may travel (AC-NF46-5): the write is on (<c>Octorate:WriteCityTaxValues</c>), the
    /// stay tax is confirmed and not stale. The price is the guest's amount; the exemption is the Octorate code of the host's reason only when
    /// the reason has one (<paramref name="octorateCodes"/>: reason code to Octorate code); <c>excludeCityTax</c> for an exempt guest or a stay
    /// the channel collected. Otherwise no position has values, and nothing about the tax is sent.
    /// </summary>
    public static IReadOnlyDictionary<int, OctorateCityTaxValues> TaxValues(
        bool writeEnabled,
        StayTouristTax? record,
        bool stale,
        IReadOnlyDictionary<string, string?> octorateCodes)
    {
        ArgumentNullException.ThrowIfNull(octorateCodes);
        if (!writeEnabled || record is not { ConfirmedAt: not null } || stale)
            return new Dictionary<int, OctorateCityTaxValues>();

        var channelCollected = record.Collector == TouristTaxCollector.Channel;
        return record.Guests.ToDictionary(
            line => line.Position,
            line => new OctorateCityTaxValues(
                line.Amount,
                line.ExemptionKind == TouristTaxExemptionKind.Host
                && line.ExemptionCode is { } code
                && octorateCodes.TryGetValue(code, out var octorate)
                && !string.IsNullOrWhiteSpace(octorate)
                    ? octorate
                    : null,
                line.ExemptionKind != TouristTaxExemptionKind.None || channelCollected));
    }

    /// <summary>The police code of the document: the code of the official table, or the one of its kind; null when neither is known.</summary>
    private static string? DocumentTypeOf(StayGuest guest) =>
        NullIfBlank(guest.DocumentTypeCode)
        ?? (guest.DocumentType is { } kind && DocumentPoliceCodes.TryGetValue(kind, out var code) ? code : null);

    private static bool IsItaly(string? code, string? name) =>
        string.Equals(code?.Trim(), ItalyStateCode, StringComparison.Ordinal)
        || AlloggiatiRecordRules.NormalizeDescription(name) == AlloggiatiRecordRules.ItalyNormalizedDescription;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
