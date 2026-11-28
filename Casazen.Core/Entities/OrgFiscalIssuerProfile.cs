using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Entities;

/// <summary>
/// An issuer of the fiscal documents and its data (FIS-02, NF-82, NF-112, NF-113, NF-116, E11/F01/US01/T01, E17/F01/US01/T01): CasaZen is always the one who
/// issues (decision of 2026-11-14), and the profile says in whose name and with which data, in Italy or in Switzerland: the regime, the legal name and the
/// structured address, the tax ids, the bank accounts, the VAT treatments, the series, the automatic issue and the reminders of the wizard.
/// <list type="bullet">
/// <item><b>The profile of the organization</b> (<see cref="PropertyOwnerId"/> <c>null</c>): one for each organization (<see cref="OrgIndexName"/>), the issuer
/// of everything the agency issues in its own name.</item>
/// <item><b>The profile of an owner who issues</b> (<see cref="PropertyOwnerId"/> set, E17/F01/US01/T01, D12): one for each owner (<see cref="OwnerIndexName"/>).
/// In Italy the owner of the property is the issuer of the document of the stay (a receipt of a private owner, the FatturaPA of a company with the agency as
/// third-party issuer); CasaZen issues it on this profile, with the owner's own series (sezionale) and numbering.</item>
/// </list>
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One source of truth for the Italian fiscal identity of the organization.</b> The Italian VAT number and the fiscal code of the organization stay on
/// <see cref="Org"/> (<c>PartitaIvaNumber</c>, <c>FiscalCode</c>): its profile never copies them and leaves <see cref="FiscalCode"/> and <see cref="VatNumber"/>
/// empty. An owner is no organization: its profile keeps its own. A Swiss issuer needs neither; its UID is <see cref="SwissVatNumber"/>.</item>
/// <item><b>Partly filled while the wizard is open.</b> Every text is optional and <see cref="DocumentRegime"/> is empty until the issuer says it; what is
/// still missing to issue is computed (<c>FiscalDocumentRules.MissingIssuerFields</c>) and <see cref="ConfirmedAt"/> is set only when the wizard is
/// completed with nothing missing.</item>
/// <item><b>Privacy.</b> The personal data of the issuer (an owner is often a private person) are encrypted at rest (<c>EncryptedColumns</c>): the legal name,
/// the fiscal code, the VAT number, the street, the house number, the postal code, the city and the PEC, and the bank accounts <see cref="Iban"/> and
/// <see cref="QrIban"/>. Those columns have no length; the lengths are checked on input (the constants below).</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org the profile is of (for an owner, the org of the owner), and the row is read
/// and written only there. The readers of the organization's own profile go through <see cref="OrgFiscalIssuerProfileQueries.OfOrganization"/>.</item>
/// </list>
/// </remarks>
[Table("OrgFiscalIssuerProfiles")]
public class OrgFiscalIssuerProfile : ITenantOwned
{
    public const string OrgIndexName = "UIX_OrgFiscalIssuerProfiles_OrgId";

    public const string OwnerIndexName = "UIX_OrgFiscalIssuerProfiles_PropertyOwnerId";

    public const int FiscalCodeMaxLength = 16;

    public const int VatNumberMaxLength = 28;

    public const int LegalNameMaxLength = 200;
    public const int AddressStreetMaxLength = 200;
    public const int AddressCivicMaxLength = 20;
    public const int PostalCodeMaxLength = 10;
    public const int CityMaxLength = 100;
    public const int ProvinceMaxLength = 2;
    public const int CountryCodeMaxLength = 2;

    public const int FatturaPaRegimeCodeMaxLength = 4;

    public const int SdiRecipientCodeMaxLength = 7;

    public const int PecMaxLength = 254;

    public const int SwissVatNumberMaxLength = 15;

    public const int IbanMaxLength = 34;

    public const int SeriesMaxLength = 10;

    public const int StampDutyReferenceMaxLength = 60;

    public const int UpdatedByUserIdMaxLength = 255;

    public const string DefaultCountryCode = "IT";

    public const string DefaultOwnerInvoiceSeries = "PM{year}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>
    /// The owner whose profile this is (E17/F01/US01/T01); <c>null</c> for the profile of the organization itself. Unique where set (one profile for each
    /// owner); the row goes with its owner (cascade), unless a document was issued on it (a document never loses its issuer: restrict).
    /// </summary>
    public Guid? PropertyOwnerId { get; set; }

    public DocumentIssuerMode IssuerMode { get; set; } = DocumentIssuerMode.CasaZen;

    public DocumentRegime? DocumentRegime { get; set; }

    public string? LegalName { get; set; }

    public string? FiscalCode { get; set; }

    /// <summary>
    /// The VAT number of an owner who issues (an Italian one is the 11 digits, without the <c>IT</c> prefix); <c>null</c> for the profile of the organization,
    /// whose number is on <see cref="Org"/>, and for a private owner. <b>Encrypted at rest.</b>
    /// </summary>
    public string? VatNumber { get; set; }

    public string? AddressStreet { get; set; }

    public string? AddressCivic { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    [MaxLength(ProvinceMaxLength)]
    public string? Province { get; set; }

    [Required, MaxLength(CountryCodeMaxLength)]
    public string CountryCode { get; set; } = DefaultCountryCode;

    /// <summary>
    /// The regime code of FatturaPA (<c>RF01</c>, <c>RF19</c>...). It comes from the tax rule of the regime and the accountant can override it here;
    /// <c>null</c> = the rule's. Italian issuers only.
    /// </summary>
    [MaxLength(FatturaPaRegimeCodeMaxLength)]
    public string? FatturaPaRegimeCode { get; set; }

    /// <summary>The code of the issuer's own SDI channel (7 letters or digits, upper case); <c>null</c> when it has none.</summary>
    [MaxLength(SdiRecipientCodeMaxLength)]
    public string? IssuerSdiRecipientCode { get; set; }

    public string? IssuerPec { get; set; }

    /// <summary>
    /// The issuer is registered for the VAT. It follows the regime (an Italian VAT number, a Swiss MWST registration): a regime that says otherwise is
    /// refused; without a regime it is what the issuer answered.
    /// </summary>
    public bool VatRegistered { get; set; }

    /// <summary>The Swiss VAT number of a registered Swiss issuer: the UID, stored as <c>CHE-123.456.789</c> (format and check digit verified).</summary>
    [MaxLength(SwissVatNumberMaxLength)]
    public string? SwissVatNumber { get; set; }

    public string? Iban { get; set; }

    public string? QrIban { get; set; }

    /// <summary>How the VAT of the stay is applied (⚖ I1): the rate of the rule, or the exemption of art. 10 (Italy).</summary>
    public StayVatTreatment StayVatTreatment { get; set; } = StayVatTreatment.Standard;

    public CleaningFeeTreatment CleaningFeeTreatment { get; set; } = CleaningFeeTreatment.FollowsStay;

    [MaxLength(SeriesMaxLength)]
    public string? GuestDocumentSeries { get; set; }

    [MaxLength(SeriesMaxLength)]
    public string? OwnerInvoiceSeries { get; set; }

    /// <summary>
    /// Who holds the accounts of the listings on the portals, for the whole organization (E14/F01/US01/T01, ⚖ I10): the owner by default (V1); a listing
    /// and portal can say otherwise (<see cref="PropertyChannelAccountHolder"/>). Read through <c>IPortalAccountHolderResolver</c>.
    /// </summary>
    public PortalAccountHolder PortalAccountHolder { get; set; } = PortalAccountHolder.Owner;

    /// <summary>
    /// The commissions of the portals whose account the agency holds (V2) are recharged to the owner on the owner invoice, at the VAT of a recharge
    /// (E14/F01/US01/T01, ⚖ I11: the scheme of a mandate without representation). On by default.
    /// </summary>
    public bool RechargePortalCommissions { get; set; } = true;

    public bool AutoIssueGuestDocuments { get; set; }

    public bool EmailGuestDocuments { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, as a JSON array of <see cref="BookingSource"/> names (jsonb, column
    /// <c>PlatformCollectedTouristTaxChannels</c>). <c>null</c> = nothing confirmed yet (no channel is listed; for Switzerland the default of the
    /// configuration applies); <c>[]</c> = the issuer confirmed that no portal collects it (E11/F01/US01/T04). Read through
    /// <see cref="PlatformCollectedTouristTaxChannels"/>, confirmed through <see cref="ConfirmPlatformCollectedTouristTaxChannels"/>.
    /// An Italian issuer that confirmed a portal for one comune (<see cref="PortalTouristTaxComuni"/>, FIX/BE-IT-TOURISTTAX-DEFAULTS) has the same column as an object,
    /// <c>{"channels":[...],"comuni":[{"channel":"Airbnb","comuneIstat":"015146"}]}</c> (no <c>channels</c> member while the list for every comune is not confirmed): no migration, and an
    /// issuer with no such confirmation keeps the plain array it always had, byte for byte.
    /// </summary>
    [Column("PlatformCollectedTouristTaxChannels", TypeName = "jsonb")]
    public string? PlatformCollectedTouristTaxChannelsJson { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, so the document of their bookings has no tourist tax line (empty for Italy; for
    /// Switzerland the default comes from the configuration of the tourist tax). Names that are no longer a <see cref="BookingSource"/> are skipped.
    /// Assigning an empty list leaves the choice as it is (never confirmed stays so): to say that none collects it, use
    /// <see cref="ConfirmPlatformCollectedTouristTaxChannels"/>.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<BookingSource> PlatformCollectedTouristTaxChannels
    {
        get => ChannelsOf(PlatformCollectedTouristTaxChannelsJson);
        set => PlatformCollectedTouristTaxChannelsJson = ComposePlatformCollected(value is { Count: > 0 } ? value : null, PortalTouristTaxComuni);
    }

    /// <summary>
    /// True once the issuer confirmed the channels whose portal collects the tourist tax, even to say that none does; false while nothing was ever
    /// confirmed (the step «Tassa di soggiorno» of the billing setup, E11/F01/US01/T04). A reader that falls back to the Swiss default of the configuration
    /// does it only while this is false: a confirmed empty list means «no portal collects it», never «use the default».
    /// </summary>
    [NotMapped]
    public bool PlatformCollectedTouristTaxChannelsConfirmed => ReadPlatformCollected(PlatformCollectedTouristTaxChannelsJson).ChannelsGiven;

    public void ConfirmPlatformCollectedTouristTaxChannels(IEnumerable<BookingSource> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        PlatformCollectedTouristTaxChannelsJson = ComposePlatformCollected(channels.Distinct().ToList(), PortalTouristTaxComuni);
    }

    /// <summary>
    /// The portals the issuer confirmed for one comune (the proposal «Airbnb incassa a Milano», FIX/BE-IT-TOURISTTAX-DEFAULTS): the channel and the ISTAT code of the comune. They count only for a
    /// property in that comune, never for the other comuni of the issuer, and are kept apart from <see cref="PlatformCollectedTouristTaxChannels"/> (the portals that collect it in every comune).
    /// </summary>
    [NotMapped]
    public IReadOnlyList<PortalComune> PortalTouristTaxComuni => PortalComuniOf(PlatformCollectedTouristTaxChannelsJson);

    public void ConfirmPortalTouristTaxComuni(IEnumerable<PortalComune> comuni)
    {
        ArgumentNullException.ThrowIfNull(comuni);
        var current = ReadPlatformCollected(PlatformCollectedTouristTaxChannelsJson);
        PlatformCollectedTouristTaxChannelsJson = ComposePlatformCollected(current.ChannelsGiven ? current.Channels : null, [.. comuni]);
    }

    /// <summary>
    /// The authorization number of the virtual stamp duty or the number of the physical stamp (⚖ I4, Italy): printed on the documents with a stamp duty;
    /// <c>null</c> = «imposta di bollo da assolvere».
    /// </summary>
    [MaxLength(StampDutyReferenceMaxLength)]
    public string? StampDutyReference { get; set; }

    /// <summary>
    /// The profile of a Swiss owner: the agency chose «emette il proprietario» for this owner (⚖ C1, direct representation, MWSTG art. 20), so the documents
    /// of the stays of the owner's Swiss properties are issued in the owner's name. Off by default: in Switzerland the agency issues (E11/E12). Not read in
    /// Italy, where the owner of the property always issues the document of the stay.
    /// </summary>
    public bool StayIssuedByOwner { get; set; }

    public bool ConservationAgreementConfirmed { get; set; }

    public bool VirtualStampConfirmed { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    [NotMapped]
    public bool IsOwnerProfile => PropertyOwnerId is not null;

    public static IReadOnlyList<BookingSource> ChannelsOf(string? json) => ReadPlatformCollected(json).Channels;

    public static IReadOnlyList<PortalComune> PortalComuniOf(string? json) => ReadPlatformCollected(json).Comuni;

    public static bool IsIstatCode(string? code) => code is { Length: 6 } && code.All(char.IsAsciiDigit);

    private sealed record PlatformCollectedSetting(bool ChannelsGiven, IReadOnlyList<BookingSource> Channels, IReadOnlyList<PortalComune> Comuni);

    private static PlatformCollectedSetting ReadPlatformCollected(string? json)
    {
        if (json is null)
            return new PlatformCollectedSetting(false, [], []);

        if (string.IsNullOrWhiteSpace(json))
            return new PlatformCollectedSetting(true, [], []);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return new PlatformCollectedSetting(true, ChannelNamesOf(root), []);

            if (root.ValueKind != JsonValueKind.Object)
                return new PlatformCollectedSetting(true, [], []);

            var given = root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array;
            var comuni = root.TryGetProperty("comuni", out var list) && list.ValueKind == JsonValueKind.Array ? PortalComuniIn(list) : [];
            return new PlatformCollectedSetting(given, given ? ChannelNamesOf(channels) : [], comuni);
        }
        catch (JsonException)
        {
            return new PlatformCollectedSetting(true, [], []);
        }
    }

    private static List<BookingSource> ChannelNamesOf(JsonElement array) =>
        array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => Enum.TryParse<BookingSource>(item.GetString(), ignoreCase: false, out var source) && Enum.IsDefined(source) ? source : (BookingSource?)null)
            .OfType<BookingSource>()
            .Distinct()
            .ToList();

    private static List<PortalComune> PortalComuniIn(JsonElement list)
    {
        var comuni = new List<PortalComune>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("comuneIstat", out var istat) || istat.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var code = istat.GetString();
            if (!Enum.TryParse<BookingSource>(channel.GetString(), ignoreCase: false, out var source) || !Enum.IsDefined(source) || !IsIstatCode(code))
                continue;

            var comune = new PortalComune(source, code!);
            if (!comuni.Contains(comune))
                comuni.Add(comune);
        }

        return comuni;
    }

    private static string? ComposePlatformCollected(IReadOnlyList<BookingSource>? channels, IReadOnlyList<PortalComune> comuni)
    {
        var names = channels?.Distinct().Order().Select(source => source.ToString()).ToList();
        var confirmed = comuni
            .Where(comune => IsIstatCode(comune.ComuneIstat))
            .Distinct()
            .OrderBy(comune => comune.Channel)
            .ThenBy(comune => comune.ComuneIstat, StringComparer.Ordinal)
            .Select(comune => new { channel = comune.Channel.ToString(), comuneIstat = comune.ComuneIstat })
            .ToList();
        if (confirmed.Count == 0)
            return names is null ? null : JsonSerializer.Serialize(names);

        return names is null
            ? JsonSerializer.Serialize(new { comuni = confirmed })
            : JsonSerializer.Serialize(new { channels = names, comuni = confirmed });
    }
}

/// <summary>
/// The one way to read the profile of an organization itself (E17/F01/US01/T01): since owners have profiles in the same table, a reader that looked for «the
/// profile of the org» by <c>OrgId</c> alone could take the profile of one of its owners.
/// </summary>
public static class OrgFiscalIssuerProfileQueries
{
    public static IQueryable<OrgFiscalIssuerProfile> OfOrganization(this IQueryable<OrgFiscalIssuerProfile> profiles, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Where(profile => profile.OrgId == orgId && profile.PropertyOwnerId == null);
    }

    public static IQueryable<OrgFiscalIssuerProfile> OfOrganizations(this IQueryable<OrgFiscalIssuerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Where(profile => profile.PropertyOwnerId == null);
    }
}
