using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/RES-PAYOUTS: the two reads the wire page needs, which payouts can still be paid (<c>GET api/wire-transfers/payable-payouts?currency=</c>) and which ones a batch pays
/// (<c>GET api/wire-transfers/batch/{id}/payouts</c>), so that after a reload the payouts already in a batch are not offered again. One proof for each rule of money,
/// tenant, permission and privacy of the reads: no database server, no network (an in-memory database with the encrypted columns on).
/// </summary>
public class WirePayableReadsTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private const string UserId = "auth0|collaborator-solo-alcuni";

    /// <summary>
    /// Money rule proof: a payout is payable only while it is confirmed, of a published version, with something to transfer and in no batch. A payout in a batch, a reversed
    /// one (also one reversed after its batch was made), one of a version never published and one with nothing to transfer are not in the list, a payable one is, oldest first.
    /// What the list offers is what a batch accepts: one batch of the whole list is made, and after it the list is empty.
    /// </summary>
    [Fact]
    public async Task PayablePayouts_ExcludeThoseAlreadyInABatchAndReversedOnes_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);
        var giulia = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", SwissIban);
        var luigi = await AddOwnerAsync(db, orgId, "Luigi", "Bianchi", OtherSwissIban, legalName: "Bianchi Immobiliare SA");
        var mario = await AddOwnerAsync(db, orgId, "Mario", "Rossi", LiechtensteinIban);

        // The payouts that can be paid: confirmed, of a published version, in no batch. The newer one was added first, so that the order is the confirmation's.
        var newer = await AddPayoutAsync(db, orgId, luigi, 800.50m);
        var older = await AddPayoutAsync(db, orgId, giulia, 1200.00m);
        await SetConfirmedAtAsync(db, older, Now.UtcDateTime.AddDays(-2));
        await SetConfirmedAtAsync(db, newer, Now.UtcDateTime.AddDays(-1));

        // The payouts that cannot: already in a batch, reversed, reversed after its batch was made, of a version never published to the owner, with nothing to transfer.
        var batched = await AddPayoutAsync(db, orgId, mario, 300.00m);
        var reversedAfterBatch = await AddPayoutAsync(db, orgId, mario, 320.00m);
        var reversed = await AddPayoutAsync(db, orgId, mario, 310.00m, status: OwnerPayoutStatus.Reversed);
        var unpublished = await AddPayoutAsync(db, orgId, mario, 330.00m, published: false);
        var nothingToTransfer = await AddPayoutAsync(db, orgId, mario, 0.00m, withholding: 50.00m);
        var (firstBatch, _) = await service.CreateBatchAsync(orgId, [batched, reversedAfterBatch], ExecutionDate);
        await ReverseAsync(db, reversedAfterBatch);

        var payable = await service.ListPayablePayoutsAsync(orgId, "CHF");

        Assert.Equal(new[] { older, newer }, payable.Select(p => p.PayoutId).ToArray());
        Assert.DoesNotContain(payable, p => new[] { batched, reversedAfterBatch, reversed, unpublished, nothingToTransfer }.Contains(p.PayoutId));
        var first = payable[0];
        Assert.Equal(giulia, first.OwnerId);
        Assert.Equal("Giulia Verdi", first.OwnerName);
        Assert.Equal(2026, first.PeriodYear);
        Assert.Equal(10, first.PeriodMonth);
        Assert.Equal(1200.00m, first.Amount);
        Assert.Equal("CHF", first.Currency);
        Assert.Equal(OwnerPayoutStatus.Confirmed, first.Status);
        Assert.True(first.OwnerHasIban);
        Assert.Equal("Bianchi Immobiliare SA", payable[1].OwnerName);

        // The two that are in a batch are in the payouts of that batch, and the one reversed after it was made says so.
        var inFirstBatch = (await service.GetBatchPayoutsAsync(orgId, firstBatch.Id)).Payouts;
        Assert.Equal(new[] { batched, reversedAfterBatch }.Order(), inFirstBatch.Select(p => p.PayoutId).Order());
        Assert.Equal(OwnerPayoutStatus.Confirmed, inFirstBatch.Single(p => p.PayoutId == batched).Status);
        Assert.Equal(OwnerPayoutStatus.Reversed, inFirstBatch.Single(p => p.PayoutId == reversedAfterBatch).Status);

        // The list is what the page can pay: one batch of the whole list is accepted and, after it, a reload offers none of them again.
        var (batch, _) = await service.CreateBatchAsync(orgId, payable.Select(p => p.PayoutId).ToList(), ExecutionDate);
        Assert.Equal(2, batch.TransferCount);
        Assert.Equal(2000.50m, batch.TotalAmount);
        Assert.Empty(await service.ListPayablePayoutsAsync(orgId, "CHF"));
    }

    /// <summary>
    /// Money rule proof: a list is one currency. The CHF list holds the CHF payouts (a code stored in lower case is the same currency, as a batch reads it) and none of the
    /// EUR ones, the EUR list the reverse, and one batch of a list is accepted (a batch is one currency). There is no list of every currency: a missing currency, or one with
    /// no wire file, is refused (422 <c>wire_currency_not_supported</c>) and nothing is listed.
    /// </summary>
    [Fact]
    public async Task PayablePayouts_AreNeverMixedAcrossCurrencies_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);
        var chf = await AddTwoPayoutsAsync(db, orgId, "CHF");
        var eur = await AddTwoPayoutsAsync(db, orgId, "EUR");
        var anna = await AddOwnerAsync(db, orgId, "Anna", "Neri", LiechtensteinIban);
        var lowerCase = await AddPayoutAsync(db, orgId, anna, 15.00m, "chf");
        await AddPayoutAsync(db, orgId, anna, 25.00m, "USD");

        var chfList = await service.ListPayablePayoutsAsync(orgId, "CHF");
        var eurList = await service.ListPayablePayoutsAsync(orgId, " eur ");

        Assert.Equal(chf.Append(lowerCase).Order(), chfList.Select(p => p.PayoutId).Order());
        Assert.All(chfList, p => Assert.Equal("CHF", p.Currency));
        Assert.Equal(eur.Order(), eurList.Select(p => p.PayoutId).Order());
        Assert.All(eurList, p => Assert.Equal("EUR", p.Currency));
        Assert.Empty(chfList.Select(p => p.PayoutId).Intersect(eurList.Select(p => p.PayoutId)));

        // A list is a batch that can be made: each one goes into one file, and no file would be mixed.
        Assert.Equal("CHF", (await service.CreateBatchAsync(orgId, chfList.Select(p => p.PayoutId).ToList(), ExecutionDate)).Batch.Currency);
        Assert.Equal("EUR", (await service.CreateBatchAsync(orgId, eurList.Select(p => p.PayoutId).ToList(), ExecutionDate)).Batch.Currency);

        foreach (var notAList in new string?[] { null, string.Empty, "  ", "USD", "GBP", "CH", "CHF,EUR" })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.ListPayablePayoutsAsync(orgId, notAList));
            Assert.Equal(WireTransferErrors.CurrencyNotSupported, refused.Code);
            Assert.Equal("WireCurrencyNotSupported", refused.MessageKey);
        }
    }

    /// <summary>
    /// Tenant rule proof: a batch of another org does not exist for the caller (404), whether the global filter of the context is on or off (the service asks for the org
    /// itself), and the payable payouts of another org are never in a list. The org of the batch reads its payouts, with the state of the batch.
    /// </summary>
    [Fact]
    public async Task WireBatchPayouts_OfAnotherOrganization_AreNotFound_Test()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var options = NewOptions();
        var keys = new EphemeralDataProtectionProvider();
        await using var db = NewDb(orgId, options: options, provider: keys);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);
        var paid = await AddTwoPayoutsAsync(db, orgId);
        var (batch, _) = await service.CreateBatchAsync(orgId, paid, ExecutionDate);
        await service.ConfirmDownloadAsync(orgId, batch.Id);
        var owner = await AddOwnerAsync(db, orgId, "Giulia", "Verdi", SwissIban);
        var stillPayable = await AddPayoutAsync(db, orgId, owner, 90.00m);

        // The org of the batch: its payouts in the order of the transfers (the confirmation, then the id), the amounts of the file and the state of the batch.
        var own = await service.GetBatchPayoutsAsync(orgId, batch.Id);

        Assert.Equal(batch.Id, own.Batch.Id);
        Assert.Equal(WireBatchStatus.Downloaded, own.Batch.Status);
        Assert.Equal("CHF", own.Batch.Currency);
        Assert.Equal(2, own.Batch.TransferCount);
        Assert.Equal(paid.Order(), own.Payouts.Select(p => p.PayoutId).Order());
        Assert.Equal(new[] { 1200.00m, 1574.50m }, own.Payouts.Select(p => p.Amount).Order().ToArray());
        Assert.Equal(new[] { "Luigi Bianchi", "Mario Rossi" }, own.Payouts.Select(p => p.OwnerName).Order().ToArray());
        Assert.All(own.Payouts, p => Assert.Equal(OwnerPayoutStatus.Confirmed, p.Status));
        Assert.All(own.Payouts, p => Assert.True(p.OwnerHasIban));
        Assert.Equal(stillPayable, Assert.Single(await service.ListPayablePayoutsAsync(orgId, "CHF")).PayoutId);

        // Another org, with the global filter on and with it off: the batch is not found and none of the payouts of the first org is in its list.
        foreach (var filterEnabled in new[] { true, false })
        {
            await using var otherDb = NewDb(otherOrgId, filterEnabled, options, keys);
            var other = NewService(otherDb);

            await Assert.ThrowsAsync<NotFoundException>(() => other.GetBatchPayoutsAsync(otherOrgId, batch.Id));
            Assert.Empty(await other.ListPayablePayoutsAsync(otherOrgId, "CHF"));
        }

        // A batch that does not exist is the same answer.
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetBatchPayoutsAsync(orgId, Guid.NewGuid()));
    }

    /// <summary>
    /// Permission rule proof: the two reads list the payouts of every owner, so a caller whose reach is limited to some properties (a collaborator «Solo alcuni», an account
    /// in no org team), one with no scope at all and one with no org get 403 before the service is asked anything; a caller who reaches the whole org is served, and the
    /// answer is not cached.
    /// </summary>
    [Fact]
    public async Task WireReads_PropertyLimitedCaller_IsForbidden_Test()
    {
        foreach (var restricted in new HostScope?[] { new HostScope(OrgId, GrantedToUserId: UserId), new HostScope(OrgId, OwnerId: UserId), null })
        {
            var service = new Mock<IWireTransferService>(MockBehavior.Strict);
            var controller = NewController(service.Object, OrgId, restricted);

            Assert.IsType<ForbidResult>((await controller.ListPayablePayouts("CHF")).Result);
            Assert.IsType<ForbidResult>((await controller.GetBatchPayouts(Guid.NewGuid())).Result);
            service.VerifyNoOtherCalls();
        }

        var noOrg = new Mock<IWireTransferService>(MockBehavior.Strict);
        var withoutOrg = NewController(noOrg.Object, null, new HostScope(OrgId));
        Assert.IsType<ForbidResult>((await withoutOrg.ListPayablePayouts("CHF")).Result);
        Assert.IsType<ForbidResult>((await withoutOrg.GetBatchPayouts(Guid.NewGuid())).Result);
        noOrg.VerifyNoOtherCalls();

        var batchId = Guid.NewGuid();
        var payout = new WirePayoutSummary(Guid.NewGuid(), Guid.NewGuid(), "Giulia Verdi", 2026, 10, 1200.00m, "CHF", Now.UtcDateTime, OwnerPayoutStatus.Confirmed, true);
        var batch = new WireBatchSummary(batchId, "CHF", WireBatchStatus.Generated, 1, 1200.00m, ExecutionDate, WireMessageFormats.Pain001V09Ch03, Now.UtcDateTime, null, null);
        var served = new Mock<IWireTransferService>();
        served.Setup(s => s.ListPayablePayoutsAsync(OrgId, "CHF", It.IsAny<CancellationToken>())).ReturnsAsync([payout]);
        served.Setup(s => s.GetBatchPayoutsAsync(OrgId, batchId, It.IsAny<CancellationToken>())).ReturnsAsync(new WireBatchPayoutsSummary(batch, [payout]));
        var wholeOrg = NewController(served.Object, OrgId, new HostScope(OrgId));

        var list = Assert.IsType<OkObjectResult>((await wholeOrg.ListPayablePayouts("CHF")).Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WirePayoutResponse>>(list.Value));
        Assert.Equal(payout.PayoutId, listed.PayoutId);
        Assert.Equal("no-store", wholeOrg.Response.Headers.CacheControl.ToString());

        var read = Assert.IsType<OkObjectResult>((await wholeOrg.GetBatchPayouts(batchId)).Result);
        var response = Assert.IsType<WireBatchPayoutsResponse>(read.Value);
        Assert.Equal(WireBatchStatus.Generated, response.Batch.Status);
        Assert.Equal(payout.PayoutId, Assert.Single(response.Payouts).PayoutId);
    }

    /// <summary>
    /// Permission rule proof: the two reads need the financial permission of every wire endpoint, <c>agency.wire.manage</c>, which they get from the class of the controller. The
    /// class names that policy and is not anonymous, and neither action overrides it: no <c>AllowAnonymous</c> on the action and no other requirement beside the policy of the
    /// class, so what a request has to pass is that policy and not a weaker one (the integration tests of the roles run in CI only).
    /// </summary>
    [Fact]
    public void WireReads_AreBehindTheWireManagePolicy_Test()
    {
        var controller = typeof(WireTransferController);
        Assert.Empty(controller.GetCustomAttributes(typeof(IAllowAnonymous), true));

        foreach (var action in new[] { nameof(WireTransferController.ListPayablePayouts), nameof(WireTransferController.GetBatchPayouts) })
        {
            var method = controller.GetMethod(action)!;
            var requirements = controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Concat(method.GetCustomAttributes(typeof(AuthorizeAttribute), true)).Cast<AuthorizeAttribute>();

            Assert.Empty(method.GetCustomAttributes(typeof(IAllowAnonymous), true));
            Assert.Equal(CasazenPolicies.AgencyWireManage, Assert.Single(requirements.Select(a => a.Policy).Distinct()));
        }
    }

    /// <summary>
    /// Privacy rule proof: the page is told whether an owner has an IBAN, as a yes or a no, and never the IBAN: not in the payable list, not in the payouts of a batch, and no
    /// address, no debit account, no file. An owner with no IBAN, or with a blank one, is a no. The members of the answers are pinned, so that a new one has to be chosen on
    /// purpose.
    /// </summary>
    [Fact]
    public async Task WirePayouts_ShowOnlyWhetherTheOwnerHasAnIban_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb(orgId);
        await AddIssuerAsync(db, orgId);
        var service = NewService(db);
        var batched = await AddTwoPayoutsAsync(db, orgId);
        var (batch, _) = await service.CreateBatchAsync(orgId, batched, ExecutionDate);
        var withIban = await AddPayoutAsync(db, orgId, await AddOwnerAsync(db, orgId, "Giulia", "Verdi", LiechtensteinIban), 40.00m);
        var withoutIban = await AddPayoutAsync(db, orgId, await AddOwnerAsync(db, orgId, "Anna", "Neri", null), 50.00m);
        var blankIban = await AddPayoutAsync(db, orgId, await AddOwnerAsync(db, orgId, "Paola", "Gialli", "  "), 60.00m);

        var payable = await service.ListPayablePayoutsAsync(orgId, "CHF");
        var inBatch = await service.GetBatchPayoutsAsync(orgId, batch.Id);

        Assert.True(payable.Single(p => p.PayoutId == withIban).OwnerHasIban);
        Assert.False(payable.Single(p => p.PayoutId == withoutIban).OwnerHasIban);
        Assert.False(payable.Single(p => p.PayoutId == blankIban).OwnerHasIban);
        Assert.All(inBatch.Payouts, p => Assert.True(p.OwnerHasIban));

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payableJson = JsonSerializer.Serialize(payable.Select(WirePayoutResponse.From).ToList(), web);
        var batchJson = JsonSerializer.Serialize(WireBatchPayoutsResponse.From(inBatch), web);
        foreach (var json in new[] { payableJson, batchJson })
        {
            foreach (var secret in new[] { SwissIban, OtherSwissIban, LiechtensteinIban, DebitIban, "Via Cantonale", "Via Nassa", "Lugano", "<Document", "debit" })
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        string[] payoutMembers = ["amount", "confirmedAt", "currency", "ownerHasIban", "ownerId", "ownerName", "payoutId", "periodMonth", "periodYear", "status"];
        using var payableDocument = JsonDocument.Parse(payableJson);
        Assert.Equal(payoutMembers, payableDocument.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        using var batchDocument = JsonDocument.Parse(batchJson);
        Assert.Equal(new[] { "batch", "payouts" }, batchDocument.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            new[] { "createdAt", "currency", "downloadedAt", "executionDate", "format", "id", "paidAt", "status", "totalAmount", "transferCount" },
            batchDocument.RootElement.GetProperty("batch").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(payoutMembers, batchDocument.RootElement.GetProperty("payouts")[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The instant a payout was confirmed: the reads list the oldest first.</summary>
    private static async Task SetConfirmedAtAsync(AppDbContext db, Guid payoutId, DateTime confirmedAt)
    {
        var payout = await db.OwnerPayouts.SingleAsync(p => p.Id == payoutId);
        payout.ConfirmedAt = confirmedAt;
        await db.SaveChangesAsync();
    }

    /// <summary>What <c>OwnerPayoutService.ReversePayoutAsync</c> does to the payout row (it does not know about the wire batches).</summary>
    private static async Task ReverseAsync(AppDbContext db, Guid payoutId)
    {
        var payout = await db.OwnerPayouts.SingleAsync(p => p.Id == payoutId);
        payout.Status = OwnerPayoutStatus.Reversed;
        payout.ReversedAt = Now.UtcDateTime;
        payout.ReversalReason = "reversed after the batch";
        await db.SaveChangesAsync();
    }

    /// <summary>The controller of the wire page for a caller of <paramref name="orgId"/> (<c>null</c>: of no org) whose reach on the properties is <paramref name="scope"/>.</summary>
    private static WireTransferController NewController(IWireTransferService service, Guid? orgId, HostScope? scope)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var reach = new Mock<IHostScopeResolver>();
        reach.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var identity = new ClaimsIdentity([new Claim("sub", UserId), new Claim(ClaimTypes.NameIdentifier, UserId)], "test");

        return new WireTransferController(service, org.Object, reach.Object, NullLogger<WireTransferController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }
}
