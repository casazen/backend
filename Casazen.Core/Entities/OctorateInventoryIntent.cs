using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What a person declared about where the listings of a property are today (section 9.1, D6, E02/F01/US03): already on Octorate, already on
/// OTAs or in another channel manager, or never published. It is stored apart from the work that follows from it (the import batch), so a
/// restart, a second device or another service finds the same answer.
/// </summary>
/// <remarks>
/// <para><b>One current intent for a property.</b> The partial unique index <see cref="PropertyIndexName"/> holds one row for each
/// org and property (a row with a property); the rows with no property are the intents of a named group of the mixed portfolio that has no
/// property yet (a listing found on an OTA that CasaZen has not got), held once for each org and group by
/// <see cref="OrgLevelGroupIndexName"/>.</para>
/// <para><b>Never inferred.</b> <see cref="OctorateInventoryScenario.NewInventory"/> exists only because a person chose it: a missing
/// row, a missing mapping or missing data never stand for it, and the creation of a listing on Octorate is refused for a property without
/// that row (<c>IInventoryScenarioGuard</c>). An import never turns an existing scenario into a new one.</para>
/// <para>No personal data and no secret: ids, a group name the person chose, and the Auth0 subject of who declared it.</para>
/// </remarks>
[Table("OctorateInventoryIntents")]
public class OctorateInventoryIntent : ITenantOwned
{
    /// <summary>Name of the partial unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>) of the rows with a property.</summary>
    public const string PropertyIndexName = "UIX_OctorateInventoryIntents_OrgId_PropertyId";

    /// <summary>Name of the partial unique index on (<see cref="OrgId"/>, <see cref="WizardBatch"/>) of the rows with no property.</summary>
    public const string OrgLevelGroupIndexName = "UIX_OctorateInventoryIntents_OrgId_Group_OrgLevel";

    /// <summary>Longest <see cref="WizardBatch"/>.</summary>
    public const int WizardBatchMaxLength = 100;

    /// <summary>Longest <see cref="ActorUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The CasaZen property the intent is about (FK, CASCADE); <c>null</c> for the intent of a group that has no property yet.</summary>
    public Guid? PropertyId { get; set; }

    public OctorateInventoryScenario Scenario { get; set; }

    /// <summary>
    /// The named group of the wizard (a mixed portfolio) the assignment belongs to; <c>null</c> for an assignment that belongs to no group.
    /// An intent with no property always has one.
    /// </summary>
    [MaxLength(WizardBatchMaxLength)]
    public string? WizardBatch { get; set; }

    /// <summary>Who declared it: the Auth0 subject.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it was declared or changed last (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
