using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// How the package of the accountant is sent (<c>GET</c> and <c>PUT api/accountant-package/settings</c>, E15/F01/US02/T01, NF-101): the address of the accountant, whether the package is sent every month, the day of
/// the month (Europe/Rome), the folders and what the last send ended in. The address is personal data of a third party: it is read only by a caller of the whole org and never logged. <c>email</c> is <c>null</c> when there
/// is none; <c>contents</c> are keys (<c>guestDocuments</c>, <c>ownerInvoices</c>, <c>statements</c>, <c>agencySummary</c>, <c>vat</c>, <c>withholding</c>, <c>touristTax</c>, <c>collections</c>), never texts to show.
/// </summary>
public sealed class AccountantPackageSettingsDto
{
    public bool Enabled { get; init; }

    public string? Email { get; init; }

    /// <summary>The day of the month the send is due, 1 to 28 (Europe/Rome); from that day the previous month goes at the first run, until it is sent.</summary>
    public int SendDay { get; init; }

    public IReadOnlyList<string> Contents { get; init; } = [];

    /// <summary>The last attempt; <c>null</c> before the first.</summary>
    public AccountantPackageLastSendDto? LastSend { get; init; }

    /// <summary>When a person saved the settings last (UTC); <c>null</c> for an organization that saved none.</summary>
    public DateTime? UpdatedAt { get; init; }

    public static AccountantPackageSettingsDto From(AccountantPackageSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new AccountantPackageSettingsDto
        {
            Enabled = view.Enabled,
            Email = view.Email,
            SendDay = view.SendDay,
            Contents = AccountantPackageFolderRules.KeysOf(view.Contents),
            LastSend = view.LastSend is { } last ? AccountantPackageLastSendDto.From(last) : null,
            UpdatedAt = view.UpdatedAt,
        };
    }
}

/// <summary>What the last attempt to send the package ended in: the month, when, <c>sent</c> or <c>failed</c> and, for a failure, the stable code of why (never a text, an address or a name).</summary>
public sealed class AccountantPackageLastSendDto
{
    /// <summary>The month the package was of (<c>yyyy-MM</c>).</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary>When the attempt ended (UTC).</summary>
    public DateTime At { get; init; }

    /// <summary><c>sent</c>: the e-mail with the link is in the queue of the e-mails (the queue delivers it and retries); <c>failed</c>: nothing was sent and the next run tries again.</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary><c>null</c> for a send; for a failure <c>package_too_large</c>, <c>package_failed</c>, <c>storage_failed</c>, <c>link_not_configured</c> or <c>email_not_queued</c>.</summary>
    public string? Error { get; init; }

    public static AccountantPackageLastSendDto From(AccountantPackageLastSend last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return new AccountantPackageLastSendDto { Period = last.Period, At = last.At, Result = AgencySummaryDto.Key(last.Result), Error = last.Error };
    }
}

/// <summary>
/// <c>POST api/accountant-package/send</c> (E15/F01/US02/T02): the package of <see cref="Period"/> (the previous month) is made and kept, and the e-mail with its link is in the queue of the e-mails, never that it was
/// delivered (the queue delivers it and retries). <see cref="LinkExpiresAt"/> is the last instant (UTC) the link in it opens; no address and no link are in the answer.
/// </summary>
public sealed record AccountantPackageEmailQueuedDto(string Period, bool Queued, DateTime LinkExpiresAt)
{
    public static AccountantPackageEmailQueuedDto From(AccountantPackageSendNowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AccountantPackageEmailQueuedDto(result.Period, Queued: true, result.LinkExpiresAt);
    }
}

/// <summary>
/// The body of <c>PUT api/accountant-package/settings</c>: all the settings, as a whole. A field that is missing is a refusal (422 <c>accountant_package_settings_invalid</c>), never a value (a send that turns off, a
/// day 0); an empty <c>contents</c> never means every folder (422 <c>accountant_package_contents_invalid</c>). <c>email</c> empty or <c>null</c> clears the address, and the monthly send cannot stay on without one.
/// </summary>
public sealed record SaveAccountantPackageSettingsRequest
{
    public bool? Enabled { get; init; }

    public string? Email { get; init; }

    public int? SendDay { get; init; }

    public List<string>? Contents { get; init; }
}
