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
    /// <summary>Longest <see cref="RegimeCode"/>.</summary>
    public const int RegimeCodeMaxLength = 12;

    /// <summary>Longest <see cref="Channel"/>.</summary>
    public const int ChannelMaxLength = 20;

    /// <summary>Longest <see cref="DeclaredByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Name of the unique index of the rows without a <see cref="RegimeCode"/> (the notification to the police of Ticino): a 23505 on it means the day is declared already.</summary>
    public const string DefaultRegimeIndexName = "UX_SwissNotificationDeclarations_Property_Day";

    /// <summary>Name of the unique index of the rows with a <see cref="RegimeCode"/>.</summary>
    public const string NamedRegimeIndexName = "UX_SwissNotificationDeclarations_Property_Day_Regime";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2); never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property the day belongs to. Foreign key, cascade.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The arrival day the declaration closes, a calendar day in Switzerland (Europe/Zurich).</summary>
    public DateOnly ArrivalDay { get; set; }

    /// <summary>The person who declared it (<c>User.Id</c>, the Auth0 subject). Not a foreign key: the declaration outlives the account.</summary>
    [Required, MaxLength(UserIdMaxLength)]
    public string DeclaredByUserId { get; set; } = string.Empty;

    /// <summary>When it was declared (UTC).</summary>
    public DateTime DeclaredAt { get; set; } = DateTime.UtcNow;

    /// <summary>How the notification was made (<see cref="SwissNotificationChannels"/>); <c>portal</c> for Ticino.</summary>
    [Required, MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = SwissNotificationChannels.Portal;

    /// <summary>
    /// The receipt of the portal that goes with the declaration: a <c>Police</c> receipt of the same property whose report date is the arrival day. <c>null</c> when the host declares
    /// without one (the receipt is optional, decision D10) and after the retention removed it. Foreign key, SET NULL.
    /// </summary>
    public Guid? ReceiptId { get; set; }

    /// <summary>
    /// The regime the declaration is about: <c>null</c> for the notification to the police of canton Ticino (E30), the code of the regime for the notification of another canton (E31). At
    /// most <see cref="RegimeCodeMaxLength"/> characters.
    /// </summary>
    [MaxLength(RegimeCodeMaxLength)]
    public string? RegimeCode { get; set; }
}

/// <summary>The values of <see cref="SwissNotificationDeclaration.Channel"/>: how the host made the notification. Text, so a regime of another canton adds its own value with no migration.</summary>
public static class SwissNotificationChannels
{
    /// <summary>The host uploaded the file, or typed the guests, on the cantonal police portal: the only way of Ticino (notifiche.ti.ch), which has no API.</summary>
    public const string Portal = "portal";
}
