using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>LR-12, decision LR-D29: the figures of the area are read by the backend and handed to the screens from one place.</summary>
public class LongRentRulesTests
{
    [Fact]
    public void Current_IsReadFromTheConstantsTheBackendChecksAgainst_NotCopiedFromThem()
    {
        var rules = LongRentRules.Current;

        Assert.Equal(48, rules.TermMonths["Libero"]);
        Assert.Equal(LeaseContractTerms.LiberoMinimumMonths, rules.TermMonths["Libero"]);
        Assert.Equal(36, rules.TermMonths["Concordato"]);
        Assert.Equal(LeaseContractTerms.ConcordatoMinimumMonths, rules.TermMonths["Concordato"]);
        Assert.Equal(48, rules.RenewalMonths["Libero"]);
        Assert.Equal(24, rules.RenewalMonths["Concordato"]);
        Assert.Equal(6, rules.NoticeMonths);
        Assert.Equal(LongRentDeadlineRules.NoticeMonthsBeforeEnd, rules.NoticeMonths);
        Assert.Equal(30, rules.RegistrationDays);
        Assert.Equal(RliRegistrationDeadline.DaysFromStipulaOrStart, rules.RegistrationDays);
        Assert.Equal(3, rules.DepositMaxRents);
        Assert.Equal(["Libero", "Concordato", "Transitorio"], rules.AvailableTypes);
        Assert.Equal(30, rules.TransitoryDocumentationAfterDays);
        Assert.Equal(LeaseContractTerms.TransitorioDocumentationAfterDays, rules.TransitoryDocumentationAfterDays);
        Assert.Equal(10_000, rules.TransitoryBandFromInhabitants);
    }

    [Fact]
    public void TheJson_HasTheShapeTheScreensRead_TheTypeKeysKeepTheirCase()
    {
        var json = JsonSerializer.Serialize(LongRentRules.Current, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(48, root.GetProperty("termMonths").GetProperty("Libero").GetInt32());
        Assert.Equal(36, root.GetProperty("termMonths").GetProperty("Concordato").GetInt32());
        Assert.Equal(48, root.GetProperty("renewalMonths").GetProperty("Libero").GetInt32());
        Assert.Equal(24, root.GetProperty("renewalMonths").GetProperty("Concordato").GetInt32());
        Assert.Equal(6, root.GetProperty("noticeMonths").GetInt32());
        Assert.Equal(30, root.GetProperty("registrationDays").GetInt32());
        Assert.Equal(3, root.GetProperty("depositMaxRents").GetInt32());
        Assert.Equal(["Libero", "Concordato", "Transitorio"], root.GetProperty("availableTypes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, root.GetProperty("transitoryTermMonths").GetProperty("min").GetInt32());
        Assert.Equal(18, root.GetProperty("transitoryTermMonths").GetProperty("max").GetInt32());
        Assert.Equal(30, root.GetProperty("transitoryDocumentationAfterDays").GetInt32());
    }

    [Theory]
    [InlineData(LeaseContractType.Libero)]
    [InlineData(LeaseContractType.Concordato)]
    [InlineData(LeaseContractType.Transitorio)] // TR: made in CasaZen with the Allegato B, as well as brought in already signed (LR-13)
    public void TheLiberoTheConcordatoAndTheTransitory_CanBeCreated(LeaseContractType type)
    {
        Assert.True(LongRentRules.IsAvailable(type));
        Assert.True(LongRentRules.IsAvailable(type, LeaseOrigin.Created));
        Assert.True(LongRentRules.IsAvailable(type, LeaseOrigin.Imported));
    }

    [Fact]
    public void OnlyTheLiberoAndTheConcordato_HaveAFirstPeriodOfTheirType_TheTransitoryStatesItsOwn()
    {
        Assert.Equal(48, LongRentRules.FirstTermMonths(LeaseContractType.Libero));
        Assert.Equal(36, LongRentRules.FirstTermMonths(LeaseContractType.Concordato));
        Assert.Null(LongRentRules.FirstTermMonths(LeaseContractType.Transitorio));
    }

    [Theory]
    [InlineData(LeaseContractType.Libero, 48)]
    [InlineData(LeaseContractType.Concordato, 24)]
    public void TheRenewalOfTheContractsThatRenew_IsTheInterviewsAnswer(LeaseContractType type, int months)
    {
        Assert.Equal(months, LongRentRules.RenewalMonths(type));
    }

    [Fact]
    public void ATransitoryLease_DoesNotRenew()
    {
        Assert.Null(LongRentRules.RenewalMonths(LeaseContractType.Transitorio));
    }

    [Theory]
    [InlineData(2026, 11, 1, 48, 2030, 10, 31)]
    [InlineData(2026, 11, 15, 36, 2029, 11, 14)]
    [InlineData(2024, 2, 29, 48, 2028, 2, 28)] // a leap day: the 29th of February 2028 exists, the first period ends the day before
    [InlineData(2024, 2, 29, 36, 2027, 2, 27)] // 2027 has no 29th: the period ends on 27 February
    [InlineData(2026, 1, 31, 48, 2030, 1, 30)]
    public void FirstTermEnd_IsTheDayBeforeTheSameDayOfTheMonthAfterTheMonths(
        int year, int month, int day, int months, int endYear, int endMonth, int endDay)
    {
        var end = LongRentRules.FirstTermEnd(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), months);

        Assert.Equal(new DateTime(endYear, endMonth, endDay), end.Date);
    }
}
