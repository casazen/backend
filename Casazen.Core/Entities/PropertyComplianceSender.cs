using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Who sends the police (Alloggiati Web) and ISTAT communications of a property (ADR-029, NF-42, CHK-03): one row per property
/// (unique index <see cref="PropertyIndexName"/>), written only by <c>IPropertySenderStore.SetAsync</c>. A property without a row has
/// the default of PO-02 (the channel manager while its listing is confirmed on a connected Octorate account, manual otherwise),
/// computed when it is read and never stored.
/// </summary>
/// <remarks>
/// <para><b>Never two senders</b> (ADR-029-R5, CHK-05): ISTAT follows Alloggiati. The database refuses a row whose two senders
/// differ (check constraint <see cref="IstatFollowsAlloggiatiConstraintName"/>), so two automatic senders can never be stored for one
/// property.</para>
/// <para>No personal data: who changed it last is the opaque id of the account (the Auth0 subject), not a foreign key.</para>
/// </remarks>
[Table("PropertyComplianceSenders")]
public class PropertyComplianceSender : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="PropertyId"/>: a concurrent first choice of the sender loses on it.</summary>
    public const string PropertyIndexName = "UIX_PropertyComplianceSenders_PropertyId";

    /// <summary>Name of the check constraint that keeps <see cref="IstatSender"/> equal to <see cref="AlloggiatiSender"/>.</summary>
    public const string IstatFollowsAlloggiatiConstraintName = "CK_PropertyComplianceSenders_IstatFollowsAlloggiati";

    /// <summary>Longest <see cref="LastChangedByUserId"/>.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE: the choice goes with the property).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Who sends the Alloggiati Web communications of the property.</summary>
    public ComplianceSenderType AlloggiatiSender { get; set; }

    /// <summary>Who sends the ISTAT communications: always the same as <see cref="AlloggiatiSender"/> (ADR-029-R5).</summary>
    public ComplianceSenderType IstatSender { get; set; }

    /// <summary>When the sender was last chosen (UTC).</summary>
    public DateTime LastChangedAt { get; set; }

    /// <summary>Who chose it last: the Auth0 subject of the account, not a foreign key.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string LastChangedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
