using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>PUT /api/properties/{propertyId}/questura-credentials</c> (CO-14): the three Alloggiati Web credentials,
/// all required every time (they replace the stored ones). Never returned, never logged. Validation messages are
/// SharedResources keys.
/// </summary>
public sealed class SetQuesturaCredentialsRequest
{
    [Required(ErrorMessage = "QuesturaCredentialsUsernameRequired")]
    [MaxLength(PropertyQuesturaCredentials.MaxUsernameLength, ErrorMessage = "QuesturaCredentialsUsernameTooLong")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "QuesturaCredentialsPasswordRequired")]
    [MaxLength(PropertyQuesturaCredentials.MaxPasswordLength, ErrorMessage = "QuesturaCredentialsPasswordTooLong")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "QuesturaCredentialsWsKeyRequired")]
    [MaxLength(PropertyQuesturaCredentials.MaxWsKeyLength, ErrorMessage = "QuesturaCredentialsWsKeyTooLong")]
    public string WsKey { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int? ApartmentId { get; set; }

    public QuesturaCredentialsInput ToInput() => new(Username, Password, WsKey, ApartmentId);

    public override string ToString() => nameof(SetQuesturaCredentialsRequest) + " { *** }";
}

/// <summary>
/// Answer of the Questura credentials endpoints: only whether they are configured and since when, never a value
/// (write-only credentials, CO-14).
/// </summary>
public sealed class QuesturaCredentialsStatusDto
{
    public bool Configured { get; init; }

    public DateTime? ConfiguredAt { get; init; }

    public static QuesturaCredentialsStatusDto From(QuesturaCredentialsStatus status) => new()
    {
        Configured = status.Configured,
        ConfiguredAt = status.ConfiguredAt,
    };
}
