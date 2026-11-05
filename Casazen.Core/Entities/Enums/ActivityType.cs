namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What an item of the "To do" list is (ADR-026, NF-07). Stored by name in the string column
/// <see cref="InternalActivity.ActivityType"/>, next to the plain-text values of the staff work ("Cleaning", "Maintenance",
/// "CheckIn"), which are not members of this enum: a row of the staff work has no <see cref="InternalActivity.Action"/>, a
/// row of the To do always has one.
/// </summary>
/// <remarks>
/// The name is what the rows keep, so a member is never renamed; a later task appends the member it needs. The
/// numbers are never stored.
/// </remarks>
public enum ActivityType
{
    /// <summary>A stay whose check-in is still to be done.</summary>
    CheckInPending = 1,

    /// <summary>A stay whose check-out is still to be done.</summary>
    CheckOutPending = 2,

    /// <summary>The guests of a stay are still to be sent to Alloggiati Web within its deadline.</summary>
    AlloggiatiDeadline = 3,

    /// <summary>The ISTAT data of a period are still to be sent within their deadline.</summary>
    IstatDeadline = 4,

    /// <summary>A receipt (of a transmission) is still to be retrieved.</summary>
    ReceiptToRetrieve = 5,

    /// <summary>A message waits for the host's approval before it is sent.</summary>
    MessageToApprove = 6,

    /// <summary>A listing could not be published on a channel.</summary>
    PublicationError = 7,

    /// <summary>A fiscal document needs the host.</summary>
    FiscalDocument = 8,

    /// <summary>A wizard was left half way and can be resumed.</summary>
    WizardResume = 9,

    /// <summary>An integration failed and needs the host.</summary>
    IntegrationError = 10,

    /// <summary>The credentials of a service are about to expire or expired.</summary>
    CredentialsToRenew = 11,

    /// <summary>The connection to the channel manager was lost.</summary>
    ReconnectChannelManager = 12,
}
