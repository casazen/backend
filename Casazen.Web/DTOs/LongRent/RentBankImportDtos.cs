namespace Casazen.Web.DTOs.LongRent;

public class BankStatementUpload
{
    public IFormFile? File { get; set; }

    public string? Mapping { get; set; }
}

public sealed class BankStatementConfirmUpload : BankStatementUpload
{
    public string? Selections { get; set; }
}

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

public sealed class BankSelectionBody
{
    public string? Fingerprint { get; set; }

    public Guid? InstallmentId { get; set; }
}
