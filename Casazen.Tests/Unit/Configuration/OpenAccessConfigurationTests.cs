using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// BL-01: the "accesso aperto" settings are checked at startup in every environment (a wrong value stops the container, the
/// previous deployment stays), the committed <c>appsettings.json</c> keeps the switch off, and a deployment with the switch on
/// says so in its log.
/// </summary>
public class OpenAccessConfigurationTests
{
    private static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddCasazenOpenAccessConfiguration(configuration);
        return services.BuildServiceProvider();
    }

    private static void Validate(IConfiguration configuration)
    {
        using var provider = BuildProvider(configuration);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    // ─── Startup validation ──────────────────────────────────────────────────────

    [Fact]
    public void Startup_NothingConfigured_Starts()
    {
        Validate(Config());
    }

    [Fact]
    public void Startup_CommittedAppsettings_StartsWithTheSwitchOffAndScaleAsTheTier()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Validate(configuration);

        Assert.Equal("False", configuration["Entitlement:OpenAccess:Enabled"]);
        Assert.Equal("Scale", configuration["Entitlement:OpenAccess:Tier"]);
        var setting = OpenAccess.Read(configuration);
        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Scale, setting.Tier);
        Assert.All(Enum.GetValues<PlanTier>(), paid => Assert.Equal(paid, setting.Apply(paid)));
    }

    [Theory]
    [InlineData("true", "Scale")]
    [InlineData("true", "Pro")]
    [InlineData("true", "starter")]
    [InlineData("true", null)]
    [InlineData("false", "Scale")]
    [InlineData(null, "Scale")]
    public void Startup_ValidSettings_Starts(string? enabled, string? tier)
    {
        Validate(Config(new()
        {
            ["Entitlement:OpenAccess:Enabled"] = enabled,
            ["Entitlement:OpenAccess:Tier"] = tier,
        }));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("ture")]
    public void Startup_SwitchNotABoolean_FailsNamingTheVariable(string enabled)
    {
        using var provider = BuildProvider(Config(new() { ["Entitlement:OpenAccess:Enabled"] = enabled }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains("Entitlement__OpenAccess__Enabled must be true or false", failure);
        Assert.Contains("docs/runbooks/open-access.md", failure);
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Free")]
    [InlineData("2")]
    public void Startup_UnknownTier_FailsNamingTheVariableAndThePlans(string tier)
    {
        using var provider = BuildProvider(Config(new()
        {
            ["Entitlement:OpenAccess:Enabled"] = "true",
            ["Entitlement:OpenAccess:Tier"] = tier,
        }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains("Entitlement__OpenAccess__Tier must be the name of a plan (Starter, Pro, Scale)", failure);
        Assert.Contains("docs/runbooks/open-access.md", failure);
    }

    [Fact]
    public void Startup_BothSettingsWrong_ListsBothProblems()
    {
        using var provider = BuildProvider(Config(new()
        {
            ["Entitlement:OpenAccess:Enabled"] = "yes",
            ["Entitlement:OpenAccess:Tier"] = "Enterprise",
        }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(2, ex.Failures.Count());
    }

    // ─── Startup log ─────────────────────────────────────────────────────────────

    [Fact]
    public void LogOpenAccess_SwitchOn_LogsOneWarningWithTheTierAndTheRunbook()
    {
        var logs = new CapturingLoggerProvider();
        using var app = BuildApp(logs, new()
        {
            ["Entitlement:OpenAccess:Enabled"] = "true",
            ["Entitlement:OpenAccess:Tier"] = "Pro",
        });

        app.LogOpenAccess();

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Open access is ON", entry.Message);
        Assert.Contains("Entitlement__OpenAccess__Enabled", entry.Message);
        Assert.Contains("Pro", entry.Message);
        Assert.Contains("docs/runbooks/open-access.md", entry.Message);
    }

    [Fact]
    public void LogOpenAccess_SwitchOnWithoutTier_LogsScale()
    {
        var logs = new CapturingLoggerProvider();
        using var app = BuildApp(logs, new() { ["Entitlement:OpenAccess:Enabled"] = "true" });

        app.LogOpenAccess();

        Assert.Contains("Scale", Assert.Single(logs.Entries).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes")] // not valid: refused at startup, read as off
    public void LogOpenAccess_SwitchOffOrNotValid_LogsNothing(string? enabled)
    {
        var logs = new CapturingLoggerProvider();
        using var app = BuildApp(logs, new() { ["Entitlement:OpenAccess:Enabled"] = enabled });

        app.LogOpenAccess();

        Assert.Empty(logs.Entries);
    }

    private static WebApplication BuildApp(CapturingLoggerProvider logs, Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        return builder.Build();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    /// <summary>Keeps every log line written through the host's logger factory.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(Entries);

        public void Dispose()
        {
        }

        private sealed class Sink(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
