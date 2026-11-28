using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The declaration «I uploaded the file» of an arrival day of a property (E30/F02/US02/T01, NF-128, decision D10, casazen/backend#979): a person of the org says that the guests of
/// that day were notified to the police, with or without the receipt of the portal. A declared day is closed like a day with a receipt: the alerts and the cockpit read it from
/// <c>ITicinoNotificationService.ListPendingDaysAsync</c>, the one place that says it, and the file of that day is generated again only on an explicit confirmation (the portal
/// may create duplicates).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The key.</b> One declaration for each (<see cref="PropertyId"/>, <see cref="ArrivalDay"/>, <see cref="RegimeCode"/>). A <c>null</c> code is the notification to the police of
/// canton Ticino (the first regime); the notification of another canton (E31) takes its own code and needs no new table and no migration. PostgreSQL takes two NULLs as different
/// values, so one index would let a second row without a code in: the key is two partial unique indexes, one for the rows without a code and one for the rows with it (the pattern of
/// <see cref="MarketplaceActivation"/>).</item>
/// <item><b>No guest data.</b> The row names the property, the day, the person who declared it (<see cref="DeclaredByUserId"/>, the Auth0 subject, never a name: the name is read from the
/// user when the day is shown), the instant, the channel and, optionally, the receipt.</item>
/// <item><b>Undo</b> deletes the row, for the same org and with the same permission as declaring. The bell and the log say who did what; the files that were generated are in
/// <see cref="SwissNotificationFileEvent"/>.</item>
/// <item><b>The receipt</b> (<see cref="ReceiptId"/>, optional) is a <c>Police</c> receipt of that property and day (<see cref="SwissComplianceReceipt"/>). A receipt removed at the end of its
/// retention (NF-128) leaves the declaration without it (SET NULL) and never removes the declaration.</item>
/// <item><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the org of the property, never one the client names; the rows go with their property (cascade).</item>
/// </list>
/// </remarks>
[Table("SwissNotificationDeclarations")]
public class SwissNotificationDeclaration : ITenantOwned
{
    public const int RegimeCodeMaxLength = 12;

    public const int ChannelMaxLength = 20;

    public const int UserIdMaxLength = 255;

    public const string DefaultRegimeIndexName = "UX_SwissNotificationDeclarations_Property_Day";

    public const string NamedRegimeIndexName = "UX_SwissNotificationDeclarations_Property_Day_Regime";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public DateOnly ArrivalDay { get; set; }

    [Required, MaxLength(UserIdMaxLength)]
    public string DeclaredByUserId { get; set; } = string.Empty;

    public DateTime DeclaredAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = SwissNotificationChannels.Portal;

    public Guid? ReceiptId { get; set; }

    [MaxLength(RegimeCodeMaxLength)]
    public string? RegimeCode { get; set; }
}

public static class SwissNotificationChannels
{
    public const string Portal = "portal";

    public const string Assisted = "assisted";
}
