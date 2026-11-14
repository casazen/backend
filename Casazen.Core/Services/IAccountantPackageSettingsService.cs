using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// How the package of the accountant is sent to the accountant (E15/F01/US02/T01, NF-101, decision of 2026-11-14): the address, the monthly send, its day and its folders, and what the last send
/// ended in. One set of settings for each organization; the accountant only receives. The wizard «Fatturazione» reads and saves them (E15/F02/US02/T01).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The service never checks roles.</b> The web layer decides who reads (<c>payment.read</c>) and who saves (<c>property.write</c> and a scope that reaches the whole org); the service works in the
/// org it is given and names it in every query.</item>
/// <item><b>Validated here, never half saved.</b> A refused change saves nothing (422, with a code of <see cref="AccountantPackageErrors"/>): the address is a plain address or none, the send cannot be on without an address,
/// the day is 1 to 28, the folders are a non-empty list of known keys (an empty list never means «everything»).</item>
/// <item><b>The address is personal data of a third party.</b> Encrypted at rest, never written to a log and never put in an error.</item>
/// </list>
/// </remarks>
public interface IAccountantPackageSettingsService
{
    /// <summary>The settings of <paramref name="orgId"/>, or the defaults of an organization that has saved none: no address, the send off, the day <see cref="Entities.AccountantPackageSettings.DefaultSendDay"/>, every folder.</summary>
    Task<AccountantPackageSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the settings of <paramref name="orgId"/> as a whole (the row is created on the first save) and answers them as they are. <paramref name="userId"/> is recorded as who saved them.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>accountant_package_settings_invalid</c> (a field is missing), <c>accountant_package_recipient_invalid</c> (not a plain address), <c>accountant_package_recipient_missing</c> (the send on with no address),
    /// <c>accountant_package_send_day_invalid</c> (not 1 to 28), <c>accountant_package_contents_invalid</c> (an empty list, an unknown or a repeated folder).
    /// </exception>
    Task<AccountantPackageSettingsView> SaveAsync(
        Guid orgId,
        AccountantPackageSettingsChange change,
        string? userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The settings as a <c>PUT</c> carries them, all of them: a field that is missing is a refusal, never what an omitted field turns into (a send that is off, a day 0).
/// </summary>
/// <param name="Enabled">True to send the package every month. Required.</param>
/// <param name="Email">The address of the accountant; empty or <c>null</c> clears it. A plain address (no name, no spaces).</param>
/// <param name="SendDay">The day of the month the send is due, 1 to 28 (Europe/Rome). Required.</param>
/// <param name="Contents">The keys of the folders to send (<see cref="AccountantPackageFolderRules.KeyOf"/>). Required and never empty.</param>
public sealed record AccountantPackageSettingsChange(bool? Enabled, string? Email, int? SendDay, IReadOnlyList<string>? Contents);

/// <summary>What the last attempt to send the package ended in.</summary>
/// <param name="Period">The month the package was of (<c>yyyy-MM</c>).</param>
/// <param name="At">When the attempt ended (UTC).</param>
/// <param name="Result">Sent or failed.</param>
/// <param name="Error">The stable code of why it failed; <c>null</c> for a send.</param>
public sealed record AccountantPackageLastSend(string Period, DateTime At, AccountantPackageSendResult Result, string? Error);

/// <summary>The settings of an organization.</summary>
/// <param name="Enabled">True when the package is sent every month.</param>
/// <param name="Email">The address of the accountant; <c>null</c> when there is none.</param>
/// <param name="SendDay">The day of the month the send is due (Europe/Rome).</param>
/// <param name="Contents">The folders of the package that is sent.</param>
/// <param name="LastSend">The last attempt; <c>null</c> before the first.</param>
/// <param name="UpdatedAt">When a person saved them last (UTC); <c>null</c> for the defaults of an organization that saved none.</param>
public sealed record AccountantPackageSettingsView(
    bool Enabled,
    string? Email,
    int SendDay,
    AccountantPackageFolders Contents,
    AccountantPackageLastSend? LastSend,
    DateTime? UpdatedAt);

/// <summary>
/// The folders of the package as the settings name them (E15/F01/US02/T01): the one place that says which bit is which folder and which key of the API stands for it, so the settings, the builder
/// and the screen never drift.
/// </summary>
public static class AccountantPackageFolderRules
{
    /// <summary>The folders one by one, in the order of the package (the order of <c>indice.csv</c>).</summary>
    public static IReadOnlyList<AccountantPackageFolders> Singles { get; } =
    [
        AccountantPackageFolders.GuestDocuments,
        AccountantPackageFolders.OwnerInvoices,
        AccountantPackageFolders.Statements,
        AccountantPackageFolders.AgencySummary,
        AccountantPackageFolders.Vat,
        AccountantPackageFolders.Withholding,
        AccountantPackageFolders.TouristTax,
        AccountantPackageFolders.Collections,
    ];

    /// <summary>The key of a folder in the API (camel case): <c>guestDocuments</c>, <c>ownerInvoices</c>, <c>statements</c>, <c>agencySummary</c>, <c>vat</c>, <c>withholding</c>, <c>touristTax</c>, <c>collections</c>.</summary>
    public static string KeyOf(AccountantPackageFolders folder) => folder switch
    {
        AccountantPackageFolders.GuestDocuments => "guestDocuments",
        AccountantPackageFolders.OwnerInvoices => "ownerInvoices",
        AccountantPackageFolders.Statements => "statements",
        AccountantPackageFolders.AgencySummary => "agencySummary",
        AccountantPackageFolders.Vat => "vat",
        AccountantPackageFolders.Withholding => "withholding",
        AccountantPackageFolders.TouristTax => "touristTax",
        AccountantPackageFolders.Collections => "collections",
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Not a single folder of the package."),
    };

    /// <summary>The name of the folder in the ZIP (<see cref="AccountantPackageStructure"/>).</summary>
    public static string FolderOf(AccountantPackageFolders folder) => folder switch
    {
        AccountantPackageFolders.GuestDocuments => AccountantPackageStructure.GuestDocumentsFolder,
        AccountantPackageFolders.OwnerInvoices => AccountantPackageStructure.OwnerInvoicesFolder,
        AccountantPackageFolders.Statements => AccountantPackageStructure.StatementsFolder,
        AccountantPackageFolders.AgencySummary => AccountantPackageStructure.AgencySummaryFolder,
        AccountantPackageFolders.Vat => AccountantPackageStructure.VatFolder,
        AccountantPackageFolders.Withholding => AccountantPackageStructure.WithholdingFolder,
        AccountantPackageFolders.TouristTax => AccountantPackageStructure.TouristTaxFolder,
        AccountantPackageFolders.Collections => AccountantPackageStructure.CollectionsFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Not a single folder of the package."),
    };

    /// <summary>True for a folder that holds the owner accounting (the invoices to the owners, the statements, the withholding).</summary>
    public static bool IsOwnerAccounting(AccountantPackageFolders folder) =>
        folder is AccountantPackageFolders.OwnerInvoices or AccountantPackageFolders.Statements or AccountantPackageFolders.Withholding;

    /// <summary>The keys of the folders of <paramref name="set"/>, in the order of the package.</summary>
    public static IReadOnlyList<string> KeysOf(AccountantPackageFolders set) =>
        [.. Singles.Where(folder => (set & folder) == folder).Select(KeyOf)];

    /// <summary>
    /// The set the keys of a request name: exactly the keys of <see cref="KeyOf"/>. <c>false</c> (and <see cref="AccountantPackageFolders.None"/>) for a list that is missing or empty (an empty list never means
    /// «every folder»), for a key that is not one of them and for a key that is repeated.
    /// </summary>
    public static bool TryFromKeys(IReadOnlyCollection<string>? keys, out AccountantPackageFolders set)
    {
        set = AccountantPackageFolders.None;
        if (keys is null || keys.Count == 0)
            return false;

        var found = AccountantPackageFolders.None;
        foreach (var key in keys)
        {
            var folder = Singles.FirstOrDefault(single => string.Equals(KeyOf(single), key, StringComparison.Ordinal));
            if (folder == AccountantPackageFolders.None || (found & folder) == folder)
                return false;

            found |= folder;
        }

        set = found;
        return true;
    }
}
