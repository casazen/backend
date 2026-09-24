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
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Booking"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The booker (<see cref="Booking.Guest"/>) when this row was created from the booker's record: the first row of the
    /// booking. Null for the other guests. The booker's contact data and GDPR consent stay on <see cref="Guest"/>.
    /// </summary>
    public Guid? GuestId { get; set; }
    public virtual Guest? Guest { get; set; }

    /// <summary>Order of the row in the communication, from 0.</summary>
    public int Position { get; set; }

    /// <summary>Kind of guest ("tipo alloggiato"). Stored as an integer of CasaZen, never the Alloggiati table code.</summary>
    public StayGuestType Type { get; set; } = StayGuestType.SingleGuest;

    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Only <see cref="Entities.Gender.Male"/> or <see cref="Entities.Gender.Female"/> are valid for Alloggiati Web.</summary>
    public Gender? Gender { get; set; }

    /// <summary>Date of birth (date only, UTC midnight).</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>True when born in Italy (comune and province are then required), false when born abroad; null when unknown.</summary>
    public bool? BornInItaly { get; set; }

    /// <summary>Code of the comune of birth in the Alloggiati "Comuni" table; Italy only.</summary>
    [MaxLength(9)]
    public string? BirthComuneCode { get; set; }

    /// <summary>Comune of birth as entered (Italy). Rows migrated from the single-guest model keep the old free-text place here.</summary>
    [MaxLength(100)]
    public string BirthComuneName { get; set; } = string.Empty;

    /// <summary>Province of birth: two-letter car plate code (Roma = RM); Italy only.</summary>
    [MaxLength(2)]
    public string? BirthProvince { get; set; }

    /// <summary>Code of the state of birth in the Alloggiati "Stati" table (born abroad).</summary>
    [MaxLength(9)]
    public string? BirthCountryCode { get; set; }

    /// <summary>State of birth as entered (born abroad).</summary>
    [MaxLength(100)]
    public string BirthCountryName { get; set; } = string.Empty;

    /// <summary>Code of the citizenship in the Alloggiati "Stati" table.</summary>
    [MaxLength(9)]
    public string? CitizenshipCode { get; set; }

    /// <summary>Citizenship as entered.</summary>
    [MaxLength(100)]
    public string CitizenshipName { get; set; } = string.Empty;

    /// <summary>Kind of document chosen when no official document table is available. <see cref="GuestDocumentType.Other"/> is not accepted.</summary>
    public GuestDocumentType? DocumentType { get; set; }

    /// <summary>Code of the document type in the Alloggiati "Documenti" table.</summary>
    [MaxLength(5)]
    public string? DocumentTypeCode { get; set; }

    /// <summary>Document number; only for a single guest or a head of family or group (the record allows 20 characters).</summary>
    [MaxLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>Code of the place of issue: a comune code (issued in Italy) or a state code (issued abroad).</summary>
    [MaxLength(9)]
    public string? DocumentIssuePlaceCode { get; set; }

    /// <summary>Place of issue of the document as entered (comune or state).</summary>
    [MaxLength(100)]
    public string DocumentIssuePlaceName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The row the booking's guest stands for when no guest has been registered yet: the booker, as single guest or, when
    /// more guests are declared, as head of family. Not saved: its <see cref="Id"/> is empty. The migration
    /// <c>AddStayGuests</c> applied the same mapping to the bookings that existed before it.
    /// </summary>
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
    /// <summary>Ospite singolo: document required.</summary>
    SingleGuest = 1,

    /// <summary>Capo famiglia: document required, followed by the family members.</summary>
    HeadOfFamily = 2,

    /// <summary>Capo gruppo: document required, followed by the group members.</summary>
    HeadOfGroup = 3,

    /// <summary>Familiare: no document, follows the head of family.</summary>
    FamilyMember = 4,

    /// <summary>Membro gruppo: no document, follows the head of group.</summary>
    GroupMember = 5,
}
