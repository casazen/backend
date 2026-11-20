using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The audit of a file CasaZen generated for the police notification (E30/F02/US02/T01, NF-128, casazen/backend#979): one row for every generation, whoever asked and whether or
/// not the host uploaded the file afterwards. It answers «was a file made for that day, how many times, when and with what content?» (the hash) without keeping the file: the files are
/// rebuilt on every request and never stored.
/// </summary>
/// <remarks>
/// <para><b>No personal data.</b> The row holds the property, the day, the format, the number of rows, the SHA-256 of the file and the person who asked (<see cref="CreatedByUserId"/>, the Auth0
/// subject): never a name, a document, an address, a booking code or a value of the file. Append only: a row is never edited.</para>
/// <para><b>Who writes.</b> The file of an arrival day (<see cref="SwissNotificationFileFormats.Csv"/>, <see cref="SwissNotificationFileFormats.Xml"/>) and the sheet of a group
/// (<see cref="SwissNotificationFileFormats.GroupSheet"/>, whose day is the arrival of its stay) are written by <c>ITicinoNotificationService</c>; the guest register of E31
/// (<see cref="SwissNotificationFileFormats.Register"/>) writes its own rows, with the first day of its period as <see cref="ArrivalDay"/>.</para>
/// <para><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the org of the property; the rows go with their property (cascade).</para>
/// </remarks>
[Table("SwissNotificationFileEvents")]
public class SwissNotificationFileEvent : ITenantOwned
{
    /// <summary>Longest <see cref="Format"/>.</summary>
    public const int FormatMaxLength = 16;

    /// <summary>Length of <see cref="Sha256"/>: 64 hexadecimal characters.</summary>
    public const int Sha256Length = 64;

    /// <summary>Longest <see cref="CreatedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2); never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property the file is about. Foreign key, cascade.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The arrival day the file is about, a calendar day in Switzerland (Europe/Zurich).</summary>
    public DateOnly ArrivalDay { get; set; }

    /// <summary>What was generated: a value of <see cref="SwissNotificationFileFormats"/>.</summary>
    [Required, MaxLength(FormatMaxLength)]
    public string Format { get; set; } = string.Empty;

    /// <summary>How many rows the file has (the notified persons of the day, the members of a group sheet, the lines of a register); never negative.</summary>
    public int RowCount { get; set; }

    /// <summary>SHA-256 of the bytes of the file, lowercase hexadecimal (64 characters).</summary>
    [Required, MaxLength(Sha256Length)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The person who generated it (<c>User.Id</c>, the Auth0 subject); <c>null</c> for a generation no person asked for. Not a foreign key.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    /// <summary>When it was generated (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The values of <see cref="SwissNotificationFileEvent.Format"/>, stored as text (the same words the API uses). The column is bounded by the check <c>CK_SwissNotificationFileEvents_Format</c>, so a
/// new format needs a migration that widens the check as well as a value here. The list is photographed by <c>TicinoDeclarationTests</c>: a value added here is a decision of its own.
/// </summary>
public static class SwissNotificationFileFormats
{
    /// <summary>The CSV of an arrival day for the portal (the default file of the Ticino police).</summary>
    public const string Csv = "csv";

    /// <summary>The XML of an arrival day for the portal (only when it fits the XSD).</summary>
    public const string Xml = "xml";

    /// <summary>The downloadable guest register of a period (E31/F01, every canton).</summary>
    public const string Register = "register";

    /// <summary>The sheet of a group of six or more persons of a Ticino stay (decision D11), kept five years by the host.</summary>
    public const string GroupSheet = "groupSheet";

    /// <summary>Every value, in the order of the brief.</summary>
    public static IReadOnlyList<string> All { get; } = [Csv, Xml, Register, GroupSheet];

    /// <summary>
    /// The formats that are «the file of the day» for the portal (<see cref="Csv"/> and <see cref="Xml"/>): the ones the day counts (<c>fileGeneratedCount</c>) and the ones a declared day
    /// asks to confirm again. A group sheet and a register are kept by the host, never uploaded as a notification.
    /// </summary>
    public static IReadOnlyList<string> PoliceFile { get; } = [Csv, Xml];
}
