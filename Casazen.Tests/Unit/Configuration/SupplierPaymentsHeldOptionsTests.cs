using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// SP-15c-a: the times of the money CasaZen holds are configuration, validated at startup, never constants. The one that matters most is the
/// life of the block on the customer's card: it must stay far below what the card networks guarantee (a Visa charged without the cardholder
/// present: 4 days 18 hours), or a booking could be accepted with no money behind it (VT-D22).
/// </summary>
public class SupplierPaymentsHeldOptionsTests
{
    private static SupplierPaymentsOptions Valid() => new() { CommissionPercent = 10m };

    [Fact]
    public void TheDefaults_AreValid_AndPrudent()
    {
        var options = Valid();

        Assert.Empty(options.Validate());
        Assert.Equal(48, options.AuthorizationValidityHours);
        Assert.Equal(60, options.ReleaseAfterMinutes);
        Assert.Equal(60, options.MaxHoldDays);
        Assert.Equal(7, options.HoldWarningDays);
        Assert.Equal(15, options.TransferRetryBaseMinutes);
        Assert.Equal(720, options.TransferRetryMaxMinutes);
        Assert.Equal(5, options.TransferAlertAfterAttempts);

        // What a host pays goes through CasaZen unless an environment switches it off (SP-15c-b, the way back to the direct charge).
        Assert.True(options.HeldForHosts);

        // The block is treated as usable for far less than the 4 days 18 hours (114 hours) of the shortest card block.
        Assert.True(options.AuthorizationValidity < TimeSpan.FromHours(114));
        Assert.True(SupplierPaymentsOptions.MaxAuthorizationValidityHours < 114);
        Assert.Equal(TimeSpan.FromMinutes(60), options.ReleaseAfter);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(96)]
    public void AuthorizationValidityHours_InsideTheRange_IsValid(int hours)
    {
        var options = Valid();
        options.AuthorizationValidityHours = hours;

        Assert.Empty(options.Validate());
        Assert.Equal(TimeSpan.FromHours(hours), options.AuthorizationValidity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(97)]
    [InlineData(114)] // exactly what a card keeps: no margin at all
    [InlineData(168)] // the seven days of most cards
    public void AuthorizationValidityHours_OutsideTheRange_IsRefusedAtStartup(int hours)
    {
        var options = Valid();
        options.AuthorizationValidityHours = hours;

        var failure = Assert.Single(options.Validate());
        Assert.Contains("SupplierPayments__AuthorizationValidityHours", failure, StringComparison.Ordinal);
        Assert.False(new SupplierPaymentsOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(10_080)]
    public void ReleaseAfterMinutes_InsideTheRange_IsValid(int minutes)
    {
        var options = Valid();
        options.ReleaseAfterMinutes = minutes;

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_081)]
    public void ReleaseAfterMinutes_OutsideTheRange_IsRefused(int minutes)
    {
        var options = Valid();
        options.ReleaseAfterMinutes = minutes;

        Assert.Contains("SupplierPayments__ReleaseAfterMinutes", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(366, 7)]
    [InlineData(60, 61)] // warns later than the maximum
    [InlineData(60, -1)]
    public void MaxHoldDaysAndItsWarning_MustBeConsistent(int maxHoldDays, int warningDays)
    {
        var options = Valid();
        options.MaxHoldDays = maxHoldDays;
        options.HoldWarningDays = warningDays;

        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData(0, 720, 5)]
    [InlineData(1_441, 10_080, 5)]
    [InlineData(15, 10, 5)] // the longest wait below the first
    [InlineData(15, 10_081, 5)]
    [InlineData(15, 720, 0)]
    [InlineData(15, 720, 101)]
    public void TheRetryOfATransfer_MustBeConsistent(int baseMinutes, int maxMinutes, int alertAfter)
    {
        var options = Valid();
        options.TransferRetryBaseMinutes = baseMinutes;
        options.TransferRetryMaxMinutes = maxMinutes;
        options.TransferAlertAfterAttempts = alertAfter;

        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void TheValuesAreReadFromTheConfiguration_UnderTheSectionOfThePayments()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupplierPayments:CommissionPercent"] = "12.5",
                ["SupplierPayments:AuthorizationValidityHours"] = "30",
                ["SupplierPayments:ReleaseAfterMinutes"] = "90",
                ["SupplierPayments:MaxHoldDays"] = "45",
                ["SupplierPayments:HoldWarningDays"] = "5",
                ["SupplierPayments:TransferRetryBaseMinutes"] = "10",
                ["SupplierPayments:TransferRetryMaxMinutes"] = "600",
                ["SupplierPayments:TransferAlertAfterAttempts"] = "3",
            })
            .Build();

        var options = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Empty(options.Validate());
        Assert.Equal(30, options.AuthorizationValidityHours);
        Assert.Equal(90, options.ReleaseAfterMinutes);
        Assert.Equal(45, options.MaxHoldDays);
        Assert.Equal(5, options.HoldWarningDays);
        Assert.Equal(10, options.TransferRetryBaseMinutes);
        Assert.Equal(600, options.TransferRetryMaxMinutes);
        Assert.Equal(3, options.TransferAlertAfterAttempts);
    }
}
