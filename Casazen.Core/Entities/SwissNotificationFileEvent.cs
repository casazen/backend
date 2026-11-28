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
    public const int FormatMaxLength = 16;

    public const int Sha256Length = 64;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public DateOnly ArrivalDay { get; set; }

    [Required, MaxLength(FormatMaxLength)]
    public string Format { get; set; } = string.Empty;

    public int RowCount { get; set; }

    [Required, MaxLength(Sha256Length)]
    public string Sha256 { get; set; } = string.Empty;

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class SwissNotificationFileFormats
{
    public const string Csv = "csv";

    public const string Xml = "xml";

    public const string Register = "register";

    public const string GroupSheet = "groupSheet";

    public static IReadOnlyList<string> All { get; } = [Csv, Xml, Register, GroupSheet];

    public static IReadOnlyList<string> PoliceFile { get; } = [Csv, Xml];
}
