using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// How the package of the accountant is sent to the accountant (E15/F01/US02/T01, NF-101, decision of 2026-11-14): the address, the monthly send, its day and its folders, and what the last send
/// ended in. One set of settings for each organization; the accountant only receives. The wizard «Fatturazione» reads and saves them (E15/F02/US02/T01).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The service never checks roles.</b> The web layer decides who reads (<c>payment.read</c>) and who saves (<c>property.write</c> and a scope that reaches the whole org); the service works in the
/// org it is given, names it in every query, and is told by the web layer whether the caller holds <c>agency.statement.view</c> (<see cref="AccountantPackageSettingsChange.OwnerAccounting"/>): on an organization
/// on the agency plan it refuses the folders that carry the owner accounting to a caller that does not, because the package it sends every month holds all of it.</item>
/// <item><b>Validated here, never half saved.</b> A refused change saves nothing (422, with a code of <see cref="AccountantPackageErrors"/>): the address is a plain address or none, the send cannot be on without an address,
/// the day is 1 to 28, the folders are a non-empty list of known keys (an empty list never means «everything»).</item>
/// <item><b>The address is personal data of a third party.</b> Encrypted at rest, never written to a log and never put in an error.</item>
/// </list>
/// </remarks>
public interface IAccountantPackageSettingsService
{
    Task<AccountantPackageSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

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
/// <param name="OwnerAccounting">The caller holds <c>agency.statement.view</c> (decided by the web layer, as for the package). <c>false</c> unless said: without it a folder that carries the owner accounting is refused to the caller of an agency's organization.</param>
public sealed record AccountantPackageSettingsChange(bool? Enabled, string? Email, int? SendDay, IReadOnlyList<string>? Contents, bool OwnerAccounting = false);

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

public static class AccountantPackageFolderRules
{
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

    public static bool IsOwnerAccounting(AccountantPackageFolders folder) =>
        folder is AccountantPackageFolders.OwnerInvoices or AccountantPackageFolders.Statements or AccountantPackageFolders.Withholding;

    /// <summary>
    /// The folders that can carry the owner accounting in the package an agency sends: the three that are made of it and the two that hold a part of it (the owner parts of the agency summary, the portal commissions in the VAT).
    /// On an organization on the agency plan the monthly package holds all of it, so choosing one of them is sharing the owner accounting with a third party: it needs <c>agency.statement.view</c>.
    /// </summary>
    public const AccountantPackageFolders OwnerAccountingCarriers = AccountantPackageFolders.OwnerInvoices | AccountantPackageFolders.Statements | AccountantPackageFolders.Withholding | AccountantPackageFolders.AgencySummary | AccountantPackageFolders.Vat;

    public static bool PlanHoldsOwnerAccounting(PlanTier tier, bool agencyTierDowngraded) => tier == PlanTier.Agency && !agencyTierDowngraded;

    public static IReadOnlyList<string> KeysOf(AccountantPackageFolders set) =>
        [.. Singles.Where(folder => (set & folder) == folder).Select(KeyOf)];

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
