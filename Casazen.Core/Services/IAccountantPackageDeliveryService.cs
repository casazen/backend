namespace Casazen.Core.Services;

/// <summary>
/// The package of the accountant sent to the accountant every month (E15/F01/US02/T02, NF-101, D11, decision of 2026-11-14): the package of the previous month is made, kept in the private storage
/// and announced to the accountant by an e-mail with a signed link that opens for <see cref="AccountantPackageLinkRules.Lifetime"/>. The e-mail queue has no attachments, so the e-mail carries the link, never the
/// package. The accountant only receives; nothing it does comes back.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>A month is sent once.</b> The settings of the org say which month the accountant already has (<c>LastSendPeriod</c> with the result <c>Sent</c>). The monthly send checks it, builds the package, and
/// records the month in the same step that queues the e-mail, under the lock of the org: however many runs reach the month (the job, its retry, a «send a test» at the same moment) the accountant gets one e-mail
/// for it. A crash between the queue and the commit sends it again rather than losing it. A failed attempt is recorded with a stable code and never replaces a send that went out; the next run tries again.</item>
/// <item><b>Only the stored address.</b> The e-mail goes to the address saved in the settings of the org and to nobody else: never to an address that comes with the request, never to a member of the org.</item>
/// <item><b>The link is the whole proof.</b> HMAC-SHA256 with the secret of the signed links of the org, the org, the month and the expiry; whatever makes a link not open (wrong, changed, expired, made for another
/// org or month, a package that is gone, a secret that is not configured) is the same answer, <c>null</c>, which the controller turns into the same 404. Nothing of it is logged but the org and the month.</item>
/// <item><b>The package waits for its link and no longer.</b> It is kept in the private storage, under a key made of the org and the month, until the link has expired: the nightly run deletes it.</item>
/// <item><b>Nothing personal in a log.</b> The address of the accountant, the link and its signature are never written: the logs carry the org, the month, the codes and the counts.</item>
/// </list>
/// </remarks>
public interface IAccountantPackageDeliveryService
{
    Task<IReadOnlyList<Guid>> DueOrganizationsAsync(DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// The package of the month before <paramref name="today"/> for <paramref name="orgId"/>, with the folders its settings chose and the whole of the owner accounting when the organization is on the agency plan (it is
    /// the organization that sends it, not a person; an organization on another plan has none to send), sent to its accountant, once: <see cref="AccountantPackageMonthlyOutcome.AlreadySent"/> when the month is already sent. A failure (the package too large, the storage, the queue, the link secret) is
    /// recorded in the settings as <c>failed</c> with its code and answered as <see cref="AccountantPackageMonthlyOutcome.Failed"/>, never swallowed: the job fails after it has tried every org, so Hangfire runs it again. The package is made before the lock and the settings are read again under it: if their folders are no longer the ones it was made with,
    /// nothing is stored or queued (<see cref="AccountantPackageMonthlyOutcome.SettingsChanged"/>).
    /// </summary>
    Task<AccountantPackageMonthlyOutcome> SendMonthlyAsync(Guid orgId, DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Send a test» (<c>POST api/accountant-package/send</c>): the package of the previous month, with the folders of the settings, sent now to the saved address, whatever the day and whether the monthly send is on. It
    /// is the send of that month: the accountant is not sent it again by the monthly run. On an organization on the agency plan <paramref name="access"/> must allow the owner accounting, so the test is the package the monthly
    /// send makes and never a partial one (a person never shares what it may not read). A failure is not recorded in the settings; it is the answer.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>accountant_package_recipient_missing</c> (no address saved), <c>accountant_package_owner_accounting_required</c> (an agency's organization and an access without the owner accounting: nothing is made),
    /// <c>accountant_package_too_large</c> (the package of the month is over the caps of the archive) and <c>accountant_package_not_sent</c> (the package could not be stored or the e-mail not queued: nothing was sent).
    /// </exception>
    Task<AccountantPackageSendNowResult> SendNowAsync(Guid orgId, AccountantPackageAccess access, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredPackagesAsync(CancellationToken cancellationToken = default);

    Task<AccountantPackageFile?> OpenAsync(Guid orgId, string? period, string? expiresAt, string? signature, CancellationToken cancellationToken = default);
}

public enum AccountantPackageMonthlyOutcome
{
    Sent = 1,

    /// <summary>The month is already sent (by an earlier run, a retry or a test): nothing is made, stored or queued.</summary>
    AlreadySent = 2,

    NotDue = 3,

    NotConfigured = 4,

    Failed = 5,

    SettingsChanged = 6,
}

public sealed record AccountantPackageSendNowResult(string Period, DateTime LinkExpiresAt);

public sealed record AccountantPackageFile(Stream Content, string FileName);

public static class AccountantPackageSendErrors
{
    public const string PackageTooLarge = "package_too_large";

    public const string PackageFailed = "package_failed";

    public const string StorageFailed = "storage_failed";

    public const string LinkNotConfigured = "link_not_configured";

    public const string EmailNotConfigured = "email_not_configured";

    public const string EmailNotQueued = "email_not_queued";
}

public static class AccountantPackageLinkRules
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static readonly TimeSpan Retention = Lifetime;

    public const string SignaturePurpose = "accountant-package";
}
