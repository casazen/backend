using System.Text.RegularExpressions;
using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T03 (NF-66, NF-67, AC-NF66-3): the one table of the situations. The 14 situations and the one that matches none, each once, with their stable codes, the sensitive
/// categories they carry by nature, the modes they allow, their default, the topic that answers them and the topics their answer needs.
/// </summary>
public partial class AssistantSituationCatalogTests
{
    private static AssistantSituationInfo Info(AssistantSituation situation) => AssistantSituationCatalog.Of(situation);

    [Fact]
    public void All_EveryMemberOfTheEnum_HasExactlyOneEntry_WithAStableUniqueSnakeCaseCode()
    {
        // Adding a situation without an entry fails here: a hole in the table would be a situation the gate does not know.
        Assert.Equal(
            Enum.GetValues<AssistantSituation>().Order(),
            AssistantSituationCatalog.All.Select(info => info.Situation).Order());
        Assert.Equal(15, AssistantSituationCatalog.All.Count);
        Assert.Equal(AssistantSituationCatalog.All.Count, AssistantSituationCatalog.All.Select(info => info.Code).Distinct().Count());
        Assert.All(AssistantSituationCatalog.All, info => Assert.Matches(SnakeCase(), info.Code));
    }

    [Fact]
    public void Codes_AreTheOnesOfTheBrief()
    {
        Assert.Equal(
            [
                "check_in_time", "early_arrival_or_late_checkout", "parking", "wifi", "directions", "pets", "invoice_request", "thank_you", "malfunction", "noise",
                "discount_request", "cancellation_or_refund", "complaint", "emergency", "other",
            ],
            AssistantSituationCatalog.All.Select(info => info.Code));
    }

    [Fact]
    public void SensitiveCategories_AreTheOnesTheSituationCarriesByNature()
    {
        Assert.Equal(SensitiveCategory.PersonalData | SensitiveCategory.Money, Info(AssistantSituation.InvoiceRequest).SensitiveCategories);
        Assert.Equal(SensitiveCategory.Damage, Info(AssistantSituation.Malfunction).SensitiveCategories);
        Assert.Equal(SensitiveCategory.Complaint, Info(AssistantSituation.Noise).SensitiveCategories);
        Assert.Equal(SensitiveCategory.Complaint, Info(AssistantSituation.Complaint).SensitiveCategories);
        Assert.Equal(SensitiveCategory.Money, Info(AssistantSituation.DiscountRequest).SensitiveCategories);
        Assert.Equal(SensitiveCategory.CancellationRefund | SensitiveCategory.Money, Info(AssistantSituation.CancellationOrRefund).SensitiveCategories);
        Assert.Equal(SensitiveCategory.Safety, Info(AssistantSituation.Emergency).SensitiveCategories);

        foreach (var plain in new[]
                 {
                     AssistantSituation.CheckInTime, AssistantSituation.EarlyArrivalOrLateCheckout, AssistantSituation.Parking, AssistantSituation.WiFi,
                     AssistantSituation.Directions, AssistantSituation.Pets, AssistantSituation.ThankYou, AssistantSituation.Other,
                 })
        {
            Assert.Equal(SensitiveCategory.None, Info(plain).SensitiveCategories);
            Assert.False(Info(plain).IsSensitive);
        }
    }

    [Fact]
    public void Modes_ASensitiveSituationAndOther_AllowEscalateAndDraftOnly_TheOthersAllowAllFour_EveryOneStartsInDraftButTheEmergency()
    {
        foreach (var info in AssistantSituationCatalog.All)
        {
            var neverAutomatic = info.IsSensitive || info.Situation == AssistantSituation.Other;
            AssistantMode[] expected = neverAutomatic
                ? [AssistantMode.Escalate, AssistantMode.Draft]
                : [AssistantMode.Escalate, AssistantMode.Draft, AssistantMode.Automatic, AssistantMode.AutomaticWithExceptions];
            Assert.Equal(expected, info.AllowedModes);
            Assert.Contains(info.DefaultMode, info.AllowedModes);
            Assert.Equal(info.Situation == AssistantSituation.Emergency ? AssistantMode.Escalate : AssistantMode.Draft, info.DefaultMode);
        }
    }

    [Fact]
    public void SourceTopics_AndTheTopicsAnAnswerNeeds_AreTheOnesOfTheBrief()
    {
        Assert.Equal(KnowledgeTopic.CheckIn, Info(AssistantSituation.CheckInTime).SourceTopic);
        Assert.Equal(KnowledgeTopic.CheckIn, Info(AssistantSituation.EarlyArrivalOrLateCheckout).SourceTopic);
        Assert.Equal(KnowledgeTopic.Parking, Info(AssistantSituation.Parking).SourceTopic);
        Assert.Equal(KnowledgeTopic.WiFi, Info(AssistantSituation.WiFi).SourceTopic);
        Assert.Equal(KnowledgeTopic.Directions, Info(AssistantSituation.Directions).SourceTopic);
        Assert.Equal(KnowledgeTopic.Pets, Info(AssistantSituation.Pets).SourceTopic);
        Assert.Equal(KnowledgeTopic.HouseRules, Info(AssistantSituation.Noise).SourceTopic);
        Assert.Equal(KnowledgeTopic.Contacts, Info(AssistantSituation.Emergency).SourceTopic);

        foreach (var none in new[]
                 {
                     AssistantSituation.InvoiceRequest, AssistantSituation.ThankYou, AssistantSituation.Malfunction, AssistantSituation.DiscountRequest,
                     AssistantSituation.CancellationOrRefund, AssistantSituation.Complaint, AssistantSituation.Other,
                 })
        {
            Assert.Null(Info(none).SourceTopic);
            Assert.Empty(Info(none).RequiredTopics);
        }

        // A situation that is answered from a topic needs data on that topic; an early arrival also needs the check-out.
        Assert.Equal([KnowledgeTopic.CheckIn, KnowledgeTopic.CheckOut], Info(AssistantSituation.EarlyArrivalOrLateCheckout).RequiredTopics);
        Assert.All(
            AssistantSituationCatalog.All.Where(info => info.SourceTopic is not null),
            info => Assert.Contains(info.SourceTopic!.Value, info.RequiredTopics));
    }

    [Fact]
    public void Of_AValueThatIsNoSituation_IsAnError_NeverADefault() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AssistantSituationCatalog.Of((AssistantSituation)55));

    [Theory]
    [InlineData("check_in_time", AssistantSituation.CheckInTime)]
    [InlineData("CHECK_IN_TIME", AssistantSituation.CheckInTime)]
    [InlineData(" cancellation_or_refund ", AssistantSituation.CancellationOrRefund)]
    [InlineData("CheckInTime", AssistantSituation.CheckInTime)]
    [InlineData("checkintime", AssistantSituation.CheckInTime)]
    [InlineData("WiFi", AssistantSituation.WiFi)]
    [InlineData("wifi", AssistantSituation.WiFi)]
    [InlineData("other", AssistantSituation.Other)]
    [InlineData("Emergency", AssistantSituation.Emergency)]
    public void TryParse_AcceptsTheCodeAndTheNameOfTheMember_InAnyCase(string text, AssistantSituation expected)
    {
        Assert.True(AssistantSituationCatalog.TryParse(text, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("7")]
    [InlineData("99")]
    [InlineData("gossip")]
    [InlineData("check-in-time")]
    public void TryParse_ANumberOrANameThatIsNoSituation_IsRefused(string text) =>
        Assert.False(AssistantSituationCatalog.TryParse(text, out _));

    [Fact]
    public void TryParse_NothingAtAll_IsRefused()
    {
        Assert.False(AssistantSituationCatalog.TryParse(null, out _));
        Assert.False(AssistantSituationCatalog.TryFromCode(null, out _));
    }

    [Fact]
    public void TryFromCode_IsTheStableCodeOnly_NotTheNameOfTheMember()
    {
        Assert.True(AssistantSituationCatalog.TryFromCode("check_in_time", out var fromCode));
        Assert.Equal(AssistantSituation.CheckInTime, fromCode);
        Assert.False(AssistantSituationCatalog.TryFromCode("CheckInTime", out _));
    }

    [GeneratedRegex(@"^[a-z]+(_[a-z]+)*$")]
    private static partial Regex SnakeCase();
}
