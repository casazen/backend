namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The folders of the package of the accountant (E15/F01/US01/T01) the agency chooses to put in the one it sends every month (E15/F01/US02/T01, NF-101): one bit for each
/// folder of <c>AccountantPackageStructure</c>. The numbers are stored (<see cref="AccountantPackageSettings.Contents"/>): never renumber one, never reuse one.
/// <see cref="All"/> is what the package of the screens always holds.
/// </summary>
[Flags]
public enum AccountantPackageFolders
{
    None = 0,

    GuestDocuments = 1,

    /// <summary><c>fatture-proprietari/</c>: the invoices of the agency to the owners (owner accounting).</summary>
    OwnerInvoices = 2,

    Statements = 4,

    AgencySummary = 8,

    Vat = 16,

    Withholding = 32,

    TouristTax = 64,

    Collections = 128,

    All = GuestDocuments | OwnerInvoices | Statements | AgencySummary | Vat | Withholding | TouristTax | Collections,
}

public enum AccountantPackageSendResult
{
    /// <summary>The package is in the private storage and the e-mail with its link is in the queue of the e-mails (it says nothing about its delivery: the queue delivers it and retries).</summary>
    Sent = 1,

    Failed = 2,
}
