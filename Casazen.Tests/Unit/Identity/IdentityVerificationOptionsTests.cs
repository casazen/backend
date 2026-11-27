using System.Text.Json;
using Casazen.Core.Options;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F01/US01/T01 (NF-192, NF-194, NF-195): the section <c>IdentityVerification</c>. Its defaults are the ones of the brief and the committed
/// appsettings.json writes the same, <c>AddCasazenServices</c> binds it once from the configuration of the application, and
/// <see cref="IdentityVerificationOptions.Validate"/> names an invalid setting and its bounds, never the value that was set.
/// </summary>
public class IdentityVerificationOptionsTests
{
    [Fact]
    public void IdentityVerificationOptions_Defaults_AreTheBriefsAndTheCommittedAppsettingsWritesTheSame()
    {
        var defaults = new IdentityVerificationOptions();
        var section = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build()
            .GetSection(IdentityVerificationOptions.SectionName);
        var committed = new IdentityVerificationOptions();
        section.Bind(committed);

        Assert.Empty(defaults.Validate());
        Assert.Equal(
            ("IdentityPolicies/identity-policy-2026.11-1.json", "", "IdentityFormats"),
            (defaults.PolicyFile, defaults.PolicySha256, defaults.FormatsDirectory));
        Assert.Equal(
            (60, 262_144, 6_291_456, 720, 8_000),
            (defaults.Presence.MaxFrames, defaults.Presence.MaxFrameBytes, defaults.Presence.MaxSequenceBytes, defaults.Presence.MaxFrameSide,
                defaults.Presence.SequenceTimeoutMs));
        Assert.Equal((48, 3, 480), (defaults.Evidence.RetentionHours, defaults.Evidence.MaxFrames, defaults.Evidence.MaxSide));
        Assert.Equal(
            (24, 30, 12),
            (defaults.Retention.AbandonedAfterHours, defaults.Retention.DocumentCheckDays, defaults.Retention.VerificationLogMonths));

        // Every one of the 14 settings is written in appsettings.json, with the value of the class but one: the pin. The class leaves it empty (no policy is
        // used until one is pinned), the committed file pins the canonical SHA-256 of the policy file it ships (E70/F01/US02/T01, IdentityPolicyFileCoherenceTests).
        Assert.Equal(14, section.AsEnumerable(makePathsRelative: true).Count(setting => setting.Value is not null));
        Assert.Matches("^[0-9a-f]{64}$", committed.PolicySha256);
        committed.PolicySha256 = defaults.PolicySha256;
        Assert.Equal(JsonSerializer.Serialize(defaults), JsonSerializer.Serialize(committed));
    }

    [Fact]
    public void IdentityVerificationOptions_Validate_NamesTheSettingNotTheValue()
    {
        // Privacy: a configuration error reaches the logs and the staff. It names the setting and its bounds, never what was set.
        var options = new IdentityVerificationOptions
        {
            PolicySha256 = "not-a-hash-but-a-secret-73519",
            Presence = new IdentityPresenceOptions { MaxFrames = 987_654 },
            Evidence = new IdentityEvidenceOptions { RetentionHours = 4_242 },
            Retention = new IdentityRetentionOptions { VerificationLogMonths = 777 },
        };

        var failures = options.Validate();

        Assert.Equal(4, failures.Count);
        Assert.StartsWith("IdentityVerification:PolicySha256 ", failures[0], StringComparison.Ordinal);
        Assert.StartsWith("IdentityVerification:Presence:MaxFrames ", failures[1], StringComparison.Ordinal);
        Assert.StartsWith("IdentityVerification:Evidence:RetentionHours ", failures[2], StringComparison.Ordinal);
        Assert.StartsWith("IdentityVerification:Retention:VerificationLogMonths ", failures[3], StringComparison.Ordinal);
        Assert.All(failures, failure =>
        {
            Assert.DoesNotContain("73519", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("987654", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("4242", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("777", failure, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void IdentityVerificationOptions_ValidateRetention_JudgesOnlyTheThreePeriods_InTheWordsOfValidate()
    {
        // E73/F01/US02/T03 (review of S015): the retention needs its three periods and nothing else of the section; it names the setting, never the value, as Validate does.
        var options = new IdentityVerificationOptions
        {
            Presence = new IdentityPresenceOptions { MaxFrames = 987_654 },
            Evidence = new IdentityEvidenceOptions { RetentionHours = 4_242 },
            Retention = new IdentityRetentionOptions { AbandonedAfterHours = 0, VerificationLogMonths = 777 },
        };

        var retention = options.ValidateRetention();

        Assert.Equal(2, retention.Count);
        Assert.StartsWith("IdentityVerification:Retention:AbandonedAfterHours ", retention[0], StringComparison.Ordinal);
        Assert.StartsWith("IdentityVerification:Retention:VerificationLogMonths ", retention[1], StringComparison.Ordinal);
        Assert.Equal(retention, options.Validate().Where(failure => failure.StartsWith("IdentityVerification:Retention:", StringComparison.Ordinal)));
        Assert.DoesNotContain("777", string.Concat(retention), StringComparison.Ordinal);
        Assert.Empty(new IdentityVerificationOptions { Presence = new IdentityPresenceOptions { MaxFrames = 1 } }.ValidateRetention());
    }

    [Theory]
    [InlineData("Evidence:RetentionHours", "1", true)]
    [InlineData("Evidence:RetentionHours", "168", true)]
    [InlineData("Evidence:RetentionHours", "169", false)]
    [InlineData("Evidence:MaxFrames", "0", true)]
    [InlineData("Evidence:MaxFrames", "6", false)]
    [InlineData("Retention:AbandonedAfterHours", "0", false)]
    [InlineData("Retention:DocumentCheckDays", "365", true)]
    [InlineData("Retention:DocumentCheckDays", "366", false)]
    [InlineData("Retention:VerificationLogMonths", "121", false)]
    [InlineData("Presence:MaxSequenceBytes", "524288", true)]
    [InlineData("Presence:MaxFrameBytes", "1048576", true)]
    [InlineData("PolicySha256", "0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79", true)]
    [InlineData("PolicySha256", "0BA9FBFA", false)]
    [InlineData("PolicyFile", " ", false)]
    public void IdentityVerificationOptions_Validate_KeepsTheBoundsOfTheBrief(string setting, string value, bool valid)
    {
        var options = Bind(new Dictionary<string, string?> { [$"{IdentityVerificationOptions.SectionName}:{setting}"] = value });

        var failures = options.Validate();

        Assert.Equal(valid, failures.Count == 0);
        if (!valid)
            Assert.Contains($"{IdentityVerificationOptions.SectionName}:{setting} ", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityVerificationOptions_Validate_RefusesASequenceSmallerThanOneFrame()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["IdentityVerification:Presence:MaxFrameBytes"] = "1048576",
            ["IdentityVerification:Presence:MaxSequenceBytes"] = "524288",
        });

        var failure = Assert.Single(options.Validate());

        Assert.StartsWith("IdentityVerification:Presence:MaxSequenceBytes must not be smaller than", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCasazenServices_BindsTheSectionOnce_FromTheConfigurationOfTheApplication()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["IdentityVerification:Evidence:RetentionHours"] = "12" })
            .Build());

        services.AddCasazenServices();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConfigureOptions<IdentityVerificationOptions>));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IdentityVerificationOptions>>().Value;
        Assert.Equal(12, options.Evidence.RetentionHours);
        Assert.Equal(60, options.Presence.MaxFrames);
        Assert.Equal("IdentityFormats", options.FormatsDirectory);
    }

    private static IdentityVerificationOptions Bind(Dictionary<string, string?> values)
    {
        var options = new IdentityVerificationOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(IdentityVerificationOptions.SectionName).Bind(options);
        return options;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
