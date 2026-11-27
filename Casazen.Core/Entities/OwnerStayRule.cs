using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The rules the agency gives the owner stays of a property (E07/F07/US01/T01, AC-NF111-2): the nights an owner may take in a calendar year, the notice
/// and the window of a request, the shortest and longest stay, the dates no owner stay may take, whether a person of the agency approves each request,
/// and the period in which owner stays are allowed. One row for the property (<see cref="PropertyOwnerId"/> <c>null</c>: every owner of it) and at most
/// one for each of its owners, which replaces the property row for that owner. A property with no row: every request waits for the agency, with no
/// other limit than the free dates.
/// </summary>
[Table("OwnerStayRules")]
public class OwnerStayRule : ITenantOwned
{
    /// <summary>The longest <see cref="UpdatedByUserId"/>: the column length of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the property. Server-set.</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The owner the row is for (<c>PropertyOwner.Id</c>); <c>null</c> for the rule of the property, which every owner without a row of its own follows.</summary>
    public Guid? PropertyOwnerId { get; set; }

    /// <summary>The nights an owner may hold in one calendar year (requested and approved stays, Europe/Rome dates); <c>null</c> for no limit.</summary>
    public int? YearlyNightAllowance { get; set; }

    /// <summary>The fewest days between today and the arrival of a request (the notice); 0 for none.</summary>
    public int MinAdvanceDays { get; set; }

    /// <summary>The most days between today and the arrival of a request (the window); <c>null</c> for no limit.</summary>
    public int? MaxAdvanceDays { get; set; }

    /// <summary>The shortest stay, in nights (at least 1).</summary>
    public int MinNights { get; set; } = 1;

    /// <summary>The longest stay, in nights; <c>null</c> for no limit other than the longest block of the calendar.</summary>
    public int? MaxNights { get; set; }

    /// <summary>A person of the agency approves every request. When false, a request that obeys the rules is approved by CasaZen in the background.</summary>
    public bool ApprovalRequired { get; set; } = true;

    /// <summary>The first night owner stays are allowed; <c>null</c> for no start.</summary>
    public DateOnly? ActiveFrom { get; set; }

    /// <summary>The last night owner stays are allowed; <c>null</c> for no end.</summary>
    public DateOnly? ActiveUntil { get; set; }

    /// <summary>
    /// The dates no owner stay may take (jsonb): a list of <c>{ "from": "yyyy-MM-dd", "to": "yyyy-MM-dd" }</c>, both nights included. Written and read only
    /// through <c>OwnerStayRules</c>.
    /// </summary>
    public string BlackoutsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Who saved the rule last (<c>User.Id</c>).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
