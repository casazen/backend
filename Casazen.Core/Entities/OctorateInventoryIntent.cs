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
    public const string PropertyIndexName = "UIX_OctorateInventoryIntents_OrgId_PropertyId";

    public const string OrgLevelGroupIndexName = "UIX_OctorateInventoryIntents_OrgId_Group_OrgLevel";

    public const int WizardBatchMaxLength = 100;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public OctorateInventoryScenario Scenario { get; set; }

    [MaxLength(WizardBatchMaxLength)]
    public string? WizardBatch { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
