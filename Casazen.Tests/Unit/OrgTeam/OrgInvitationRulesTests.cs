using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Xunit;

namespace Casazen.Tests.Unit.OrgTeam;

/// <summary>AM-02: the fixed numbers and small rules of the org invitations.</summary>
public class OrgInvitationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Validity_IsSevenDaysAndTheReminderTheThird()
    {
        Assert.Equal(TimeSpan.FromDays(7), OrgInvitationRules.Validity);
        Assert.Equal(TimeSpan.FromDays(3), OrgInvitationRules.ReminderAfter);
        Assert.Equal(30, OrgInvitationRules.DefaultRetentionDays);
    }

    [Theory]
    [InlineData("  Anna.Leone@Example.IT ", "anna.leone@example.it")]
    [InlineData("MARIO@EXAMPLE.COM", "mario@example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeEmail_TrimsAndLowercases(string? email, string expected)
    {
        Assert.Equal(expected, OrgInvitationRules.NormalizeEmail(email));
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("fr", "it")]
    [InlineData("", "it")]
    [InlineData(null, "it")]
    public void NormalizeLanguage_SupportedOrTheDefault(string? language, string expected)
    {
        Assert.Equal(expected, OrgInvitationRules.NormalizeLanguage(language));
    }

    [Fact]
    public void ReminderDueAt_IsThreeDaysAfterTheInvitationWasSent()
    {
        var sent = Now;
        var expiresAt = sent + OrgInvitationRules.Validity;

        Assert.Equal(sent + OrgInvitationRules.ReminderAfter, OrgInvitationRules.ReminderDueAt(expiresAt));
    }

    [Theory]
    [InlineData(OrgInvitationStatus.Pending, 1, true)]
    [InlineData(OrgInvitationStatus.Pending, 0, false)]
    [InlineData(OrgInvitationStatus.Pending, -1, false)]
    [InlineData(OrgInvitationStatus.Accepted, 1, false)]
    [InlineData(OrgInvitationStatus.Revoked, 1, false)]
    [InlineData(OrgInvitationStatus.Expired, 1, false)]
    public void IsOpen_OnlyAPendingInvitationBeforeItsExpiry(OrgInvitationStatus status, int daysLeft, bool expected)
    {
        Assert.Equal(expected, OrgInvitationRules.IsOpen(status, Now.AddDays(daysLeft), Now));
    }
}
