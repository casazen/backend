using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15b: the admin tools of the service payments. The list and the detail show gross, commission, net, what was refunded and the
/// commission that went back with it, with ids and names and never an email or a phone; the commission of a supplier is its own
/// percentage (a free period is 0, with an end date or without) and changes only the payments created afterwards; the monthly
/// export is the manual invoice's source (payments collected through Stripe and refunds that succeeded in the Rome month).
/// </summary>
public class SupplierPaymentAdminServiceTests
{
    private const string AdminId = "auth0|admin-tools";

    private static SupplierPaymentAdminService Admin(ServiceRequestScenario s, decimal? vatPercent = null) =>
        new(
            s.Db,
            Options.Create(new SupplierPaymentsOptions { CommissionPercent = 10m, CommissionVatPercent = vatPercent }),
            NullLogger<SupplierPaymentAdminService>.Instance,
            s.Clock);

    private static AdminServicePaymentQuery Everything(int page = 1, int pageSize = 50) => new(null, null, null, null, null, page, pageSize);

    private static async Task<OpenedPayment> PaidAsync(ServiceRequestScenario s)
    {
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        return opened;
    }

    // ─── The list and the detail ───

    [Fact]
    public async Task List_ShowsGrossCommissionNetAndWhatWasRefunded_NewestFirst_WithNamesAndNoContact()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var older = await PaidAsync(s);
        s.Clock.Advance(TimeSpan.FromHours(1));
        var newer = await PaidAsync(s);
        await s.Payments.RefundAsync(newer.PaymentId, 1_500, null, AdminId);

        var (items, total) = await Admin(s).ListAsync(Everything());

        Assert.Equal(2, total);
        Assert.Equal([newer.PaymentId, older.PaymentId], items.Select(i => i.Id).ToArray());
        var item = items[0];
        Assert.Equal("Supplier Srl", item.SupplierName); // the legal name of the profile
        Assert.Equal("Casa Rossi", item.PayerName);
        Assert.Equal(s.SupplierOrgId, item.SupplierOrgId);
        Assert.Equal(s.HostOrgId, item.PayerOrgId);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, item.Status);
        Assert.Equal(ServicePaymentChannel.Stripe, item.PaidVia);
        Assert.Equal((6_000, 10m, 600, 5_400), (item.AmountCents, item.CommissionPercent, item.ApplicationFeeCents, item.NetCents));
        Assert.Equal((1_500, 150), (item.RefundedCents, item.CommissionRefundedCents));
        Assert.Equal(newer.IntentId, item.StripePaymentIntentId);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, item.ConnectedAccountId);
        // Nothing about people: the item has no email, phone or address.
        var members = typeof(AdminServicePaymentItem).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(members, name => name.Contains("Email", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Phone", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_Filters_ByStatus_Supplier_Late_AndCreationTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var paid = await PaidAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(2));
        var open = await s.OpenedAsync();
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, open.Request.Id, p => p.LateAt = ServiceRequestScenario.Instant.UtcDateTime);
        var admin = Admin(s);

        var paidOnes = await admin.ListAsync(Everything() with { Status = ServicePaymentStatus.Paid });
        var late = await admin.ListAsync(Everything() with { Late = true });
        var onTime = await admin.ListAsync(Everything() with { Late = false });
        var ofSupplier = await admin.ListAsync(Everything() with { SupplierOrgId = s.SupplierOrgId });
        var ofAnother = await admin.ListAsync(Everything() with { SupplierOrgId = Guid.NewGuid() });
        var recent = await admin.ListAsync(Everything() with { CreatedFrom = ServiceRequestScenario.Instant.UtcDateTime.AddDays(1) });
        var old = await admin.ListAsync(Everything() with { CreatedTo = ServiceRequestScenario.Instant.UtcDateTime.AddDays(1) });

        Assert.Equal([paid.PaymentId], paidOnes.Items.Select(i => i.Id).ToArray());
        Assert.Equal([open.PaymentId], late.Items.Select(i => i.Id).ToArray());
        Assert.Equal([paid.PaymentId], onTime.Items.Select(i => i.Id).ToArray());
        Assert.Equal(2, ofSupplier.Total);
        Assert.Equal(0, ofAnother.Total);
        Assert.Equal([open.PaymentId], recent.Items.Select(i => i.Id).ToArray());
        Assert.Equal([paid.PaymentId], old.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task List_NeedsReview_IsTheAdminsWorkQueue_WithTheReasonOfEach()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, e => e with { ApplicationFeeCents = 1 }));
        await PaidAsync(s);

        var (items, total) = await Admin(s).ListAsync(Everything() with { Status = ServicePaymentStatus.NeedsReview });

        Assert.Equal(1, total);
        Assert.Equal("review:fee", Assert.Single(items).FailureCode);
    }

    [Fact]
    public async Task List_PagesAreClampedAndCounted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await PaidAsync(s);
            s.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var admin = Admin(s);
        var second = await admin.ListAsync(Everything(page: 2, pageSize: 2));
        var absurd = await admin.ListAsync(Everything(page: -4, pageSize: 100_000));
        var tiny = await admin.ListAsync(Everything(page: 1, pageSize: 0));

        Assert.Equal(3, second.Total);
        Assert.Single(second.Items);
        Assert.Equal(3, absurd.Items.Count);
        Assert.Single(tiny.Items);
        Assert.Equal(3, tiny.Total);
    }

    [Fact]
    public async Task Get_ReturnsThePaymentWithItsRefundsInOrder_AndUnknownIs404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await PaidAsync(s);
        await s.Payments.RefundAsync(opened.PaymentId, 1_000, "primo", AdminId);
        await s.Payments.RefundAsync(opened.PaymentId, 500, "secondo", AdminId);

        var detail = await Admin(s).GetAsync(opened.PaymentId);

        Assert.Equal(opened.PaymentId, detail.Payment.Id);
        Assert.Equal(1_500, detail.Payment.RefundedCents);
        Assert.Equal(["primo", "secondo"], detail.Refunds.Select(r => r.Reason!).ToArray());
        Assert.Equal([1, 2], detail.Refunds.Select(r => r.Sequence).ToArray());
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Admin(s).GetAsync(Guid.NewGuid()));
        Assert.Equal(ServicePaymentErrors.NotFound, ex.Code);
    }

    // ─── The commission of a supplier ───

    [Fact]
    public async Task Commission_ByDefault_IsThePlatformsPercentage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var setting = await Admin(s).GetCommissionAsync(s.SupplierOrgId);

        Assert.Equal(new SupplierCommissionSetting(s.SupplierOrgId, 10m, null, null, false, 10m), setting);
    }

    [Fact]
    public async Task Commission_AFreePeriod_AppliesToThePaymentsCreatedDuringIt_AndNotToThoseBefore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var before = await s.CompletedOnlineAsync();
        var until = ServiceRequestScenario.Instant.UtcDateTime.AddDays(30);

        var setting = await Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, until, "Fornitore pilota: primo mese gratis", AdminId);
        var during = await s.CompletedOnlineAsync();

        Assert.Equal(new SupplierCommissionSetting(s.SupplierOrgId, 10m, 0m, until, true, 0m), setting);
        var paymentBefore = await s.OnlyPaymentOfAsync(before.Id);
        var paymentDuring = await s.OnlyPaymentOfAsync(during.Id);
        // A payment keeps the percentage it was created with: the change is never applied backwards.
        Assert.Equal((10m, 600), (paymentBefore.CommissionPercent, paymentBefore.ApplicationFeeCents));
        Assert.Equal((0m, 0), (paymentDuring.CommissionPercent, paymentDuring.ApplicationFeeCents));
        Assert.Equal(paymentDuring.AmountCents, paymentDuring.NetCents);
    }

    [Fact]
    public async Task Commission_AfterTheEndOfThePeriod_ThePlatformsPercentageIsBack_WithoutAnyoneTakingTheOverrideOff()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var until = ServiceRequestScenario.Instant.UtcDateTime.AddDays(30);
        await Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, until, "Primo mese gratis", AdminId);

        s.Clock.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromSeconds(1)));
        var after = await s.CompletedOnlineAsync();
        var setting = await Admin(s).GetCommissionAsync(s.SupplierOrgId);

        var payment = await s.OnlyPaymentOfAsync(after.Id);
        Assert.Equal((10m, 600), (payment.CommissionPercent, payment.ApplicationFeeCents));
        Assert.False(setting.OverrideActive);
        Assert.Equal(10m, setting.EffectivePercent);
        Assert.Equal(0m, setting.OverridePercent); // still stored: the audit trail and the profile show what it was
    }

    [Fact]
    public async Task Commission_AnOwnPercentageWithNoEnd_StaysUntilRemoved_AndRemovingItRestoresThePlatformPercentage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var admin = Admin(s);
        await admin.SetCommissionAsync(s.SupplierOrgId, 7.5m, null, "Accordo commerciale", AdminId);
        s.Clock.Advance(TimeSpan.FromDays(365));

        var set = await admin.GetCommissionAsync(s.SupplierOrgId);
        var removed = await admin.SetCommissionAsync(s.SupplierOrgId, null, null, "Fine dell'accordo", AdminId);

        Assert.Equal((7.5m, null, true, 7.5m), (set.OverridePercent, set.OverrideUntil, set.OverrideActive, set.EffectivePercent));
        Assert.Equal(new SupplierCommissionSetting(s.SupplierOrgId, 10m, null, null, false, 10m), removed);
    }

    [Fact]
    public async Task Commission_RemovingTheOverride_AlsoRemovesItsEnd()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var admin = Admin(s);
        await admin.SetCommissionAsync(s.SupplierOrgId, 0m, ServiceRequestScenario.Instant.UtcDateTime.AddDays(10), "Gratis", AdminId);

        await admin.SetCommissionAsync(s.SupplierOrgId, null, null, "Annullato", AdminId);

        var profile = await s.Db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
        Assert.Null(profile.CommissionPercentOverride);
        Assert.Null(profile.CommissionOverrideUntil);
    }

    [Fact]
    public async Task Commission_EveryChange_LeavesAnAuditEntry_WithWhoWhenWhyAndTheOldAndNewValue()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var admin = Admin(s);

        await admin.SetCommissionAsync(s.SupplierOrgId, 0m, ServiceRequestScenario.Instant.UtcDateTime.AddDays(30), "Primo mese gratis", AdminId);
        s.Clock.Advance(TimeSpan.FromDays(1));
        await admin.SetCommissionAsync(s.SupplierOrgId, 5m, null, "Accordo", "auth0|another-admin");

        var entries = await s.Db.SupplierAdminAuditEntries.AsNoTracking().OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(SupplierAdminAuditAction.CommissionChanged, e.Action));
        Assert.All(entries, e => Assert.Equal(s.SupplierOrgId, e.SupplierOrgId));
        Assert.Equal([AdminId, "auth0|another-admin"], entries.Select(e => e.ActorUserId).ToArray());
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, entries[0].OccurredAt);
        Assert.Contains("platform default -> 0% until 2026-11-07", entries[0].Reason);
        Assert.Contains("Primo mese gratis", entries[0].Reason);
        Assert.Contains("0% until 2026-11-07T10:00:00Z -> 5%", entries[1].Reason);
        Assert.Null(entries[0].PreviousStatus);
        Assert.InRange(entries[0].Reason!.Length, 1, 500);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(50.01)]
    [InlineData(100)]
    [InlineData(10.123)]
    public async Task Commission_APercentageOutsideTheRange_OrWithMoreThanTwoDecimals_Is422_AndChangesNothing(double percent)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Admin(s).SetCommissionAsync(s.SupplierOrgId, (decimal)percent, null, "x", AdminId));

        Assert.Equal(ServicePaymentErrors.CommissionInvalid, ex.Code);
        Assert.Equal(50m, Assert.Single(ex.MessageArgs));
        Assert.Empty(await s.Db.SupplierAdminAuditEntries.ToListAsync());
        Assert.Null((await s.Db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == s.SupplierOrgId)).CommissionPercentOverride);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(10)]
    [InlineData(50)]
    public async Task Commission_TheEdgesOfTheRange_AreValid(double percent)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var setting = await Admin(s).SetCommissionAsync(s.SupplierOrgId, (decimal)percent, null, "limite", AdminId);

        Assert.Equal((decimal)percent, setting.EffectivePercent);
    }

    [Fact]
    public async Task Commission_AnEndInThePast_OrWithoutAPercentage_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var admin = Admin(s);
        var now = ServiceRequestScenario.Instant.UtcDateTime;

        var past = await Assert.ThrowsAsync<DomainRuleException>(() => admin.SetCommissionAsync(s.SupplierOrgId, 0m, now.AddSeconds(-1), "x", AdminId));
        var rightNow = await Assert.ThrowsAsync<DomainRuleException>(() => admin.SetCommissionAsync(s.SupplierOrgId, 0m, now, "x", AdminId));
        var noPercent = await Assert.ThrowsAsync<DomainRuleException>(() => admin.SetCommissionAsync(s.SupplierOrgId, null, now.AddDays(5), "x", AdminId));

        Assert.All([past, rightNow, noPercent], ex => Assert.Equal(ServicePaymentErrors.CommissionUntilInvalid, ex.Code));
        Assert.Empty(await s.Db.SupplierAdminAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Commission_AnUnknownSupplier_Is404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var get = await Assert.ThrowsAsync<NotFoundException>(() => Admin(s).GetCommissionAsync(Guid.NewGuid()));
        var set = await Assert.ThrowsAsync<NotFoundException>(() => Admin(s).SetCommissionAsync(Guid.NewGuid(), 0m, null, "x", AdminId));

        Assert.Equal(SupplierAdminErrorCodes.SupplierNotFound, get.Code);
        Assert.Equal(SupplierAdminErrorCodes.SupplierNotFound, set.Code);
    }

    [Fact]
    public async Task Commission_TheReasonAndTheActorAreRequired_ByTheService()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, null, " ", AdminId));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, null, "x", ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, null, new string('x', 501), AdminId));
    }

    [Fact]
    public async Task Commission_AnEndGivenInLocalTime_IsStoredAsUtc()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var local = DateTime.SpecifyKind(ServiceRequestScenario.Instant.UtcDateTime.AddDays(3), DateTimeKind.Local);

        var setting = await Admin(s).SetCommissionAsync(s.SupplierOrgId, 0m, local, "x", AdminId);

        Assert.Equal(DateTimeKind.Utc, setting.OverrideUntil!.Value.Kind);
        Assert.Equal(local.ToUniversalTime(), setting.OverrideUntil);
    }

    // ─── The monthly export ───

    private static async Task<OpenedPayment> PaidAtAsync(ServiceRequestScenario s, DateTime paidAtUtc)
    {
        var opened = await PaidAsync(s);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, opened.Request.Id, p => p.PaidAt = paidAtUtc);
        return opened;
    }

    [Fact]
    public async Task Export_TheMonthIsTheCalendarMonthInRome_NotInUtc()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // 30 September 22:30 UTC is 1 October 00:30 in Rome; 31 October 23:30 UTC is 1 November 00:30 in Rome.
        var firstOfOctober = await PaidAtAsync(s, new DateTime(2026, 9, 30, 22, 30, 0, DateTimeKind.Utc));
        var lastOfOctober = await PaidAtAsync(s, new DateTime(2026, 10, 31, 22, 59, 0, DateTimeKind.Utc));
        var firstOfNovember = await PaidAtAsync(s, new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc));
        var lastOfSeptember = await PaidAtAsync(s, new DateTime(2026, 9, 30, 21, 30, 0, DateTimeKind.Utc));
        s.Clock.Advance(TimeSpan.FromDays(40)); // the export is asked in November

        var october = await Admin(s).ExportCommissionsAsync(2026, 10);
        var november = await Admin(s).ExportCommissionsAsync(2026, 11);
        var september = await Admin(s).ExportCommissionsAsync(2026, 9);

        Assert.Equal(
            new[] { firstOfOctober.PaymentId, lastOfOctober.PaymentId }.Order().ToArray(),
            october.Rows.Select(r => r.PaymentId).Order().ToArray());
        Assert.Equal([firstOfNovember.PaymentId], november.Rows.Select(r => r.PaymentId).ToArray());
        Assert.Equal([lastOfSeptember.PaymentId], september.Rows.Select(r => r.PaymentId).ToArray());
        Assert.Equal(new DateOnly(2026, 10, 1), october.Rows.Single(r => r.PaymentId == firstOfOctober.PaymentId).Date);
        Assert.Equal(new DateOnly(2026, 10, 31), october.Rows.Single(r => r.PaymentId == lastOfOctober.PaymentId).Date);
    }

    [Fact]
    public async Task Export_ALinePerPayment_WithGrossCommissionNetAndTheFiscalDataOfTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var vat = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
        vat.VatNumber = "IT01234567890";
        await s.Db.SaveChangesAsync();
        var opened = await PaidAsync(s);

        var export = await Admin(s).ExportCommissionsAsync(2026, 10);

        var row = Assert.Single(export.Rows);
        Assert.Equal(CommissionExportRowType.Payment, row.Type);
        Assert.Equal(new DateOnly(2026, 10, 8), row.Date);
        Assert.Equal((opened.PaymentId, opened.Request.Id, s.SupplierOrgId), (row.PaymentId, row.ServiceRequestId, row.SupplierOrgId));
        Assert.Equal(("Supplier Srl", "IT01234567890", s.HostOrgId, "Casa Rossi"), (row.SupplierName, row.SupplierVatNumber, row.PayerOrgId, row.PayerName));
        Assert.Equal(("eur", 6_000L, 10m, 600L, 5_400L), (row.Currency, row.GrossCents, row.CommissionPercent, row.CommissionCents, row.NetCents));
    }

    [Fact]
    public async Task Export_ARefundIsANegativeLine_OfTheMonthItSucceededIn_WithTheCommissionThatWentBack()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await PaidAtAsync(s, new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc));
        await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, AdminId); // 8 October

        var october = await Admin(s).ExportCommissionsAsync(2026, 10);
        var september = await Admin(s).ExportCommissionsAsync(2026, 9);

        var refund = Assert.Single(october.Rows);
        Assert.Equal(CommissionExportRowType.Refund, refund.Type);
        Assert.Equal(new DateOnly(2026, 10, 8), refund.Date);
        Assert.Equal(opened.PaymentId, refund.PaymentId);
        Assert.Equal((-1_500L, -150L, -1_350L, 10m), (refund.GrossCents, refund.CommissionCents, refund.NetCents, refund.CommissionPercent));
        // The payment itself belongs to the month it was paid in.
        var payment = Assert.Single(september.Rows);
        Assert.Equal(CommissionExportRowType.Payment, payment.Type);
        Assert.Equal(6_000, payment.GrossCents);
    }

    [Fact]
    public async Task Export_AMonthWithAPaymentAndItsRefund_NetsToWhatCasaZenKept()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await PaidAsync(s);
        await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, AdminId);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Payments.RefundAsync(opened.PaymentId, 1_234, null, AdminId);

        var export = await Admin(s).ExportCommissionsAsync(2026, 10);

        Assert.Equal(3, export.Rows.Count);
        Assert.Equal(600 - 150 - 123, export.Rows.Sum(r => r.CommissionCents));
        Assert.Equal(6_000 - 1_500 - 1_234, export.Rows.Sum(r => r.GrossCents));
    }

    [Fact]
    public async Task Export_APaymentReceivedOutsideCasaZen_CarriesNoCommissionAndIsNotInTheFile()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.CompletedOnlineAsync();
        await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Contanti");

        var export = await Admin(s).ExportCommissionsAsync(2026, 10);

        Assert.Empty(export.Rows);
    }

    [Fact]
    public async Task Export_AFailedRefund_AndAPendingOne_AreNotInTheFile()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await PaidAsync(s);
        s.Gateway.NewRefundStatus = "pending";
        await s.Payments.RefundAsync(opened.PaymentId, 1_000, null, AdminId);
        s.Gateway.NewRefundStatus = "succeeded";
        s.Gateway.FailNextRefund(new Stripe.StripeException(System.Net.HttpStatusCode.BadRequest, new Stripe.StripeError { Code = "charge_disputed" }, "no"));
        await s.Payments.RefundAsync(opened.PaymentId, 500, null, AdminId);

        var export = await Admin(s).ExportCommissionsAsync(2026, 10);

        Assert.Equal([CommissionExportRowType.Payment], export.Rows.Select(r => r.Type).ToArray());
    }

    [Fact]
    public async Task Export_TheVatRateOfTheConfiguration_IsRepeated_AndEmptyUntilTheConsultantDecides()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await PaidAsync(s);

        Assert.Null((await Admin(s).ExportCommissionsAsync(2026, 10)).VatPercent);
        Assert.Equal(22m, (await Admin(s, vatPercent: 22m).ExportCommissionsAsync(2026, 10)).VatPercent);
    }

    [Theory]
    [InlineData(2026, 11)] // the future
    [InlineData(2027, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1999, 12)]
    public async Task Export_AMonthThatIsInTheFutureOrNotAMonth_Is422(int year, int month)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Admin(s).ExportCommissionsAsync(year, month));

        Assert.Equal(ServicePaymentErrors.ExportMonthInvalid, ex.Code);
    }

    [Fact]
    public async Task Export_TheCurrentMonth_IsAllowed_AndAnEmptyMonthIsJustEmpty()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var current = await Admin(s).ExportCommissionsAsync(2026, 10);
        var past = await Admin(s).ExportCommissionsAsync(2026, 1);

        Assert.Empty(current.Rows);
        Assert.Empty(past.Rows);
        Assert.Equal((2026, 10), (current.Year, current.Month));
    }
}
