using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// SP-15c-c (decision VT-D16): the times of the complaint and the dispute are configuration, validated at startup, never constants — the product owner
/// changes them without a release. The defaults are the ones of the decision: 48 hours for the supplier to reply, 72 for the admins to decide, 48 after the end
/// of the work for CasaZen to open the no-show complaint.
/// </summary>
public class SupplierPaymentsDisputeOptionsTests
{
    private static SupplierPaymentsOptions Valid() => new() { CommissionPercent = 10m };

    [Fact]
    public void TheDefaults_AreTheOnesOfTheDecision_AndAreValid()
    {
        var options = Valid();

        Assert.Empty(options.Validate());
        Assert.Equal(48, options.DisputeSupplierReplyHours);
        Assert.Equal(72, options.DisputeDecisionTargetHours);
        Assert.Equal(48, options.DisputeNoShowHours);
        Assert.Equal(30, options.DisputeRefundAlertMinutes);
        Assert.Equal(TimeSpan.FromHours(48), options.DisputeSupplierReply);
        Assert.Equal(TimeSpan.FromHours(72), options.DisputeDecisionTarget);
        Assert.Equal(TimeSpan.FromHours(48), options.DisputeNoShow);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(48)]
    [InlineData(720)]
    public void TheTimes_InsideTheRange_AreValid(int hours)
    {
        var options = Valid();
        options.DisputeSupplierReplyHours = hours;
        options.DisputeDecisionTargetHours = hours;
        options.DisputeNoShowHours = hours;

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(721)]
    public void TheTimes_OutsideTheRange_AreRefusedAtStartup_EachByName(int hours)
    {
        var reply = Valid();
        reply.DisputeSupplierReplyHours = hours;
        Assert.Contains("SupplierPayments__DisputeSupplierReplyHours", Assert.Single(reply.Validate()), StringComparison.Ordinal);

        var decision = Valid();
        decision.DisputeDecisionTargetHours = hours;
        Assert.Contains("SupplierPayments__DisputeDecisionTargetHours", Assert.Single(decision.Validate()), StringComparison.Ordinal);

        var noShow = Valid();
        noShow.DisputeNoShowHours = hours;
        Assert.Contains("SupplierPayments__DisputeNoShowHours", Assert.Single(noShow.Validate()), StringComparison.Ordinal);

        Assert.False(new SupplierPaymentsOptionsValidator().Validate(null, reply).Succeeded);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(1_440, true)]
    [InlineData(0, false)]
    [InlineData(1_441, false)]
    public void TheRefundAlert_IsBetweenOneMinuteAndADay(int minutes, bool valid)
    {
        var options = Valid();
        options.DisputeRefundAlertMinutes = minutes;

        Assert.Equal(valid, options.Validate().Count == 0);
    }

    [Fact]
    public void TheValuesAreReadFromTheConfiguration_UnderTheSectionOfThePayments()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupplierPayments:CommissionPercent"] = "10",
                ["SupplierPayments:DisputeSupplierReplyHours"] = "24",
                ["SupplierPayments:DisputeDecisionTargetHours"] = "96",
                ["SupplierPayments:DisputeNoShowHours"] = "12",
                ["SupplierPayments:DisputeRefundAlertMinutes"] = "15",
            })
            .Build();

        var options = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Empty(options.Validate());
        Assert.Equal(24, options.DisputeSupplierReplyHours);
        Assert.Equal(96, options.DisputeDecisionTargetHours);
        Assert.Equal(12, options.DisputeNoShowHours);
        Assert.Equal(15, options.DisputeRefundAlertMinutes);
    }
}
