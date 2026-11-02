using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The import of a bank statement on the rents, on EF InMemory without the web host: what the preview proposes inside the caller's scope,
/// that the confirmation goes through the real offline payment of the ledger (state, date, note, who), that the same movement is never
/// imported twice, what is remembered of who paid (hashes only) and that nothing readable reaches the log. The cases of the whole request
/// (authorization, JSON, multipart) are <c>LongRentBankImportIntegrationTests</c>.
/// </summary>
public class RentBankImportServiceTests
{
    private const string OwnerA = "auth0|owner-a";
    private const string OwnerB = "auth0|owner-b";
    private const string PayerIban = "DE89370400440532013000";
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private const string FirstFile =
        "Data contabile;Data valuta;Descrizione;Importo\r\n"
        + "03/10/2026;03/10/2026;BONIFICO DA VERDI GIULIA CANONE OTTOBRE 2026 IBAN DE89 3704 0044 0532 0130 00;900,00\r\n"
        + "04/10/2026;04/10/2026;BONIFICO DA ROSSI MARIO AFFITTO;750,00\r\n"
        + "05/10/2026;05/10/2026;PAGAMENTO POS;-45,50\r\n"
        + "06/10/2026;06/10/2026;STIPENDIO;2.100,00\r\n";

    private sealed class Fixture
    {
        public required AppDbContext Db { get; init; }

        public required RentBankImportService Service { get; init; }

        public required RentBillingService Billing { get; init; }

        public required ListLogger Log { get; init; }

        public required FakeTimeProvider Clock { get; init; }

        public required IConfiguration Configuration { get; init; }

        /// <summary>The import service over another ledger service (one that fails in some way) and the same context, clock and secret.</summary>
        public RentBankImportService ServiceOver(IRentBillingService billing) => new(Db, billing, Log, Clock, Configuration);

        public required Guid OrgId { get; init; }

        public required Guid PropertyA { get; init; }

        public required Guid PropertyB { get; init; }

        public required Guid VerdiLease { get; init; }

        public required Guid VerdiOct { get; init; }

        public required Guid VerdiNov { get; init; }

        public required Guid RossiLease { get; init; }

        public required Guid RossiOct { get; init; }

        public HostScope OrgWide => new(OrgId);

        public HostScope OwnerOfB => new(OrgId, OwnerId: OwnerB);
    }

    /// <summary>The secret of the signed links, from which the key of the payer hashes is made (<see cref="BankPayerHash.DeriveKey"/>).</summary>
    private static IConfiguration WithSecret() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SIGNED_LINK_SECRET"] = "a-long-random-secret-of-the-server" }).Build();

    private static async Task<Fixture> CreateAsync(FailingSaveInterceptor? interceptor = null, bool withSecret = true)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        var db = new AppDbContext(builder.Options);
        var org = new OrgEntity
        {
            Name = "Casa Rossi Srl",
            Slug = $"casa-rossi-{Guid.NewGuid():N}",
            DisplayName = "Casa Rossi",
            ContactEmail = "info@casarossi.example",
            IsActive = true,
        };
        var propertyA = new Property { OrgId = org.Id, OwnerId = OwnerA, Name = "Bilocale Sparano", Address = "Via Sparano 1", City = "Bari" };
        var propertyB = new Property { OrgId = org.Id, OwnerId = OwnerB, Name = "Trilocale Murat", Address = "Via Murat 7", City = "Bari" };
        var verdi = Lease(org, propertyA, "Giulia", "Verdi", 900m);
        var rossi = Lease(org, propertyB, "Mario", "Rossi", 750m);
        var verdiSchedule = new RentSchedule { OrgId = org.Id, LeaseContractId = verdi.Id, BillingDayOfMonth = 5, Amount = 900m, IsActive = true };
        var rossiSchedule = new RentSchedule { OrgId = org.Id, LeaseContractId = rossi.Id, BillingDayOfMonth = 10, Amount = 750m, IsActive = true };
        var verdiOct = Installment(org, verdi, verdiSchedule, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), 900m);
        var verdiNov = Installment(org, verdi, verdiSchedule, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), 900m);
        var rossiOct = Installment(org, rossi, rossiSchedule, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), 750m);
        db.AddRange(org, propertyA, propertyB, verdi, rossi, verdiSchedule, rossiSchedule, verdiOct, verdiNov, rossiOct);
        await db.SaveChangesAsync();

        var clock = new FakeTimeProvider(Start);
        var billing = new RentBillingService(
            db,
            Mock.Of<IStripeService>(),
            new RecordingEmailQueue(),
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().Build(),
            NullLogger<RentBillingService>.Instance,
            clock);
        var log = new ListLogger();
        var configuration = withSecret ? WithSecret() : new ConfigurationBuilder().Build();
        return new Fixture
        {
            Db = db,
            Billing = billing,
            Service = new RentBankImportService(db, billing, log, clock, configuration),
            Log = log,
            Clock = clock,
            Configuration = configuration,
            OrgId = org.Id,
            PropertyA = propertyA.Id,
            PropertyB = propertyB.Id,
            VerdiLease = verdi.Id,
            VerdiOct = verdiOct.Id,
            VerdiNov = verdiNov.Id,
            RossiLease = rossi.Id,
            RossiOct = rossiOct.Id,
        };
    }

    private static LeaseContract Lease(OrgEntity org, Property property, string first, string last, decimal rent) => new()
    {
        OrgId = org.Id,
        PropertyId = property.Id,
        Status = LeaseStatus.Registered,
        StartDate = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2028, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent = rent,
        Parties =
        [
            new Party { Role = PartyRole.Tenant, FirstName = first, LastName = last, FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = $"{last.ToLowerInvariant()}@tenants.example" },
        ],
    };

    private static RentLedgerEntry Installment(OrgEntity org, LeaseContract lease, RentSchedule schedule, DateOnly start, DateOnly due, decimal amount) => new()
    {
        OrgId = org.Id,
        LeaseContractId = lease.Id,
        RentScheduleId = schedule.Id,
        PeriodStart = start,
        PeriodEnd = start.AddMonths(1).AddDays(-1),
        DueDate = due,
        AmountDue = amount,
    };

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static Task<bool> Allowed(Guid propertyId, CancellationToken cancellationToken) => Task.FromResult(true);

    private static Func<Guid, CancellationToken, Task<bool>> OnlyFor(params Guid[] properties) =>
        (propertyId, _) => Task.FromResult(properties.Contains(propertyId));

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task Preview_ProposesTheInstallmentsOfTheCredits_AndWritesNothing()
    {
        var f = await CreateAsync();

        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);

        Assert.Equal(BankStatementFormat.Csv, preview.Format);
        Assert.Equal(new RentBankImportSummary(Movements: 4, Credits: 3, AlreadyImported: 0, Suggested: 2, Confident: 2, Unmatched: 1), preview.Summary);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        Assert.Equal(RentBankConfidence.High, verdi.Confidence);
        Assert.Equal(f.VerdiOct, verdi.Installment.Id);
        Assert.Equal("Giulia Verdi", verdi.Installment.TenantName);
        Assert.Equal("Bilocale Sparano", verdi.Installment.PropertyName);
        Assert.Equal(new DateOnly(2026, 10, 3), verdi.Date);
        Assert.Equal("EUR", verdi.Installment.Currency);
        Assert.Contains(RentBankReason.Month, verdi.Reasons);
        Assert.Single(verdi.Alternatives); // the same amount in November
        var rossi = preview.Suggestions.Single(s => s.Amount == 750m);
        Assert.Equal(f.RossiOct, rossi.Installment.Id);
        Assert.Equal(RentBankConfidence.High, rossi.Confidence);
        var unmatched = Assert.Single(preview.Unmatched);
        Assert.Equal(2100m, unmatched.Amount);

        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
        Assert.All(await f.Db.RentLedgerEntries.ToListAsync(), e => Assert.Equal(RentLedgerStatus.Scheduled, e.Status));
    }

    [Fact]
    public async Task Preview_TwiceOnTheSameFile_GivesTheSameAnswer()
    {
        var f = await CreateAsync();

        var first = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var second = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Suggestions.Select(s => s.Fingerprint), second.Suggestions.Select(s => s.Fingerprint));
    }

    [Fact]
    public async Task Preview_InAScopeLimitedToOneProperty_NeverProposesTheInstallmentsOfTheOther()
    {
        var f = await CreateAsync();

        var preview = await f.Service.PreviewAsync(f.OwnerOfB, Utf8(FirstFile), null);

        var only = Assert.Single(preview.Suggestions);
        Assert.Equal(f.RossiOct, only.Installment.Id);
        Assert.Equal(f.PropertyB, only.Installment.PropertyId);
        Assert.DoesNotContain(preview.Suggestions.SelectMany(s => s.Alternatives.Append(s.Installment)), i => i.LeaseId == f.VerdiLease);
        // The credit of the other property's tenant is just a credit with nothing to settle.
        Assert.Equal(2, preview.Unmatched.Count);
    }

    [Fact]
    public async Task Preview_AnotherOrgsScope_SeesNoInstallmentAtAll()
    {
        var f = await CreateAsync();

        var preview = await f.Service.PreviewAsync(new HostScope(Guid.NewGuid()), Utf8(FirstFile), null);

        Assert.Empty(preview.Suggestions);
        Assert.Equal(3, preview.Unmatched.Count);
    }

    [Fact]
    public async Task Preview_AnInstallmentThatIsPaidOrCancelledOrInFlight_IsNotAnOption()
    {
        var f = await CreateAsync();
        var october = await f.Db.RentLedgerEntries.SingleAsync(e => e.Id == f.VerdiOct);
        var november = await f.Db.RentLedgerEntries.SingleAsync(e => e.Id == f.VerdiNov);
        october.Status = RentLedgerStatus.Processing;
        november.Status = RentLedgerStatus.Cancelled;
        await f.Db.SaveChangesAsync();

        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);

        Assert.DoesNotContain(preview.Suggestions, s => s.Amount == 900m);
    }

    [Fact]
    public async Task Preview_AFailedInstallment_IsAnOptionLikeAScheduledOne()
    {
        var f = await CreateAsync();
        (await f.Db.RentLedgerEntries.SingleAsync(e => e.Id == f.VerdiOct)).Status = RentLedgerStatus.Failed;
        await f.Db.SaveChangesAsync();

        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);

        Assert.Equal(f.VerdiOct, preview.Suggestions.Single(s => s.Amount == 900m).Installment.Id);
    }

    [Fact]
    public async Task Preview_ATenantAnonymized_IsNeverNamed_AndNoLongerRecognizedByName()
    {
        var f = await CreateAsync();
        var party = await f.Db.Parties.SingleAsync(p => p.LeaseContractId == f.VerdiLease);
        LeasePartyPrivacyService.AnonymizeParty(party, Start.UtcDateTime);
        await f.Db.SaveChangesAsync();

        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);

        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        Assert.Null(verdi.Installment.TenantName);
        Assert.NotEqual(RentBankConfidence.High, verdi.Confidence);
    }

    [Fact]
    public async Task Preview_ARefusedFile_IsTheRefusalOfTheParser()
    {
        var f = await CreateAsync();

        var ex = await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(() => f.Service.PreviewAsync(f.OrgWide, Utf8("%PDF-1.7 no"), null));

        Assert.Equal(BankStatementErrorCodes.FormatUnsupported, ex.Code);
    }

    // ---------------------------------------------------------------- confirm

    [Fact]
    public async Task Confirm_DeclaresTheInstallmentPaidThroughTheOfflinePath_WithTheValueDateAndTheNote()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);

        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(1, result.Requested);
        Assert.Equal(new RentBankConfirmed(verdi.Fingerprint, f.VerdiOct), Assert.Single(result.Confirmed));
        Assert.Empty(result.Skipped);

        var entry = await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct);
        Assert.Equal(RentLedgerStatus.Paid, entry.Status);
        Assert.Equal(RentPaymentChannel.Offline, entry.PaidVia);
        Assert.Equal(new DateOnly(2026, 10, 3), entry.PaidOn);
        Assert.Equal("Da estratto conto", entry.OfflinePaymentNote);
        Assert.Equal(OwnerA, entry.MarkedPaidByUserId);

        var row = await f.Db.RentBankMovements.AsNoTracking().SingleAsync();
        Assert.Equal(verdi.Fingerprint, row.Fingerprint);
        Assert.Equal(f.OrgId, row.OrgId);
        Assert.Equal(f.VerdiLease, row.LeaseContractId);
        Assert.Equal(f.VerdiOct, row.RentLedgerEntryId);
        Assert.Equal(900m, row.Amount);
        Assert.Equal(new DateOnly(2026, 10, 3), row.ValueDate);
        Assert.Equal(OwnerA, row.ConfirmedByUserId);
        // Only hashes of the payer: nothing that reads as a name, an IBAN or a reason of payment.
        Assert.Matches("^[0-9a-f]{64}$", row.PayerIbanHash!);
        Assert.Null(row.PayerNameHash);
        Assert.DoesNotContain("VERDI", string.Join('|', row.Fingerprint, row.PayerIbanHash, row.ConfirmedByUserId), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_TheSameFileTwice_ChangesNothingTheSecondTime()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var selections = preview.Suggestions
            .Select(s => new RentBankSelection(s.Fingerprint, s.Installment.Id))
            .ToList();

        var first = await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, selections, OwnerA, Allowed);
        var rowsAfterFirst = await f.Db.RentBankMovements.AsNoTracking().OrderBy(m => m.Fingerprint).Select(m => m.Id).ToListAsync();
        var second = await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, selections, OwnerA, Allowed);

        Assert.Equal(2, first.Confirmed.Count);
        Assert.Empty(second.Confirmed);
        Assert.All(second.Skipped, s => Assert.Equal(BankStatementErrorCodes.MovementAlreadyImported, s.Code));
        Assert.Equal(rowsAfterFirst, await f.Db.RentBankMovements.AsNoTracking().OrderBy(m => m.Fingerprint).Select(m => m.Id).ToListAsync());

        // And the preview of the same file no longer offers what was imported.
        var again = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        Assert.Equal(2, again.Summary.AlreadyImported);
        Assert.Empty(again.Suggestions);
    }

    [Fact]
    public async Task Confirm_TwoFilesThatOverlap_ImportTheCommonMovementOnce()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        // The next export starts a few days earlier: it holds the same Verdi credit again, and a new one.
        const string next =
            "Data contabile;Data valuta;Descrizione;Importo\r\n"
            + "03/10/2026;03/10/2026;BONIFICO DA VERDI GIULIA CANONE OTTOBRE 2026 IBAN DE89 3704 0044 0532 0130 00;900,00\r\n"
            + "04/11/2026;04/11/2026;BONIFICO DA VERDI GIULIA CANONE NOVEMBRE 2026;900,00\r\n";
        var overlapping = await f.Service.PreviewAsync(f.OrgWide, Utf8(next), null);

        Assert.Equal(1, overlapping.Summary.AlreadyImported);
        var suggestion = Assert.Single(overlapping.Suggestions);
        Assert.Equal(new DateOnly(2026, 11, 4), suggestion.Date);
        Assert.Equal(f.VerdiNov, suggestion.Installment.Id);
    }

    [Fact]
    public async Task Confirm_APaymentDatedAfterToday_IsDeclaredPaidToday()
    {
        var f = await CreateAsync();
        const string file = "Data;Descrizione;Importo\n12/10/2026;Bonifico Verdi;900,00\n";
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(file), null);

        await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(file), null, [new RentBankSelection(preview.Suggestions[0].Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(new DateOnly(2026, 10, 9), (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).PaidOn);
    }

    [Fact]
    public async Task Confirm_TheAmountOfTheCreditMustBeTheInstallments()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var rossiCredit = preview.Suggestions.Single(s => s.Amount == 750m);

        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(rossiCredit.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(BankStatementErrorCodes.AmountMismatch, Assert.Single(result.Skipped).Code);
        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ACreditThatIsNotInTheFile_IsSkipped()
    {
        var f = await CreateAsync();

        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(new string('a', 64), f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(BankStatementErrorCodes.MovementNotFound, Assert.Single(result.Skipped).Code);
    }

    [Fact]
    public async Task Confirm_AnInstallmentOfAPropertyOutsideTheScope_IsNotFound_AndStaysUnpaid()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);

        var result = await f.Service.ConfirmAsync(
            f.OwnerOfB, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerB, Allowed);

        Assert.Equal(RentBillingErrorCodes.InstallmentNotFound, Assert.Single(result.Skipped).Code);
        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
    }

    [Fact]
    public async Task Confirm_AnInstallmentTheCallerMayNotChange_IsNotFound_AndStaysUnpaid()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        var rossi = preview.Suggestions.Single(s => s.Amount == 750m);

        var result = await f.Service.ConfirmAsync(
            f.OrgWide,
            Utf8(FirstFile),
            null,
            [new RentBankSelection(verdi.Fingerprint, f.VerdiOct), new RentBankSelection(rossi.Fingerprint, f.RossiOct)],
            OwnerA,
            OnlyFor(f.PropertyB));

        Assert.Equal(new RentBankConfirmed(rossi.Fingerprint, f.RossiOct), Assert.Single(result.Confirmed));
        Assert.Equal(RentBillingErrorCodes.InstallmentNotFound, Assert.Single(result.Skipped).Code);
        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
    }

    [Fact]
    public async Task Confirm_AnInstallmentPaidMeanwhile_IsSkippedWithTheRulesOwnCode_AndTheCreditStaysFree()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        await f.Billing.MarkPaidOfflineAsync(f.VerdiLease, f.VerdiOct, new MarkRentPaidOfflineRequest(new DateOnly(2026, 10, 2), "a mano"), OwnerA);

        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(RentBillingErrorCodes.InstallmentNotPayable, Assert.Single(result.Skipped).Code);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
        // The hand-made payment is what the ledger keeps.
        Assert.Equal("a mano", (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).OfflinePaymentNote);
    }

    [Fact]
    public async Task Confirm_AnInstallmentBeingPaidOnline_IsSkippedAsInFlight()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        (await f.Db.RentLedgerEntries.SingleAsync(e => e.Id == f.VerdiOct)).Status = RentLedgerStatus.Processing;
        await f.Db.SaveChangesAsync();

        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(RentBillingErrorCodes.InstallmentInFlight, Assert.Single(result.Skipped).Code);
    }

    [Fact]
    public async Task Confirm_OneRefusalDoesNotStopTheOthers()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        var rossi = preview.Suggestions.Single(s => s.Amount == 750m);

        var result = await f.Service.ConfirmAsync(
            f.OrgWide,
            Utf8(FirstFile),
            null,
            [new RentBankSelection(verdi.Fingerprint, f.RossiOct), new RentBankSelection(rossi.Fingerprint, f.RossiOct)],
            OwnerA,
            Allowed);

        Assert.Equal(BankStatementErrorCodes.AmountMismatch, Assert.Single(result.Skipped).Code);
        Assert.Equal(f.RossiOct, Assert.Single(result.Confirmed).InstallmentId);
        Assert.Equal(2, result.Requested);
    }

    // ---------------------------------------------------------------- one credit pays one installment

    [Fact]
    public async Task Confirm_TwoRequestsRacingForTheSameCredit_OnlyTheFirstPays()
    {
        var interceptor = new FailingSaveInterceptor();
        var f = await CreateAsync(interceptor);
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);

        // The other request saved its claim on the credit between the moment this one looked and the moment it saved: the unique index says so.
        interceptor.Failure = new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: "UIX_RentBankMovements_OrgId_Fingerprint"));
        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiNov)], OwnerA, Allowed);
        interceptor.Failure = null;

        // The loser pays nothing and is not told it did: the installment it chose is free for what it really is.
        Assert.Empty(result.Confirmed);
        Assert.Equal(BankStatementErrorCodes.MovementAlreadyImported, Assert.Single(result.Skipped).Code);
        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiNov)).Status);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
        Assert.DoesNotContain(f.Db.ChangeTracker.Entries<RentBankMovement>(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task Confirm_TheLedgerFailsUnexpectedly_TheClaimIsReleased_AndTheFailureIsNotHidden()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        var billing = new Mock<IRentBillingService>();
        billing
            .Setup(b => b.MarkPaidOfflineAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<MarkRentPaidOfflineRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the payment service is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ServiceOver(billing.Object).ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed));

        // Nothing was paid, so the credit is free again: the next try is not told it was imported.
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
        var retry = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);
        Assert.Equal(f.VerdiOct, Assert.Single(retry.Confirmed).InstallmentId);
    }

    [Fact]
    public async Task Confirm_AFailureThatLeftAHalfDoneChangeStaged_DoesNotSaveItWithTheRelease()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        var billing = new Mock<IRentBillingService>();
        billing
            .Setup(b => b.MarkPaidOfflineAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<MarkRentPaidOfflineRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(StageThePaymentAndFail);

        // The ledger staged the payment in the context, and then its save failed.
        async Task<RentInstallmentView> StageThePaymentAndFail()
        {
            (await f.Db.RentLedgerEntries.SingleAsync(e => e.Id == f.VerdiOct)).Status = RentLedgerStatus.Paid;
            throw new InvalidOperationException("the save failed");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ServiceOver(billing.Object).ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed));

        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
    }

    [Fact]
    public async Task Confirm_TheRequestIsCanceledAfterThePaymentWasSaved_TheCreditStaysClaimed()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        var billing = new Mock<IRentBillingService>();
        billing
            .Setup(b => b.MarkPaidOfflineAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<MarkRentPaidOfflineRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, MarkRentPaidOfflineRequest, string, CancellationToken>(PayThenBeCanceled);

        // The real ledger saves the payment, and the caller goes away right after.
        async Task<RentInstallmentView> PayThenBeCanceled(Guid lease, Guid installment, MarkRentPaidOfflineRequest request, string user, CancellationToken token)
        {
            await f.Billing.MarkPaidOfflineAsync(lease, installment, request, user, token);
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => f.ServiceOver(billing.Object).ConfirmAsync(
            f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed));

        // The rent is paid and the credit says which rent it paid: the next preview does not offer it for another one.
        Assert.Equal(RentLedgerStatus.Paid, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
        Assert.Equal(f.VerdiOct, (await f.Db.RentBankMovements.AsNoTracking().SingleAsync()).RentLedgerEntryId);
        var again = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        Assert.DoesNotContain(again.Suggestions, s => s.Amount == 900m);
    }

    [Fact]
    public async Task Confirm_ACreditInAnotherCurrency_NeverSettlesARent_EvenForTheSameAmount()
    {
        var f = await CreateAsync();
        const string pounds = ":20:X\n:25:ACC\n:60F:C261001GBP1000,00\n:61:2610031003C900,00NTRFNONREF\n:86:Canone Verdi\n";
        var movement = Assert.Single(BankStatementParser.Parse(Utf8(pounds)).Credits);
        Assert.Equal("GBP", movement.Currency);
        var fingerprint = BankMovementFingerprint.ForAll(f.OrgId, [movement])[0];

        // The preview leaves it with no installment; a choice made by hand for it is refused all the same.
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(pounds), null);
        Assert.Empty(preview.Suggestions);
        var result = await f.Service.ConfirmAsync(
            f.OrgWide, Utf8(pounds), null, [new RentBankSelection(fingerprint, f.VerdiOct)], OwnerA, Allowed);

        Assert.Equal(BankStatementErrorCodes.AmountMismatch, Assert.Single(result.Skipped).Code);
        Assert.Equal(RentLedgerStatus.Scheduled, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == f.VerdiOct)).Status);
        Assert.Empty(await f.Db.RentBankMovements.ToListAsync());
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("anonymized")]
    public async Task Confirm_OnALeaseWhoseTenantsAskedForErasureOrWereAnonymized_KeepsNoPayerHash(string state)
    {
        var f = await CreateAsync();
        var lease = await f.Db.LeaseContracts.SingleAsync(l => l.Id == f.VerdiLease);
        if (state == "requested")
            lease.ErasureRequested = true;
        else
            lease.PartiesAnonymizedAt = Start.UtcDateTime;
        await f.Db.SaveChangesAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);

        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        // A late credit on an anonymized lease must not bring back what the anonymization took away.
        var row = await f.Db.RentBankMovements.AsNoTracking().SingleAsync();
        Assert.Null(row.PayerIbanHash);
        Assert.Null(row.PayerNameHash);
        Assert.Equal(f.VerdiOct, row.RentLedgerEntryId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BankStatementLimits.MaxSelections + 1)]
    public async Task Confirm_NoChoiceOrTooMany_IsRefused(int count)
    {
        var f = await CreateAsync();
        var selections = Enumerable.Range(0, count).Select(i => new RentBankSelection(new string('a', 64), f.VerdiOct)).ToList();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, selections, OwnerA, Allowed));

        Assert.Equal(BankStatementErrorCodes.SelectionsInvalid, ex.Code);
    }

    // ---------------------------------------------------------------- who paid

    [Fact]
    public async Task TheNextStatement_RecognizesThePayerOfAnEarlierConfirmation_WithNoNameInTheReason()
    {
        var f = await CreateAsync();
        var first = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = first.Suggestions.Single(s => s.Amount == 900m);
        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        // Next month the bank writes only «CANONE» and the payer's IBAN: no surname, no month. The IBAN paid this lease before.
        f.Clock.Advance(TimeSpan.FromDays(27)); // it is the 5th of November: the credit of the 4th is no longer in the future
        var next = $"Data;Descrizione;IBAN ordinante;Importo\r\n04/11/2026;CANONE;{PayerIban};900,00\r\n";
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(next), null);

        var suggestion = Assert.Single(preview.Suggestions);
        Assert.Equal(f.VerdiNov, suggestion.Installment.Id);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Contains(RentBankReason.PayerIban, suggestion.Reasons);
    }

    [Fact]
    public async Task APayerNotSeenOnThisLease_IsNotRecognized()
    {
        var f = await CreateAsync();
        var first = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = first.Suggestions.Single(s => s.Amount == 900m);
        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        // The same IBAN pays 750: the lease it is known for has nothing of that amount, and the other lease has not seen it.
        var next = $"Data;Descrizione;IBAN ordinante;Importo\r\n04/10/2026;CANONE;{PayerIban};750,00\r\n";
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(next), null);

        var suggestion = Assert.Single(preview.Suggestions);
        Assert.Equal(f.RossiOct, suggestion.Installment.Id);
        Assert.DoesNotContain(RentBankReason.PayerIban, suggestion.Reasons);
        Assert.NotEqual(RentBankConfidence.High, suggestion.Confidence);
    }

    [Fact]
    public async Task WithoutTheSecretOfTheSignedLinks_NothingIsKeptOfWhoPaid_AndThePayerIsNotRecognized()
    {
        var f = await CreateAsync(withSecret: false);
        var first = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = first.Suggestions.Single(s => s.Amount == 900m);
        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);

        // Without the key of the hashes there are no hashes: a payer is learned from nothing, and the rest of the matching goes on.
        var row = await f.Db.RentBankMovements.AsNoTracking().SingleAsync();
        Assert.Null(row.PayerIbanHash);
        Assert.Null(row.PayerNameHash);
        Assert.Equal(verdi.Fingerprint, row.Fingerprint);

        f.Clock.Advance(TimeSpan.FromDays(27));
        var next = $"Data;Descrizione;IBAN ordinante;Importo\r\n04/11/2026;CANONE;{PayerIban};900,00\r\n";
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(next), null);
        var suggestion = Assert.Single(preview.Suggestions);
        Assert.Equal(f.VerdiNov, suggestion.Installment.Id);
        Assert.DoesNotContain(RentBankReason.PayerIban, suggestion.Reasons);
        Assert.Equal(RentBankConfidence.Low, suggestion.Confidence);
    }

    [Fact]
    public async Task WhenTheTenantsAreAnonymized_ThePayerHashesGo_AndTheMovementLineStays()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var leaseId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            var org = new OrgEntity { Id = orgId, Name = "Org", Slug = $"org-{Guid.NewGuid():N}", ContactEmail = "org@example.com" };
            var property = new Property { OrgId = org.Id, OwnerId = OwnerA, Name = "Casa", Address = "Via Roma 1", City = "Bari", PostalCode = "70121", Bedrooms = 1, Bathrooms = 1 };
            var lease = new LeaseContract
            {
                Id = leaseId,
                OrgId = org.Id,
                PropertyId = property.Id,
                Status = LeaseStatus.Registered,
                StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate = new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                MonthlyRent = 900m,
                Parties = [new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = "g@example.com" }],
            };
            var schedule = new RentSchedule { OrgId = org.Id, LeaseContractId = leaseId, BillingDayOfMonth = 5, Amount = 900m };
            var entry = new RentLedgerEntry
            {
                Id = entryId,
                OrgId = org.Id,
                LeaseContractId = leaseId,
                RentScheduleId = schedule.Id,
                PeriodStart = new DateOnly(2023, 10, 1),
                PeriodEnd = new DateOnly(2023, 10, 31),
                DueDate = new DateOnly(2023, 10, 5),
                AmountDue = 900m,
                Status = RentLedgerStatus.Paid,
            };
            seed.AddRange(org, property, lease, schedule, entry);
            seed.RentBankMovements.Add(new RentBankMovement
            {
                OrgId = org.Id,
                LeaseContractId = leaseId,
                RentLedgerEntryId = entryId,
                Fingerprint = new string('a', 64),
                PayerIbanHash = new string('b', 64),
                PayerNameHash = new string('c', 64),
                ValueDate = new DateOnly(2023, 10, 3),
                Amount = 900m,
                ConfirmedByUserId = OwnerA,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var service = new LeasePartyPrivacyService(db, Options.Create(new GdprOptions()), new FakeTimeProvider(Start), NullLogger<LeasePartyPrivacyService>.Instance);

            var result = await service.RequestErasureAsync(leaseId);

            Assert.Equal(LeaseErasureStatus.Anonymized, result.Status);
        }

        await using var read = new AppDbContext(options);
        var row = await read.RentBankMovements.AsNoTracking().SingleAsync();
        Assert.Null(row.PayerIbanHash);
        Assert.Null(row.PayerNameHash);
        Assert.Equal(new string('a', 64), row.Fingerprint);
        Assert.Equal(entryId, row.RentLedgerEntryId);
    }

    // ---------------------------------------------------------------- log

    [Fact]
    public async Task TheLog_NamesNoOneAndQuotesNoReasonOfPayment()
    {
        var f = await CreateAsync();
        var preview = await f.Service.PreviewAsync(f.OrgWide, Utf8(FirstFile), null);
        var verdi = preview.Suggestions.Single(s => s.Amount == 900m);
        await f.Service.ConfirmAsync(f.OrgWide, Utf8(FirstFile), null, [new RentBankSelection(verdi.Fingerprint, f.VerdiOct)], OwnerA, Allowed);
        await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(() => f.Service.PreviewAsync(f.OrgWide, Utf8("%PDF-1.7 Verdi"), null));

        var log = string.Join('\n', f.Log.Messages);
        Assert.NotEmpty(log);
        foreach (var secret in new[] { "Verdi", "VERDI", "Giulia", "GIULIA", "Rossi", "ROSSI", "DE89", "BONIFICO", "CANONE", "estratto", "STIPENDIO", verdi.Fingerprint })
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    private sealed class ListLogger : ILogger<RentBankImportService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Messages.Add(exception.ToString());
        }
    }
}
