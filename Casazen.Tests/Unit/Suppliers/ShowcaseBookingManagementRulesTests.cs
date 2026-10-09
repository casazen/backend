using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-11: the rules of the customer's own area of a booking made from a supplier's showcase, as pure functions — the credentials the
/// customer types, the table of states and actions, the 24 hours of free cancellation counted on the clock of Rome (the days the
/// clocks change included), the reason of a cancellation — and the codes and the texts (Italian and English) of what it refuses.
/// </summary>
public class ShowcaseBookingManagementRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc); // Saturday 10 October, 09:00 in Rome
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc); // Thursday 8 October, 12:00 in Rome

    // ─── The credentials ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("vetrina-test", "7K2XM-9QD4T", "mario@example.com")]
    [InlineData("  Vetrina-TEST ", "7k2xm 9qd4t", "  Mario@Example.COM  ")]
    [InlineData("vetrina-test", "7K2XM9QD4T", "mario@example.com")]
    [InlineData("vetrina-test", "7k2xm–9qd4t", "mario@example.com")] // an en dash, as a phone pastes it
    public void TryNormalize_TheFormsPeopleType_AreTheSameBooking(string slug, string code, string email)
    {
        var valid = ShowcaseBookingManagementRules.TryNormalize(new ShowcaseBookingCredentials(slug, code, email), out var key);

        Assert.True(valid);
        Assert.Equal(new ShowcaseBookingAccessKey("vetrina-test", "7K2XM9QD4T", "mario@example.com"), key);
    }

    [Fact]
    public void TryNormalize_ACodeWithTheLettersPeopleConfuse_IsReadTheWayTheyMeantIt()
    {
        // O is read as 0, I and L as 1 (BookingCodes): a person reading a code to another does not tell them apart.
        var valid = ShowcaseBookingManagementRules.TryNormalize(
            new ShowcaseBookingCredentials("vetrina-test", "OIL00-OIL00", "mario@example.com"), out var key);

        Assert.True(valid);
        Assert.Equal("0110001100", key!.Code);
    }

    public static TheoryData<string?, string?, string?> NotACandidate => new()
    {
        { null, "7K2XM-9QD4T", "mario@example.com" },
        { "", "7K2XM-9QD4T", "mario@example.com" },
        { "   ", "7K2XM-9QD4T", "mario@example.com" },
        { "vetrina\0test", "7K2XM-9QD4T", "mario@example.com" },
        { "vetrina\ttest", "7K2XM-9QD4T", "mario@example.com" },
        { new string('a', 101), "7K2XM-9QD4T", "mario@example.com" },
        { "vetrina-test", null, "mario@example.com" },
        { "vetrina-test", "", "mario@example.com" },
        { "vetrina-test", "7K2XM", "mario@example.com" },
        { "vetrina-test", "7K2XM-9QD4T-9", "mario@example.com" },
        { "vetrina-test", "7K2XM-9QD4U", "mario@example.com" }, // U is not in the alphabet
        { "vetrina-test", "7K2XM-9QD4%", "mario@example.com" },
        { "vetrina-test", new string('7', 65), "mario@example.com" },
        { "vetrina-test", "7K2XM-9QD4T", null },
        { "vetrina-test", "7K2XM-9QD4T", "" },
        { "vetrina-test", "7K2XM-9QD4T", "   " },
        { "vetrina-test", "7K2XM-9QD4T", "mario@\0example.com" },
        { "vetrina-test", "7K2XM-9QD4T", "mario@example.com\n" + "x" },
    };

    [Theory]
    [MemberData(nameof(NotACandidate))]
    public void TryNormalize_SomethingThatCannotBeABooking_IsNotValid(string? slug, string? code, string? email)
    {
        var valid = ShowcaseBookingManagementRules.TryNormalize(new ShowcaseBookingCredentials(slug, code, email), out var key);

        Assert.False(valid);
        Assert.Null(key);
    }

    [Fact]
    public void TryNormalize_NoCredentialsAtAll_IsNotValid()
    {
        Assert.False(ShowcaseBookingManagementRules.TryNormalize(null, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void TryNormalize_AnAddressLongerThanAnAddressCanBe_IsNotValid()
    {
        var tooLong = new string('a', ShowcaseBookingLimits.EmailMaxLength) + "@example.com";

        Assert.False(ShowcaseBookingManagementRules.TryNormalize(
            new ShowcaseBookingCredentials("vetrina-test", "7K2XM-9QD4T", tooLong), out _));
    }

    // ─── The table: states and actions ───────────────────────────────────────────────────────────────────────────────

    public static TheoryData<ServiceRequestStatus, bool, bool, bool> CancelByStatus => new()
    {
        // Status, while the time is still ahead, at the time itself, after it
        { ServiceRequestStatus.Richiesto, true, true, true },
        { ServiceRequestStatus.PresoInCarico, true, false, false },
        { ServiceRequestStatus.InCorso, false, false, false },
        { ServiceRequestStatus.Completato, false, false, false },
        { ServiceRequestStatus.Pagato, false, false, false },
        { ServiceRequestStatus.Rifiutato, false, false, false },
        { ServiceRequestStatus.Annullato, false, false, false },
    };

    [Theory]
    [MemberData(nameof(CancelByStatus))]
    public void CanCancel_ANewRequestAlways_ATakenOneUntilItsTime_NeverTheOthers(
        ServiceRequestStatus status,
        bool before,
        bool atTheStart,
        bool after)
    {
        Assert.Equal(before, ShowcaseBookingManagementRules.CanCancel(status, Start, Start.AddMinutes(-1)));
        Assert.Equal(atTheStart, ShowcaseBookingManagementRules.CanCancel(status, Start, Start));
        Assert.Equal(after, ShowcaseBookingManagementRules.CanCancel(status, Start, Start.AddHours(3)));
    }

    [Fact]
    public void CanCancel_EveryStatusIsInTheTable()
    {
        var inTable = CancelByStatus.Select(row => (ServiceRequestStatus)row[0]).Order().ToArray();

        Assert.Equal(Enum.GetValues<ServiceRequestStatus>().Order().ToArray(), inTable);
    }

    [Fact]
    public void CanCancel_ATakenRequestWithoutATime_CanBeCancelled()
    {
        // A showcase request always has a time; one without cannot have started, so there is nothing to protect.
        Assert.True(ShowcaseBookingManagementRules.CanCancel(ServiceRequestStatus.PresoInCarico, null, Now));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true, true, true, true)]
    [InlineData(ServiceRequestStatus.Richiesto, false, true, true, false)] // no time to move
    [InlineData(ServiceRequestStatus.Richiesto, true, false, true, false)] // the supplier is suspended
    [InlineData(ServiceRequestStatus.Richiesto, true, true, false, false)] // the service is paused or gone
    [InlineData(ServiceRequestStatus.PresoInCarico, true, true, true, false)]
    [InlineData(ServiceRequestStatus.InCorso, true, true, true, false)]
    [InlineData(ServiceRequestStatus.Completato, true, true, true, false)]
    [InlineData(ServiceRequestStatus.Pagato, true, true, true, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, true, true, true, false)]
    [InlineData(ServiceRequestStatus.Annullato, true, true, true, false)]
    public void CanReschedule_OnlyANewRequestOfAnActiveSupplierForAPublishedService(
        ServiceRequestStatus status,
        bool hasTime,
        bool supplierActive,
        bool serviceBookable,
        bool expected)
    {
        Assert.Equal(expected, ShowcaseBookingManagementRules.CanReschedule(status, hasTime, supplierActive, serviceBookable));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true, 60, true, true)]
    [InlineData(ServiceRequestStatus.Richiesto, true, 0, true, false)] // the deadline is now
    [InlineData(ServiceRequestStatus.Richiesto, true, -1, true, false)] // the deadline has passed
    [InlineData(ServiceRequestStatus.Richiesto, true, null, true, false)] // no deadline recorded: nothing to answer before
    [InlineData(ServiceRequestStatus.Richiesto, false, 60, true, false)] // no proposal
    [InlineData(ServiceRequestStatus.Richiesto, true, 60, false, false)] // the supplier is suspended
    [InlineData(ServiceRequestStatus.PresoInCarico, true, 60, true, false)]
    [InlineData(ServiceRequestStatus.Annullato, true, 60, true, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, true, 60, true, false)]
    public void CanRespondToProposal_OnlyWhileTheProposalWaitsAndItsDeadlineHasNotPassed(
        ServiceRequestStatus status,
        bool proposalPending,
        int? minutesUntilDeadline,
        bool supplierActive,
        bool expected)
    {
        DateTime? answerBy = minutesUntilDeadline is { } minutes ? Now.AddMinutes(minutes) : null;

        Assert.Equal(expected, ShowcaseBookingManagementRules.CanRespondToProposal(status, proposalPending, answerBy, Now, supplierActive));
    }

    // ─── The 24 hours ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreeCancellationUntil_IsTheHoursBeforeTheStart_AndZeroHoursIsTheStartItself()
    {
        Assert.Equal(Start.AddHours(-24), ShowcaseBookingManagementRules.FreeCancellationUntil(Start, 24));
        Assert.Equal(Start.AddHours(-72), ShowcaseBookingManagementRules.FreeCancellationUntil(Start, 72));
        Assert.Equal(Start, ShowcaseBookingManagementRules.FreeCancellationUntil(Start, 0));
        Assert.Equal(Start, ShowcaseBookingManagementRules.FreeCancellationUntil(Start, -5)); // never later than the start
    }

    [Fact]
    public void IsFreeCancellation_UntilTheDeadlineIncluded_NotAMomentAfter()
    {
        var deadline = Start.AddHours(-24);

        Assert.True(ShowcaseBookingManagementRules.IsFreeCancellation(Start, deadline.AddMinutes(-1), 24));
        Assert.True(ShowcaseBookingManagementRules.IsFreeCancellation(Start, deadline, 24));
        Assert.False(ShowcaseBookingManagementRules.IsFreeCancellation(Start, deadline.AddTicks(1), 24));
        Assert.False(ShowcaseBookingManagementRules.IsFreeCancellation(Start, Start, 24));
    }

    [Fact]
    public void FreeCancellationUntil_WhenTheClocksGoBack_IsTwentyFourElapsedHours_NotTheSameClockTimeTheDayBefore()
    {
        // Sunday 25 October 2026: at 03:00 summer time (+02:00) the clocks go back to 02:00 (+01:00). 08:00 that day is +01:00.
        var start = RomeCalendar.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(8, 0));
        Assert.Equal(new DateTime(2026, 10, 25, 7, 0, 0, DateTimeKind.Utc), start);

        var until = ShowcaseBookingManagementRules.FreeCancellationUntil(start, 24);

        // 24 hours earlier is 07:00 UTC on Saturday: 09:00 on the clock of Rome, which was still on summer time then (25 hours of clock).
        Assert.Equal(new DateTime(2026, 10, 24, 7, 0, 0, DateTimeKind.Utc), until);
        var local = RomeCalendar.ToRome(until);
        Assert.Equal(TimeSpan.FromHours(2), local.Offset);
        Assert.Equal(new DateTime(2026, 10, 24, 9, 0, 0), local.DateTime);
        Assert.Equal(TimeSpan.FromHours(1), RomeCalendar.ToRome(start).Offset);
        Assert.Equal(TimeSpan.FromHours(24), start - until);
    }

    [Fact]
    public void FreeCancellationUntil_WhenTheClocksGoForward_IsTwentyFourElapsedHours_NotTheSameClockTimeTheDayBefore()
    {
        // Sunday 29 March 2026: at 02:00 (+01:00) the clocks go forward to 03:00 (+02:00). 09:00 that day is +02:00.
        var start = RomeCalendar.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(9, 0));
        Assert.Equal(new DateTime(2026, 3, 29, 7, 0, 0, DateTimeKind.Utc), start);

        var until = ShowcaseBookingManagementRules.FreeCancellationUntil(start, 24);

        // 24 hours earlier is 07:00 UTC on Saturday: 08:00 on the clock of Rome (+01:00), 23 hours of clock.
        Assert.Equal(new DateTime(2026, 3, 28, 7, 0, 0, DateTimeKind.Utc), until);
        var local = RomeCalendar.ToRome(until);
        Assert.Equal(TimeSpan.FromHours(1), local.Offset);
        Assert.Equal(new DateTime(2026, 3, 28, 8, 0, 0), local.DateTime);
    }

    [Fact]
    public void FreeCancellationUntil_AWorkOfTheHourThatHappensTwice_IsToldApartByTheOffsetOfRome()
    {
        // 02:30 on 25 October 2026 happens twice; RomeCalendar takes its first pass (+02:00), 00:30 UTC.
        var start = RomeCalendar.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30));
        var second = start.AddHours(1);

        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), start);
        Assert.Equal(TimeSpan.FromHours(2), RomeCalendar.ToRome(start).Offset);
        Assert.Equal(TimeSpan.FromHours(1), RomeCalendar.ToRome(second).Offset);

        // The two passes read 02:30 on the clock: only the offset tells them apart, and the page shows it.
        Assert.Equal(RomeCalendar.ToRome(start).DateTime, RomeCalendar.ToRome(second).DateTime);
        Assert.NotEqual(RomeCalendar.ToRome(start), RomeCalendar.ToRome(second));
        Assert.Equal(TimeSpan.FromHours(1), second - start);
    }

    // ─── The reason ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n \t ")]
    public void NormalizeReason_NothingWritten_IsNoReason(string? reason)
    {
        Assert.Null(ShowcaseBookingManagementRules.NormalizeReason(reason));
    }

    [Fact]
    public void NormalizeReason_IsTrimmed_AndKeepsTheLinesTheCustomerWrote()
    {
        Assert.Equal("Cambio casa\nTorno a Roma", ShowcaseBookingManagementRules.NormalizeReason("  Cambio casa\r\nTorno a Roma \n"));
        Assert.Equal("Non servono più", ShowcaseBookingManagementRules.NormalizeReason("Non servono più"));
    }

    [Fact]
    public void NormalizeReason_FiveHundredCharactersAreFine_FiveHundredAndOneAreNot()
    {
        var limit = ServiceRequestLimits.CancellationReasonMaxLength;

        Assert.Equal(limit, ShowcaseBookingManagementRules.NormalizeReason(new string('a', limit))!.Length);
        var refused = Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingManagementRules.NormalizeReason(new string('a', limit + 1)));
        Assert.Equal(ShowcaseBookingErrors.Invalid, refused.Code);
        Assert.Equal(new[] { ShowcaseBookingFields.Reason }, refused.Fields);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\u0007b")]
    [InlineData("a\u001bb")]
    public void NormalizeReason_ControlCharacters_AreRefusedNamingTheField(string reason)
    {
        var refused = Assert.Throws<ShowcaseBookingRuleException>(() => ShowcaseBookingManagementRules.NormalizeReason(reason));

        Assert.Equal(new[] { "reason" }, refused.Fields);
    }

    // ─── Codes and texts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Errors_AreOfTheRightKind_AndTheNotFoundIsOneAnswer()
    {
        var notFound = ShowcaseBookingManagementErrors.BookingNotFound();

        Assert.IsType<NotFoundException>(notFound);
        Assert.Equal("supplier_booking_not_found", notFound.Code);
        Assert.Equal("SupplierBookingNotFound", notFound.MessageKey);
        Assert.IsType<DomainRuleException>(ShowcaseBookingManagementErrors.CancelRefused());
        Assert.IsType<DomainRuleException>(ShowcaseBookingManagementErrors.RescheduleRefused());
        Assert.IsType<DomainRuleException>(ShowcaseBookingManagementErrors.ProposalMissing());
        Assert.IsType<DomainRuleException>(ShowcaseBookingManagementErrors.ProposalLapsed());
        // Two calls give the same code and key: nothing in the exception depends on why nothing matched.
        Assert.Equal(notFound.Code, ShowcaseBookingManagementErrors.BookingNotFound().Code);
        Assert.Equal(notFound.MessageKey, ShowcaseBookingManagementErrors.BookingNotFound().MessageKey);
    }

    [Fact]
    public void Codes_AreUniqueSnakeCase_AndHavePrefixOfTheBooking()
    {
        var codes = typeof(ShowcaseBookingManagementErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) } && !f.Name.EndsWith("MessageKey", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(5, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^supplier_booking(_[a-z]+)+$", code));
    }

    [Fact]
    public void MessageKeys_EveryKeyOfTheArea_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        var keys = ShowcaseBookingManagementErrors.MessageKeys.Append("SupplierBookingAccessRequired").Append("SupplierBookingStartRequired").ToList();
        Assert.Equal(7, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void MessageKeys_TheNotFoundDoesNotSayWhichOfTheThreeThingsWasWrong()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture)["SupplierBookingNotFound"];

        // It names the three things together ("this code and this address"), so it holds for the wrong one whichever it is.
        Assert.Contains("codice", italian);
        Assert.Contains("indirizzo email", italian);
        Assert.DoesNotContain("non esiste", italian);
        Assert.DoesNotContain("errat", italian);
    }

    [Fact]
    public void Reasons_TheCodesAreNotSentences_AndAreTheOnlyCodes()
    {
        Assert.True(ServiceRequestCancellationReasons.IsCode("NoResponse"));
        Assert.True(ServiceRequestCancellationReasons.IsCode("ProposalNotAnswered"));
        Assert.True(ServiceRequestCancellationReasons.IsCode("CancelledByCustomer"));
        Assert.False(ServiceRequestCancellationReasons.IsCode("Cambio casa"));
        Assert.False(ServiceRequestCancellationReasons.IsCode(""));
        Assert.False(ServiceRequestCancellationReasons.IsCode(null));
        Assert.False(ServiceRequestCancellationReasons.IsCode("noresponse"));
    }

    [Fact]
    public void Price_TheAmountToShowIsTheFinalThenTheQuoteThenTheEstimate()
    {
        var lines = Array.Empty<ShowcaseBookingPriceLine>();

        var estimate = new ShowcaseBookingPrice(6000, null, null, lines);
        var quote = new ShowcaseBookingPrice(6000, 7000, null, lines);
        var final = new ShowcaseBookingPrice(6000, 7000, 8000, lines);
        var none = new ShowcaseBookingPrice(null, null, null, lines);

        Assert.Equal((6000, "estimate"), (estimate.AmountCents, estimate.Basis));
        Assert.Equal((7000, "quote"), (quote.AmountCents, quote.Basis));
        Assert.Equal((8000, "final"), (final.AmountCents, final.Basis));
        Assert.Equal((null, null), (none.AmountCents, none.Basis));
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No SharedResources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }
}
