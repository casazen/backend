using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>E05/F04/US01 (NF-68): the pure rules of the approval queue, with no clock and no database.</summary>
public class ApprovalQueueRulesTests
{
    private static readonly Guid Item = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    // ─── The keys of an alert ───

    /// <summary>
    /// The keys of the push, of the bell and of the e-mail slot write the instant of the alert with a custom format of a date, which takes its calendar from the culture of the machine
    /// (Buddhist years in <c>th-TH</c>, Hijri in <c>ar-SA</c>): the stamp is the invariant one, proved here on a real instant under three cultures.
    /// </summary>
    [Theory]
    [InlineData("it-IT")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void TheKeysOfAnAlert_FormatARealInstant_WithTheInvariantCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var at = new DateTime(2026, 11, 10, 11, 5, 9, 123, DateTimeKind.Utc);

            Assert.Equal("20261110110509", ApprovalQueueRules.StampOf(at));
            Assert.Equal("approval:3f2504e04f8941d39a0c0305e82c3301:20261110110509:auth0|owner", PushDeliveryKeys.ApprovalQueueItem(Item, at, "auth0|owner"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheKeyOfAPush_OfAnAlertOfAnotherMomentOrAnotherPerson_IsAnotherKey_AndALongUserIdIsHashedToFit()
    {
        var at = new DateTime(2026, 11, 10, 11, 5, 9, DateTimeKind.Utc);
        var longUser = "auth0|" + new string('u', 300);

        var same = PushDeliveryKeys.ApprovalQueueItem(Item, at, "auth0|owner");
        var later = PushDeliveryKeys.ApprovalQueueItem(Item, at.AddSeconds(1), "auth0|owner");
        var other = PushDeliveryKeys.ApprovalQueueItem(Item, at, "auth0|manager");
        var hashed = PushDeliveryKeys.ApprovalQueueItem(Item, at, longUser);

        Assert.Equal(same, PushDeliveryKeys.ApprovalQueueItem(Item, at.AddMilliseconds(400), "auth0|owner"));
        Assert.Equal(3, new[] { same, later, other }.Distinct().Count());
        Assert.InRange(hashed.Length, 1, Casazen.Core.Entities.InAppNotification.DeliveryKeyMaxLength);
        Assert.Equal(hashed, PushDeliveryKeys.ApprovalQueueItem(Item, at, longUser));
        Assert.DoesNotContain(longUser, hashed, StringComparison.Ordinal);
        // A Local or an Unspecified instant is read as the UTC one it is meant to be.
        Assert.Equal(ApprovalQueueRules.StampOf(at), ApprovalQueueRules.StampOf(DateTime.SpecifyKind(at, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void SendKey_OfAnItem_IsOneTheServiceOfTheRepliesAccepts()
    {
        var id = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

        var first = ApprovalQueueRules.SendKeyOf(id);
        var second = ApprovalQueueRules.SendKeyOf(id, 2);
        var last = ApprovalQueueRules.SendKeyOf(id, ApprovalQueueRules.MaxSendAttempts);

        Assert.Equal("approval-3f2504e0-4f89-41d3-9a0c-0305e82c3301", first);
        Assert.Equal("approval-3f2504e0-4f89-41d3-9a0c-0305e82c3301-2", second);
        // The rule of the key of a send: letters, digits, dashes, 8 to 64 characters. The colon of the brief would be refused (idempotency_key_invalid).
        Assert.All(new[] { first, second, last }, key => Assert.Equal(key, GuestMessageRules.NormalizeIdempotencyKey(key)));
        Assert.Null(GuestMessageRules.NormalizeIdempotencyKey($"approval:{id:D}"));
        Assert.Equal(3, new[] { first, second, last }.Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => ApprovalQueueRules.SendKeyOf(id, 0));
    }

    [Fact]
    public void CampaignSubjectMaxLength_IsTheColumnOfTheCampaign()
    {
        var column = typeof(Campaign).GetProperty(nameof(Campaign.Subject))!.GetCustomAttribute<MaxLengthAttribute>();

        Assert.NotNull(column);
        Assert.Equal(ApprovalQueueRules.CampaignSubjectMaxLength, column.Length);
    }

    [Fact]
    public void TheColumnsOfTheItem_HaveTheLengthsOfTheBrief()
    {
        static int Max(string property) => typeof(ApprovalQueueItem).GetProperty(property)!.GetCustomAttribute<MaxLengthAttribute>()!.Length;

        Assert.Equal(500, Max(nameof(ApprovalQueueItem.RejectionReason)));
        Assert.Equal(255, Max(nameof(ApprovalQueueItem.EditedByUserId)));
        Assert.Equal(50, Max(nameof(ApprovalQueueItem.OriginAction)));
        Assert.Equal(100, Max(nameof(ApprovalQueueItem.OriginActionError)));
        Assert.Equal(64, Max(nameof(ApprovalQueueItem.SituationCode)));
        Assert.Equal(80, Max(nameof(ApprovalQueueItem.ThreadKey)));
        Assert.Equal(OtaMessage.RuleCodeMaxLength, ApprovalQueueItem.SituationCodeMaxLength);
        Assert.True(MessageThreadKey.MaxLength <= ApprovalQueueItem.ThreadKeyMaxLength);
    }

    // ─── The text of an edit ───

    [Theory]
    [InlineData(ApprovalItemTypes.MessageDraft, "  Buongiorno,\r\n\r\n\r\n\r\ntutto bene  ", "Buongiorno,\n\ntutto bene")]
    [InlineData(ApprovalItemTypes.MessageDraft, "Ciao\u0007 a tutti", "Ciao a tutti")]
    [InlineData(ApprovalItemTypes.Campaign, "  Oggetto \r\n\r\n <p>Testo</p> ", "Oggetto\n\n<p>Testo</p>")]
    [InlineData(ApprovalItemTypes.Campaign, "Oggetto\n\n\n\n<p>Testo</p>", "Oggetto\n\n<p>Testo</p>")]
    [InlineData(ApprovalItemTypes.AiText, "  Un testo dell'AI \n", "Un testo dell'AI")]
    [InlineData("QualcosAltro", " x ", "x")]
    public void NormalizeEditedText_ATextOfTheType_IsStoredNormalized(string itemType, string text, string expected) =>
        Assert.Equal(expected, ApprovalQueueRules.NormalizeEditedText(itemType, text));

    [Theory]
    [InlineData(ApprovalItemTypes.MessageDraft, null)]
    [InlineData(ApprovalItemTypes.MessageDraft, "")]
    [InlineData(ApprovalItemTypes.MessageDraft, " \r\n ")]
    [InlineData(ApprovalItemTypes.Campaign, "Solo l'oggetto")]
    [InlineData(ApprovalItemTypes.Campaign, "Oggetto\nsu due righe\n\nTesto")]
    [InlineData(ApprovalItemTypes.Campaign, "\n\nTesto senza oggetto")]
    [InlineData(ApprovalItemTypes.Campaign, "Oggetto\n\n   ")]
    [InlineData(ApprovalItemTypes.AiText, "   ")]
    public void NormalizeEditedText_AnUnusableText_IsNull(string itemType, string? text) =>
        Assert.Null(ApprovalQueueRules.NormalizeEditedText(itemType, text));

    [Fact]
    public void NormalizeEditedText_AMessageOfTheLimit_IsKept_OneOverIsNot()
    {
        Assert.Equal(GuestMessageRules.TextMaxLength, ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.MessageDraft, new string('a', 2000))!.Length);
        Assert.Null(ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.MessageDraft, new string('a', 2001)));
        // The limit is the one of the message of the guest: a text of another type is not cut by it.
        Assert.Equal(5000, ApprovalQueueRules.NormalizeEditedText(ApprovalItemTypes.AiText, new string('a', 5000))!.Length);
    }

    [Fact]
    public void TryParseCampaignText_TheTextOfTheItemOfTheCampaignService_IsReadBack()
    {
        var text = ApprovalQueueRules.CampaignTextOf("Offerta di novembre", "<p>Prima riga</p>\n\n<p>Dopo una riga vuota</p>");

        Assert.True(ApprovalQueueRules.TryParseCampaignText(text, out var subject, out var body));
        Assert.Equal("Offerta di novembre", subject);
        Assert.Equal("<p>Prima riga</p>\n\n<p>Dopo una riga vuota</p>", body);
        Assert.Equal(text, ApprovalQueueRules.CampaignTextOf(subject, body));
    }

    [Fact]
    public void TryParseCampaignText_ASubjectOfTheLimit_IsKept_OneOverIsNot()
    {
        Assert.True(ApprovalQueueRules.TryParseCampaignText(new string('s', 200) + "\n\nTesto", out var subject, out _));
        Assert.Equal(200, subject.Length);
        Assert.False(ApprovalQueueRules.TryParseCampaignText(new string('s', 201) + "\n\nTesto", out _, out _));
    }

    // ─── The reason of a rejection ───

    [Theory]
    [InlineData("Il tono non va", "Il tono non va")]
    [InlineData("  Il tono\r\nnon va  ", "Il tono\nnon va")]
    public void NormalizeRejectionReason_AReason_IsStoredTrimmed(string reason, string expected) =>
        Assert.Equal(expected, ApprovalQueueRules.NormalizeRejectionReason(reason));

    [Fact]
    public void NormalizeRejectionReason_NoneBlankOrTooLong_IsNull()
    {
        Assert.Null(ApprovalQueueRules.NormalizeRejectionReason(null));
        Assert.Null(ApprovalQueueRules.NormalizeRejectionReason(string.Empty));
        Assert.Null(ApprovalQueueRules.NormalizeRejectionReason("  \r\n  "));
        Assert.Equal(500, ApprovalQueueRules.NormalizeRejectionReason(new string('r', 500))!.Length);
        Assert.Null(ApprovalQueueRules.NormalizeRejectionReason(new string('r', 501)));
    }

    // ─── The result of an action ───

    [Fact]
    public void ApprovalActionResult_AFailureCodeLongerThanTheColumn_IsCut()
    {
        var failed = ApprovalActionResult.Failed(new string('c', 150));

        Assert.False(failed.Succeeded);
        Assert.Equal(ApprovalQueueItem.OriginActionErrorMaxLength, failed.ErrorCode!.Length);
        Assert.Throws<ArgumentException>(() => ApprovalActionResult.Failed(" "));
        Assert.True(ApprovalActionResult.Executed("Sent").Succeeded);
    }

    [Fact]
    public void OriginActions_AreTheFourOfTheBrief_AndTheCategoryOfThePushIsTheOneOfTheApp()
    {
        Assert.Equal(
            ["ApproveCampaign", "None", "SendGuestMessage", "SendReviewReply"],
            typeof(ApprovalOriginActions).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal));
        Assert.Equal("draft-ready", ApprovalQueueRules.PushCategory);
        Assert.Equal(7, ApprovalQueueRules.ExpirationDays);
    }
}
