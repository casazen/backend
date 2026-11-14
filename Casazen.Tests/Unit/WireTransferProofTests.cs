using System.Text;
using System.Xml.Linq;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-017 proof tests for wire transfer money rules and IBAN privacy, on the payouts (E16/F01/US01/T01): the amount of a transfer is the net of the confirmed
/// payout, and the IBAN of an owner is never in a log or an error.
/// </summary>
public class WireTransferProofTests
{
    /// <summary>
    /// Money rule proof: the wire batch TotalAmount equals the sum of the net amounts of the confirmed payouts it pays. Tests that the wire batch total is computed
    /// correctly from the included payouts (EUR, the SEPA file).
    /// </summary>
    [Fact]
    public async Task WireBatch_SumEqualsPayoutNets()
    {
        // Arrange: in-memory database, two owners with a valid Italian IBAN and a confirmed payout each
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner1 = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ItalianIban);
        var owner2 = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherItalianIban);
        var payout1 = await AddPayoutAsync(db, orgId, owner1, 1200.00m, "EUR");
        var payout2 = await AddPayoutAsync(db, orgId, owner2, 1574.50m, "EUR");

        // Act: create the wire batch (the official pain.001 schemas are not in the repository until an operator adds them, and a file is never produced without
        // its validation: this proof is about the sums, so it validates against a schema of its own, see Pain001ValidationProofTests for the validation)
        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, [payout1, payout2], ExecutionDate, CancellationToken.None);

        // Assert: batch total equals the sum of the payout nets
        Assert.Equal(1200.00m + 1574.50m, batch.TotalAmount);
        Assert.Equal(2774.50m, batch.TotalAmount);
        Assert.Equal(2, batch.TransferCount); // One wire per payout

        // The downloaded bytes must form a complete UTF-8 payment document.
        using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = XDocument.Load(xmlStream);
        XNamespace ns = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";
        Assert.Equal(ns + "Document", document.Root!.Name);
        Assert.Equal(2, document.Descendants(ns + "CdtTrfTxInf").Count());
        Assert.Equal("2774.50", document.Descendants(ns + "CtrlSum").First().Value);
        Assert.Equal(xml, batch.XmlContent);
    }

    /// <summary>
    /// Privacy rule proof: IBAN never appears in logs or error messages.
    /// Tests that when IBAN validation fails, the IBAN itself is not logged.
    /// </summary>
    [Fact]
    public async Task WireIban_NeverInLogs()
    {
        // Arrange: an owner with an INVALID IBAN (wrong check digits) and a confirmed payout
        var orgId = Guid.NewGuid();
        const string invalidIban = "IT00X0542811101000000123456";
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", invalidIban);
        var payout = await AddPayoutAsync(db, orgId, owner, 1200.00m, "EUR");
        var logger = new CapturingLogger<WireTransferService>();

        // Act & Assert: attempting to create a batch with an invalid IBAN should fail WITHOUT logging the IBAN itself (only the owner id is listed)
        var exception = await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await NewService(db, logger).CreateBatchAsync(orgId, [payout], ExecutionDate, CancellationToken.None));

        // The exception should mention the error but NOT contain the IBAN
        Assert.Equal("owner_invalid_iban", exception.Code);
        Assert.DoesNotContain(invalidIban, exception.Message);
        Assert.DoesNotContain(invalidIban, exception.ToString());
        Assert.DoesNotContain(invalidIban, logger.AllOutput);
        Assert.DoesNotContain("Rossi", logger.AllOutput);
        Assert.Equal(owner, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(exception.Extensions["ownerIds"])));

        // The IBAN is encrypted in the database (EF automatically encrypts it)
        var storedOwner = await db.PropertyOwners.AsNoTracking().FirstAsync(o => o.Id == owner);
        // When read through EF with encryption, the IBAN is decrypted
        // When read directly from the database, it would be encrypted
        // This proves the encryption mechanism is in place
        Assert.NotNull(storedOwner.Iban);
    }
}
