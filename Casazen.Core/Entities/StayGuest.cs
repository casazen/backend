using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One person staying in a booking, i.e. one line ("schedina") of the Alloggiati Web communication (art. 109 TULPS,
/// D.M. 16/09/2021, Allegato tecnico): every guest is registered, minors included (CO-12, A5-02). The rows of a booking
/// are ordered by <see cref="Position"/>: a head of family or group comes before its members.
/// </summary>
/// <remarks>
/// Places, citizenship and document type are stored as the label the guest entered plus, when known, the code of the
/// official Alloggiati table (<see cref="AlloggiatiCodeEntry"/>). A missing code does not block data entry: it only
/// blocks the export of the record ("codice da completare"). Codes are never derived from free text by CasaZen except by
/// a unique match against an imported official table.
/// </remarks>
[Table("StayGuests")]
[Index(nameof(BookingId), nameof(Position), IsUnique = true)]
public class StayGuest : ITenantOwned
{
    public const int ResidenceStreetMaxLength = 200;

    public const int ResidencePostalCodeMaxLength = 20;

    public const int ResidenceCityNameMaxLength = 100;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public Guid OrgId { get; set; }

    /// <summary>
    /// The booker (<see cref="Booking.Guest"/>) when this row was created from the booker's record: the first row of the
    /// booking. Null for the other guests. The booker's contact data and GDPR consent stay on <see cref="Guest"/>.
    /// </summary>
    public Guid? GuestId { get; set; }
    public virtual Guest? Guest { get; set; }

    public int Position { get; set; }

    /// <summary>Kind of guest ("tipo alloggiato"). Stored as an integer of CasaZen, never the Alloggiati table code.</summary>
    public StayGuestType Type { get; set; } = StayGuestType.SingleGuest;

    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Only <see cref="Entities.Gender.Male"/> or <see cref="Entities.Gender.Female"/> are valid for Alloggiati Web.</summary>
    public Gender? Gender { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public bool? BornInItaly { get; set; }

    /// <summary>Code of the comune of birth in the Alloggiati "Comuni" table; Italy only.</summary>
    [MaxLength(9)]
    public string? BirthComuneCode { get; set; }

    [MaxLength(100)]
    public string BirthComuneName { get; set; } = string.Empty;

    [MaxLength(2)]
    public string? BirthProvince { get; set; }

    /// <summary>Code of the state of birth in the Alloggiati "Stati" table (born abroad).</summary>
    [MaxLength(9)]
    public string? BirthCountryCode { get; set; }

    [MaxLength(100)]
    public string BirthCountryName { get; set; } = string.Empty;

    [MaxLength(9)]
    public string? CitizenshipCode { get; set; }

    [MaxLength(100)]
    public string CitizenshipName { get; set; } = string.Empty;

    public GuestDocumentType? DocumentType { get; set; }

    [MaxLength(5)]
    public string? DocumentTypeCode { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    [MaxLength(9)]
    public string? DocumentIssuePlaceCode { get; set; }

    public string DocumentIssuePlaceName { get; set; } = string.Empty;

    public DateOnly? DocumentIssuedOn { get; set; }

    public DateOnly? DocumentExpiresOn { get; set; }

    /// <summary>
    /// True when the guest lives in Italy (residence comune and province), false when abroad (residence state); null when not entered.
    /// Octorate accepts a complete check-in guest only with the residence as a place name it recognizes (NF-41). Same shape rules as the
    /// birth place; optional at entry.
    /// </summary>
    public bool? ResidenceInItaly { get; set; }

    /// <summary>Code of the comune of residence in the Alloggiati "Comuni" table; Italy only.</summary>
    public string? ResidenceComuneCode { get; set; }

    public string? ResidenceComuneName { get; set; }

    public string? ResidenceProvince { get; set; }

    /// <summary>Code of the state of residence in the Alloggiati "Stati" table (living abroad).</summary>
    public string? ResidenceCountryCode { get; set; }

    public string? ResidenceCountryName { get; set; }

    /// <summary>
    /// Street and house number of the guest's home («Indirizzo» of the Ticino police file, NF-132, casazen/backend#979); null when not entered.
    /// Optional at entry, asked where the property is in canton Ticino. Encrypted at rest with the document (<c>EncryptedColumns</c>): the
    /// column is <c>text</c> and the <see cref="ResidenceStreetMaxLength"/> characters are checked on input.
    /// </summary>
    public string? ResidenceStreet { get; set; }

    public string? ResidencePostalCode { get; set; }

    public string? ResidenceCityName { get; set; }

    public string? TourismType { get; set; }

    public string? TravelWay { get; set; }

    public string? EducationLevel { get; set; }

    /// <summary>
    /// The tourist tax exemption the host chose for the guest (a non-automatic <see cref="TouristTaxExemptionReason.Code"/>, NF-46); null
    /// when none. The host's decision only: a save of the guest portal never changes it. Encrypted at rest with the document (CO-14,
    /// <c>EncryptedColumns</c>): the reason of an exemption can reveal a health condition, so the column is <c>text</c> and the length
    /// is checked at entry.
    /// </summary>
    public string? TaxExemptionCode { get; set; }

    public StayGuestDataSource DataSource { get; set; } = StayGuestDataSource.NotRecorded;

    /// <summary>User id (Auth0 <c>sub</c>) of the host who entered the row; null for the guest portal. Never returned to clients.</summary>
    [MaxLength(200)]
    public string? EnteredByUserId { get; set; }

    /// <summary>
    /// When the personal data of the row were erased (CO-15): anonymization or erasure of the booker, or
    /// <c>Gdpr:Retention:AlloggiatiData</c> after the check-out. Kind and position stay, so the stay keeps its shape.
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static StayGuest FromBooker(Booking booking, Guest guest) => new()
    {
        Id = Guid.Empty,
        BookingId = booking.Id,
        OrgId = booking.OrgId,
        GuestId = guest.Id,
        Position = 0,
        Type = booking.NumberOfGuests > 1 ? StayGuestType.HeadOfFamily : StayGuestType.SingleGuest,
        FirstName = guest.FirstName,
        LastName = guest.LastName,
        Gender = guest.Gender is Entities.Gender.Male or Entities.Gender.Female ? guest.Gender : null,
        DateOfBirth = guest.DateOfBirth,
        BornInItaly = null,
        BirthComuneName = guest.PlaceOfBirth,
        CitizenshipName = guest.Nationality,
        DocumentType = guest.DocumentType is GuestDocumentType.Other ? null : guest.DocumentType,
        DocumentNumber = guest.DocumentNumber,
        DocumentIssuePlaceName = guest.DocumentIssuingCountry,
        DataSource = StayGuestDataSource.NotRecorded,
        CreatedAt = booking.CreatedAt,
        UpdatedAt = booking.UpdatedAt,
    };
}

/// <summary>
/// Kind of guest ("tipo alloggiato"): the five official categories of Alloggiati Web
/// (<c>.claude/context/regulations/alloggiati.md</c>, "Tipi alloggiato", verified U). The integer values are CasaZen's
/// own and must never be renumbered; the official numeric codes are NOT verified and come only from the imported
/// "Tipi alloggiato" table.
/// </summary>
public enum StayGuestType
{
    SingleGuest = 1,

    HeadOfFamily = 2,

    HeadOfGroup = 3,

    FamilyMember = 4,

    GroupMember = 5,
}

public enum StayGuestDataSource
{
    NotRecorded = 0,

    GuestPortal = 1,

    Host = 2,
}
