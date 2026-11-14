using Casazen.Core.Authorization;

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
    /// <summary>
    /// The organizations whose package of the previous month is due on <paramref name="today"/> (Europe/Rome): the monthly send is on, an address is saved, the day of the send has come, and the month is not already
    /// sent. Empty while <c>Features:AccountantPackage</c> is off. Reads the settings of every org (the background run has no tenant); the send of each org then runs under its own tenant.
    /// </summary>
    Task<IReadOnlyList<Guid>> DueOrganizationsAsync(DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// The package of the month before <paramref name="today"/> for <paramref name="orgId"/>, with the folders its settings chose and the whole of the owner accounting when the organization is on the agency plan (it is
    /// the organization that sends it, not a person; an organization on another plan has none to send), sent to its accountant, once: <see cref="AccountantPackageMonthlyOutcome.AlreadySent"/> when the month is already sent. A failure (the package too large, the storage, the queue, the link secret) is
    /// recorded in the settings as <c>failed</c> with its code and answered as <see cref="AccountantPackageMonthlyOutcome.Failed"/>, never swallowed: the job fails after it has tried every org, so Hangfire runs it again.
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

    /// <summary>
    /// Deletes the packages whose link has expired (kept <see cref="AccountantPackageLinkRules.Retention"/>), and forgets them in the settings. Returns how many. An object that cannot be deleted stays and is tried
    /// again at the next run; it never stops the others.
    /// </summary>
    Task<int> DeleteExpiredPackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The package the link names: the stream of the ZIP and its file name. <c>null</c> for everything else, with no word of why (see the remarks); the caller disposes the stream. Reads no tenant data and writes nothing.
    /// </summary>
    Task<AccountantPackageFile?> OpenAsync(Guid orgId, string? period, string? expiresAt, string? signature, CancellationToken cancellationToken = default);
}

/// <summary>What the monthly send of an org did.</summary>
public enum AccountantPackageMonthlyOutcome
{
    /// <summary>The package is stored and the e-mail with its link is in the queue of the e-mails; the month is recorded as sent.</summary>
    Sent = 1,

    /// <summary>The month is already sent (by an earlier run, a retry or a test): nothing is made, stored or queued.</summary>
    AlreadySent = 2,

    /// <summary>The day of the send has not come yet in this month: nothing to do today.</summary>
    NotDue = 3,

    /// <summary>The org has no monthly send on, or no address saved, or the feature is off: nothing to do.</summary>
    NotConfigured = 4,

    /// <summary>Nothing was sent: the failure is recorded in the settings with its code and the next run tries again.</summary>
    Failed = 5,
}

/// <summary>What <see cref="IAccountantPackageDeliveryService.SendNowAsync"/> sent: the month of the package, and the last instant (UTC) its link opens. No address and no link are in it.</summary>
public sealed record AccountantPackageSendNowResult(string Period, DateTime LinkExpiresAt);

/// <summary>The package a link opens: the stream of the ZIP (the caller disposes it) and the name of the file to save.</summary>
public sealed record AccountantPackageFile(Stream Content, string FileName);

/// <summary>
/// The stable codes of why a send failed, as the settings keep them (<c>lastSend.error</c>): never a message, an address or a name. A failed send is tried again at the next run.
/// </summary>
public static class AccountantPackageSendErrors
{
    /// <summary>The package of the month is over the caps of the archive (<c>DocumentArchive:MaxExportFiles</c>, <c>MaxExportBytes</c>): raise them, or send fewer folders.</summary>
    public const string PackageTooLarge = "package_too_large";

    /// <summary>The package could not be made (an error of the services it reads).</summary>
    public const string PackageFailed = "package_failed";

    /// <summary>The package could not be put in the private storage, or the package of the month before, which it replaces, could not be deleted.</summary>
    public const string StorageFailed = "storage_failed";

    /// <summary>The secret of the signed links (<c>SIGNED_LINK_SECRET</c>) or the public https address of this API (<c>App:ApiBaseUrl</c>) is not configured: no link can be made.</summary>
    public const string LinkNotConfigured = "link_not_configured";

    /// <summary>The e-mail provider is not configured in this environment.</summary>
    public const string EmailNotConfigured = "email_not_configured";

    /// <summary>The queue of the e-mails refused the message.</summary>
    public const string EmailNotQueued = "email_not_queued";
}

/// <summary>The link of the package sent to the accountant and the time its package is kept (E15/F01/US02/T02).</summary>
public static class AccountantPackageLinkRules
{
    /// <summary>How long a link opens: 30 days from the moment the e-mail is queued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>How long a package is kept in the private storage: as long as its link opens, then the nightly run deletes it.</summary>
    public static readonly TimeSpan Retention = Lifetime;

    /// <summary>The prefix of what the signature of a link covers: a signature made for another kind of link (the unsubscribe link, the confirmation link) never opens a package, and the other way round.</summary>
    public const string SignaturePurpose = "accountant-package";
}
