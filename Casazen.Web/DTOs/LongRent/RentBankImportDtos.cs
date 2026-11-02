using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs.LongRent;

/// <summary>
/// The statement of the bank, sent as a multipart form (<c>POST api/long-rent/rents/bank-import/preview</c>): the <c>file</c> and, when the
/// importer did not recognize the columns of a table, the <c>mapping</c> the landlord chose, as JSON
/// (<c>{"headerRow":1,"date":0,"description":3,"amount":5}</c>, counted from 0 over the rows that are not empty).
/// </summary>
public class BankStatementUpload
{
    public IFormFile? File { get; set; }

    public string? Mapping { get; set; }
}

/// <summary>
/// The same statement again, with the choices of the landlord (<c>POST api/long-rent/rents/bank-import/confirm</c>): <c>selections</c> is a
/// JSON array of <c>{"fingerprint":"…","installmentId":"…"}</c>, at most 500. The server reads amount, date and payer from the file itself.
/// </summary>
public sealed class BankStatementConfirmUpload : BankStatementUpload
{
    public string? Selections { get; set; }
}

/// <summary>The columns the landlord chose, before they are checked against the file (every number is optional here, so a missing one is an error and not a 0).</summary>
public sealed class BankColumnMappingBody
{
    public int? HeaderRow { get; set; }

    public int? Date { get; set; }

    public int? Description { get; set; }

    public int? ValueDate { get; set; }

    public int? Amount { get; set; }

    public int? Credit { get; set; }

    public int? Debit { get; set; }

    public int? Counterparty { get; set; }

    public int? Iban { get; set; }
}

/// <summary>One choice of the landlord, before it is checked.</summary>
public sealed class BankSelectionBody
{
    public string? Fingerprint { get; set; }

    public Guid? InstallmentId { get; set; }
}
