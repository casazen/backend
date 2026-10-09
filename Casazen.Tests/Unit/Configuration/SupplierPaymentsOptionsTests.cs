using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// SP-15a: the section <c>SupplierPayments</c>. The commission is configuration and never code (decision D3): there is no
/// default of the percentage in the options class, it must be configured and is validated at startup (0 to 50), exactly as the
/// advisory parameters of the long-term leases are.
/// </summary>
public class SupplierPaymentsOptionsTests
{
    private static SupplierPaymentsOptions Valid(decimal? percent = 10m) => new() { CommissionPercent = percent };

    [Fact]
    public void Options_WithNothingConfigured_HaveNoCommissionAndAreInvalid()
    {
        var options = new SupplierPaymentsOptions();

        Assert.Null(options.CommissionPercent);
        var failures = options.Validate();
        Assert.Contains(failures, f => f.Contains("SupplierPayments__CommissionPercent is missing", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => { _ = options.RequireCommissionPercent(); });
        Assert.False(new SupplierPaymentsOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7.5)]
    [InlineData(10)]
    [InlineData(33.33)]
    [InlineData(50)]
    public void Commission_InsideTheRange_IsValid(double percent)
    {
        var options = Valid((decimal)percent);

        Assert.Empty(options.Validate());
        Assert.True(new SupplierPaymentsOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal((decimal)percent, options.RequireCommissionPercent());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-10)]
    [InlineData(50.01)]
    [InlineData(51)]
    [InlineData(100)]
    [InlineData(10.123)] // more than two decimals: the percentage is stored with two
    public void Commission_OutsideTheRangeOrWithTooManyDecimals_IsRefused(double percent)
    {
        var options = Valid((decimal)percent);

        var failures = options.Validate();

        Assert.Single(failures);
        Assert.Contains("SupplierPayments__CommissionPercent must be between 0 and 50", failures[0], StringComparison.Ordinal);
        Assert.False(new SupplierPaymentsOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void TheMaximumCommission_IsTheOneTheDatabaseCheckAllows()
    {
        Assert.Equal(50m, SupplierPaymentsOptions.MaxCommissionPercent);
    }

    [Fact]
    public void Defaults_OfTheOtherSettings_AreTheOnesOfTheWaveSpec()
    {
        var options = Valid();

        Assert.Equal(7, options.LateAfterDays);
        Assert.Equal(30, options.PaymentLinkValidityDays);
        Assert.Equal(50, options.MinAmountCents);
        Assert.Equal(new[] { 2, 7 }, options.EffectiveReminderDays);
    }

    [Fact]
    public void EffectiveReminderDays_AreDistinctAndAscending_AndTwoAndSevenWhenNoneIsSet()
    {
        Assert.Equal(new[] { 2, 7 }, new SupplierPaymentsOptions { ReminderDays = [] }.EffectiveReminderDays);
        Assert.Equal(new[] { 1, 3, 10 }, new SupplierPaymentsOptions { ReminderDays = [10, 3, 3, 1, 10] }.EffectiveReminderDays);
    }

    [Theory]
    [InlineData(nameof(SupplierPaymentsOptions.LateAfterDays), 0)]
    [InlineData(nameof(SupplierPaymentsOptions.LateAfterDays), 366)]
    [InlineData(nameof(SupplierPaymentsOptions.PaymentLinkValidityDays), 0)]
    [InlineData(nameof(SupplierPaymentsOptions.PaymentLinkValidityDays), 366)]
    [InlineData(nameof(SupplierPaymentsOptions.MinAmountCents), 0)]
    [InlineData(nameof(SupplierPaymentsOptions.MinAmountCents), ServiceRequestLimits.MaxAmountCents + 1)]
    public void Settings_OutOfRange_AreRefusedAtStartup(string property, int value)
    {
        var options = Valid();
        typeof(SupplierPaymentsOptions).GetProperty(property)!.SetValue(options, value);

        var failures = options.Validate();

        Assert.Single(failures);
        Assert.Contains($"SupplierPayments__{property}", failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReminderDays_OutOfRange_AreRefused()
    {
        var failures = new SupplierPaymentsOptions { CommissionPercent = 10m, ReminderDays = [2, 0] }.Validate();

        Assert.Single(failures);
        Assert.Contains("SupplierPayments__ReminderDays", failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedAppsettings_BindAndValidate_WithTheProvisionalTenPercent()
    {
        // The provisional hypothesis of the product owner (decision D3) lives only here, in configuration.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        var options = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Equal(10m, options.CommissionPercent);
        Assert.Equal(7, options.LateAfterDays);
        Assert.Equal(new[] { 2, 7 }, options.EffectiveReminderDays);
        Assert.Equal(30, options.PaymentLinkValidityDays);
        Assert.Equal(50, options.MinAmountCents);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ARailwayVariable_OverridesTheCommittedCommission()
    {
        // Env var SupplierPayments__CommissionPercent=0 (a free period for the whole platform) wins over appsettings.json.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SupplierPayments:CommissionPercent"] = "0" })
            .Build();

        var options = configuration.GetSection(SupplierPaymentsOptions.SectionName).Get<SupplierPaymentsOptions>()!;

        Assert.Equal(0m, options.CommissionPercent);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Startup_WithAnInvalidCommission_FailsToResolveTheOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions<SupplierPaymentsOptions>().Configure(o => o.CommissionPercent = 75m).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupplierPaymentsOptions>, SupplierPaymentsOptionsValidator>();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SupplierPaymentsOptions>>().Value);

        Assert.Contains("SupplierPayments__CommissionPercent", exception.Message, StringComparison.Ordinal);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
