namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The folders of the package of the accountant (E15/F01/US01/T01) the agency chooses to put in the one it sends every month (E15/F01/US02/T01, NF-101): one bit for each
/// folder of <c>AccountantPackageStructure</c>. The numbers are stored (<see cref="AccountantPackageSettings.Contents"/>): never renumber one, never reuse one.
/// <see cref="All"/> is what the package of the screens always holds.
/// </summary>
[Flags]
public enum AccountantPackageFolders
{
    /// <summary>Nothing: not a choice (the settings refuse it), the empty set of the helpers.</summary>
    None = 0,

    /// <summary><c>documenti-ospiti/</c>: the documents issued to the guests and their registers.</summary>
    GuestDocuments = 1,

    /// <summary><c>fatture-proprietari/</c>: the invoices of the agency to the owners (owner accounting).</summary>
    OwnerInvoices = 2,

    /// <summary><c>rendiconti/</c>: the published statements of the owners (owner accounting).</summary>
    Statements = 4,

    /// <summary><c>riepilogo-agenzia/</c>: the agency summary.</summary>
    AgencySummary = 8,

    /// <summary><c>iva/</c>: the VAT for each rate and issuer.</summary>
    Vat = 16,

    /// <summary><c>ritenute/</c>: the withholding, the F24 and the Certificazione Unica data (owner accounting).</summary>
    Withholding = 32,

    /// <summary><c>tassa-soggiorno/</c>: the tourist tax.</summary>
    TouristTax = 64,

    /// <summary><c>incassi/</c>: the collections, the balances to collect and the costs of the channels.</summary>
    Collections = 128,

    /// <summary>Every folder: the default of the settings and what the package of the screens holds.</summary>
    All = GuestDocuments | OwnerInvoices | Statements | AgencySummary | Vat | Withholding | TouristTax | Collections,
}

/// <summary>What the last send of the package to the accountant ended in (<see cref="AccountantPackageSettings.LastSendResult"/>). The numbers are stored: never renumber one.</summary>
public enum AccountantPackageSendResult
{
    /// <summary>The package is in the private storage and the e-mail with its link is in the queue of the e-mails (it says nothing about its delivery: the queue delivers it and retries).</summary>
    Sent = 1,

    /// <summary>Nothing was sent: the package could not be made or stored, or the e-mail could not be queued (<see cref="AccountantPackageSettings.LastSendError"/> says which). The next run tries again.</summary>
    Failed = 2,
}
