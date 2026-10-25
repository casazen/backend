using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>What the lease says, to be compared with the receipt of its registration: a lease that is still a draft has nothing to compare.</summary>
internal static class ReceiptFacts
{
    public static IReadOnlyList<string> WarningsOf(RegistrationReceiptReading receipt, LeaseContract lease)
    {
        if (lease.Status == LeaseStatus.Draft)
            return [];

        var facts = new ReceiptLeaseFacts(
            lease.MonthlyRent,
            DateOnly.FromDateTime(lease.StartDate),
            DateOnly.FromDateTime(lease.EndDate),
            lease.StipulaDate is { } stipula ? DateOnly.FromDateTime(stipula) : null,
            lease.ContractType,
            lease.TaxRegime,
            lease.Property?.City);
        return RegistrationReceiptCheck.Compare(receipt, facts);
    }
}

/// <summary>
/// <see cref="ILeaseDocumentReadingService"/>: reads the receipt of the registration from the private bucket, in process, with no OCR, and
/// compares it with the lease. The document is looked up with its lease in the query and must belong to the organization of that lease;
/// anything else is the same <c>null</c>, and nothing is read for it.
/// </summary>
public sealed class LeaseDocumentReadingService(AppDbContext db, IFileStorage storage, IContractTextReader reader) : ILeaseDocumentReadingService
{
    public async Task<LeaseDocumentReadingDto?> ReadAsync(Guid leaseId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await db.LeaseDocuments.AsNoTracking()
            .Include(d => d.LeaseContract).ThenInclude(l => l.Property)
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.LeaseContractId == leaseId && d.Kind == LeaseDocumentKind.Registration, cancellationToken);
        if (document is null || document.OrgId != document.LeaseContract.OrgId)
            return null;

        await using var stream = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, cancellationToken);
        if (stream is null)
            return null;

        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken);

        ContractText? text;
        try
        {
            text = await reader.ReadDigitalAsync(content.ToArray(), cancellationToken);
        }
        catch (InvalidDataException)
        {
            text = null;
        }

        if (text is null)
            return new LeaseDocumentReadingDto(document.Id, nameof(LeaseDocumentKind.Registration), LeaseDocumentReadingStates.Unreadable, null);

        RegistrationReceiptReading receipt;
        try
        {
            receipt = RegistrationReceiptReader.Read(text.Text);
        }
        catch (RegexMatchTimeoutException)
        {
            return new LeaseDocumentReadingDto(document.Id, nameof(LeaseDocumentKind.Registration), LeaseDocumentReadingStates.Unreadable, null);
        }

        if (!receipt.IsReceipt)
            return new LeaseDocumentReadingDto(document.Id, nameof(LeaseDocumentKind.Registration), LeaseDocumentReadingStates.NotAReceipt, null);

        return new LeaseDocumentReadingDto(
            document.Id,
            nameof(LeaseDocumentKind.Registration),
            LeaseDocumentReadingStates.Read,
            ToDto(receipt, ReceiptFacts.WarningsOf(receipt, document.LeaseContract)));
    }

    private static RegistrationReceiptDto ToDto(RegistrationReceiptReading receipt, IReadOnlyList<string> warnings) => new(
        receipt.Outcome.ToString(),
        receipt.Code,
        receipt.Number,
        receipt.Series,
        Day(receipt.RegistrationDate),
        receipt.Office,
        receipt.AnnualRent,
        Day(receipt.Start),
        Day(receipt.End),
        Day(receipt.StipulationDate),
        receipt.Typology,
        receipt.CedolareSecca,
        receipt.Comune,
        receipt.Province,
        warnings);

    private static string? Day(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
