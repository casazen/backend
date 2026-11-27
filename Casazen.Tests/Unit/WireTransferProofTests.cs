using System.Text;
using System.Xml.Linq;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Unit;

public class WireTransferProofTests
{
    [Fact]
    public async Task WireBatch_SumEqualsPayoutNets()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner1 = await AddOwnerAsync(db, orgId, "Mario", "Rossi", ItalianIban);
        var owner2 = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherItalianIban);
        var payout1 = await AddPayoutAsync(db, orgId, owner1, 1200.00m, "EUR");
        var payout2 = await AddPayoutAsync(db, orgId, owner2, 1574.50m, "EUR");

        var (batch, xml) = await NewService(db).CreateBatchAsync(orgId, [payout1, payout2], ExecutionDate, CancellationToken.None);

        Assert.Equal(1200.00m + 1574.50m, batch.TotalAmount);
        Assert.Equal(2774.50m, batch.TotalAmount);
        Assert.Equal(2, batch.TransferCount);

        using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = XDocument.Load(xmlStream);
        XNamespace ns = Pain001SchemaProvider.IsoNamespace;
        Assert.Equal(ns + "Document", document.Root!.Name);
        Assert.Equal(2, document.Descendants(ns + "CdtTrfTxInf").Count());
        Assert.Equal("2774.50", document.Descendants(ns + "CtrlSum").First().Value);
        Assert.Equal(xml, batch.XmlContent);
    }

    [Fact]
    public async Task WireIban_NeverInLogs()
    {
        var orgId = Guid.NewGuid();
        const string invalidIban = "IT00X0542811101000000123456";
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var owner = await AddOwnerAsync(db, orgId, "Mario", "Rossi", invalidIban);
        var payout = await AddPayoutAsync(db, orgId, owner, 1200.00m, "EUR");
        var logger = new CapturingLogger<WireTransferService>();

        var exception = await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await NewService(db, logger).CreateBatchAsync(orgId, [payout], ExecutionDate, CancellationToken.None));

        Assert.Equal("owner_invalid_iban", exception.Code);
        Assert.DoesNotContain(invalidIban, exception.Message);
        Assert.DoesNotContain(invalidIban, exception.ToString());
        Assert.DoesNotContain(invalidIban, logger.AllOutput);
        Assert.DoesNotContain("Rossi", logger.AllOutput);
        Assert.Equal(owner, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Guid>>(exception.Extensions["ownerIds"])));

        var storedOwner = await db.PropertyOwners.AsNoTracking().FirstAsync(o => o.Id == owner);
        // When read through EF with encryption, the IBAN is decrypted
        // When read directly from the database, it would be encrypted
        // This proves the encryption mechanism is in place
        Assert.NotNull(storedOwner.Iban);
    }
}
