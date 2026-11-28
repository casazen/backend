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

    public int SendDay { get; init; }

    public IReadOnlyList<string> Contents { get; init; } = [];

    public AccountantPackageLastSendDto? LastSend { get; init; }

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

public sealed class AccountantPackageLastSendDto
{
    public string Period { get; init; } = string.Empty;

    public DateTime At { get; init; }

    /// <summary><c>sent</c>: the e-mail with the link is in the queue of the e-mails (the queue delivers it and retries); <c>failed</c>: nothing was sent and the next run tries again.</summary>
    public string Result { get; init; } = string.Empty;

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

public sealed record SaveAccountantPackageSettingsRequest
{
    public bool? Enabled { get; init; }

    public string? Email { get; init; }

    public int? SendDay { get; init; }

    public List<string>? Contents { get; init; }
}
