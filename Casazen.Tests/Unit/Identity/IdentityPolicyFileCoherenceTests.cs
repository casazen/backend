using System.Xml.Linq;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F01/US02/T01 (NF-194, ADR-013-R18): the policy file that ships with the API is the file whose canonical SHA-256 the committed configuration pins, it is in the
/// build and publish output, every policy file of the folder is valid and named after its version, and the C# types hold no number of their own: every threshold
/// is read from the file. A qualified policy file is never edited: when one of these fails because a file changed, write a NEW file with its own version and pin it.
/// </summary>
public class IdentityPolicyFileCoherenceTests
{
    [Fact]
    public void ShippedPolicy_IsInTheBuildOutput_AndItsCanonicalSha256IsThePinnedOne()
    {
        var pinned = CommittedOptions().PolicySha256;
        var source = Path.Combine(IdentityPolicyTestFiles.SourceFolder(), IdentityPolicyTestFiles.ShippedFileName);
        var output = Path.Combine(AppContext.BaseDirectory, "IdentityPolicies", IdentityPolicyTestFiles.ShippedFileName);

        var computed = IdentityPolicyProvider.CanonicalSha256(File.ReadAllBytes(source));

        Assert.True(
            string.Equals(pinned, computed, StringComparison.Ordinal),
            $"IdentityVerification:PolicySha256 of appsettings.json is '{pinned}' but the canonical SHA-256 of {IdentityPolicyTestFiles.ShippedFileName} is '{computed}'. " +
            "A qualified policy file is never edited: write a new file (identity-policy-YYYY.MM-N.json) and pin its hash, with its PolicyFile.");
        Assert.True(File.Exists(output), "The policy file is not in the build output: check the IdentityPolicies items of Casazen.Web.csproj.");
        Assert.Equal(pinned, IdentityPolicyProvider.CanonicalSha256(File.ReadAllBytes(output)));
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, pinned);
    }

    [Fact]
    public void TheCommittedConfiguration_ThroughTheServicesOfTheApplication_GivesAReadyPolicy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddJsonFile(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false).Build());
        services.AddLogging();
        services.AddCasazenServices();
        using var provider = services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IdentityPolicyProvider>().Snapshot;

        Assert.True(snapshot.IsReady, $"The committed policy is unavailable: {snapshot.ReasonCode}");
        Assert.Equal("identity-policy-2026.11-1", snapshot.Policy!.Version);
        Assert.Equal(IdentityPolicyTestFiles.ShippedCanonicalHash, snapshot.Hash);
        Assert.True(snapshot.Provisional);
    }

    [Fact]
    public void WebProject_ShipsThePolicyFilesInTheBuildAndPublishOutput()
    {
        var project = XDocument.Load(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "Casazen.Web.csproj"));

        foreach (var kind in new[] { "Content", "None" })
        {
            var item = Assert.Single(project.Descendants(kind), element => (string?)element.Attribute("Update") == @"IdentityPolicies\**\*.json");
            Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToOutputDirectory"));
            Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToPublishDirectory"));
        }
    }

    [Fact]
    public void EveryPolicyFile_IsAStrictlyValidPolicy_NamedAfterItsVersion()
    {
        var files = Directory.GetFiles(IdentityPolicyTestFiles.SourceFolder(), "*.json");

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.True(IdentityConfigurationJson.TryCanonicalize(File.ReadAllBytes(file), out var canonical), $"{Path.GetFileName(file)} is not strict JSON.");
            var policy = IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!;

            Assert.Empty(IdentityPolicyProvider.Validate(policy));
            Assert.Equal(Path.GetFileNameWithoutExtension(file), policy.Version);
        }
    }

    [Fact]
    public void TheDefaultPolicy_IsProvisional_AndCopiesTodaysProvisionalDefaults()
    {
        var options = CommittedOptions();
        var file = Path.Combine(IdentityPolicyTestFiles.SourceFolder(), Path.GetFileName(options.PolicyFile));
        Assert.True(IdentityConfigurationJson.TryCanonicalize(File.ReadAllBytes(file), out var canonical));

        var policy = IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!;

        Assert.True(policy.Provisional);
        Assert.Equal((double)BiometricSettingsRules.DefaultAcceptThreshold, policy.Match.Accept);
        Assert.Equal((double)BiometricSettingsRules.DefaultEscalateThreshold, policy.Match.ReviewFloor);
        Assert.Equal(new BiometricModelOptions().LivenessThreshold, policy.Presence.Liveness.MinScore);
        Assert.Equal(1, policy.Closure.GuidedRetries);
    }

    [Fact]
    public void PolicyRecords_HoldNoNumberOfTheirOwn_NoDefaultAndNoConstant()
    {
        Type[] policyTypes =
        [
            typeof(IdentityDecisionPolicy),
            typeof(IdentityDocumentPolicy),
            typeof(IdentityPresencePolicy),
            typeof(IdentityChallengePolicy),
            typeof(IdentitySequencePolicy),
            typeof(IdentityContinuityPolicy),
            typeof(IdentityPosePolicy),
            typeof(IdentityLivenessPolicy),
            typeof(IdentityMatchPolicy),
            typeof(IdentityClosurePolicy),
        ];

        foreach (var type in policyTypes)
        {
            var constructor = Assert.Single(type.GetConstructors());
            Assert.All(constructor.GetParameters(), parameter => Assert.False(parameter.HasDefaultValue, $"{type.Name}.{parameter.Name} has a default value."));
            Assert.Empty(type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
        }
    }

    private static IdentityVerificationOptions CommittedOptions()
    {
        var options = new IdentityVerificationOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build()
            .GetSection(IdentityVerificationOptions.SectionName)
            .Bind(options);
        return options;
    }
}
