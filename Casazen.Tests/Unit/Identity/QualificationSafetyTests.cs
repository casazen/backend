using Casazen.Core.Services;
using Casazen.Tests.Qualification;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US02 (NF-198, D5, ADR-013-R2, R21): the guards of the qualification toolkit that keep real faces and real documents where they belong, in the process and out of the repository. The
/// output folder of a qualification is never a folder of the committed policy files nor a folder of the checkout (the repository is public, and the results are the scores of a real dataset),
/// and the pipeline registers nothing that could carry a picture out of the process: no HTTP client factory, no AI provider, no job client. These tests need no model and no dataset.
/// </summary>
public sealed class QualificationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "casazen-qualification-safety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (string Checkout, string BaseDirectory) NewCheckout()
    {
        var checkout = Path.Combine(_root, "checkout");
        var baseDirectory = Path.Combine(checkout, "Casazen.Tests", "bin", "Release", "net10.0");
        Directory.CreateDirectory(Path.Combine(checkout, "Casazen.Web", "IdentityPolicies"));
        Directory.CreateDirectory(Path.Combine(baseDirectory, "IdentityPolicies"));
        File.WriteAllText(Path.Combine(checkout, "Casazen.sln"), "placeholder");
        return (checkout, baseDirectory);
    }

    private static string Detail(Action refused)
    {
        var refusal = Assert.Throws<QualificationRunException>(refused);
        Assert.Equal(QualificationRunCodes.OutputFolderInvalid, refusal.Code);
        return refusal.Message;
    }

    [Fact]
    public void ProtectedPolicyFolders_AreTheFolderTheApplicationReadsAndTheSourcesOfTheCheckout()
    {
        var (checkout, baseDirectory) = NewCheckout();

        var folders = QualificationOutputFolder.ProtectedPolicyFolders("IdentityPolicies/identity-policy-2026.11-1.json", baseDirectory);

        Assert.Equal([Path.Combine(baseDirectory, "IdentityPolicies"), Path.Combine(checkout, "Casazen.Web", "IdentityPolicies")], folders);

        var elsewhere = Path.Combine(_root, "elsewhere", "policy.json");
        Assert.Equal(Path.Combine(_root, "elsewhere"), QualificationOutputFolder.ProtectedPolicyFolders(elsewhere, baseDirectory)[0]);
        var copy = QualificationOutputFolder.ProtectedPolicyFolders("IdentityPolicies/identity-policy-2026.11-1.json", Path.Combine(_root, "copy"));
        Assert.Equal([Path.Combine(_root, "copy", "IdentityPolicies")], copy);
    }

    [Fact]
    public void CheckoutRoot_IsTheFirstFolderAboveTheTestOutputThatHoldsTheSolution_OrNothing()
    {
        var (checkout, baseDirectory) = NewCheckout();

        Assert.Equal(checkout, QualificationOutputFolder.CheckoutRoot(baseDirectory));
        Assert.Equal(checkout, QualificationOutputFolder.CheckoutRoot(checkout));
        Assert.Null(QualificationOutputFolder.CheckoutRoot(Path.Combine(_root, "copy")));
    }

    [Fact]
    public void EnsureAllowed_RefusesTheFolderOfThePolicyFilesAndTheCheckout_AndCreatesNothing()
    {
        var (checkout, baseDirectory) = NewCheckout();
        var published = Path.Combine(baseDirectory, "IdentityPolicies");
        var sources = Path.Combine(checkout, "Casazen.Web", "IdentityPolicies");
        var protectedFolders = QualificationOutputFolder.ProtectedPolicyFolders("IdentityPolicies/identity-policy-2026.11-1.json", baseDirectory);

        foreach (var path in new[] { published, sources, published + Path.DirectorySeparatorChar, Path.Combine(published, "..", "IdentityPolicies"), Path.Combine(sources, "proposals") })
            Assert.EndsWith(QualificationOutputFolder.PolicyFolderDetail, Detail(() => QualificationOutputFolder.EnsureAllowed(path, protectedFolders, checkout)), StringComparison.Ordinal);

        foreach (var path in new[] { checkout, Path.Combine(checkout, "out"), Path.Combine(baseDirectory, "results"), Path.Combine(checkout, "Casazen.Tests", "..", "out") })
            Assert.EndsWith(QualificationOutputFolder.CheckoutDetail, Detail(() => QualificationOutputFolder.EnsureAllowed(path, protectedFolders, checkout)), StringComparison.Ordinal);

        var sibling = checkout + "-results";
        QualificationOutputFolder.EnsureAllowed(sibling, protectedFolders, checkout);
        QualificationOutputFolder.EnsureAllowed(Path.Combine(_root, "qa", "out"), protectedFolders, checkout);
        QualificationOutputFolder.EnsureAllowed(Path.Combine(checkout, "out"), [], null);
        Assert.False(Directory.Exists(sibling));
        Assert.False(Directory.Exists(Path.Combine(checkout, "out")));
        Assert.False(Directory.Exists(Path.Combine(_root, "qa")));
    }

    [Fact]
    public void IsInside_IsTheFolderOrWhatLiesUnderIt_NotAFolderThatOnlySharesItsName()
    {
        var folder = Path.Combine(_root, "policies");

        Assert.True(QualificationOutputFolder.IsInside(folder, folder));
        Assert.True(QualificationOutputFolder.IsInside(folder + Path.DirectorySeparatorChar, folder));
        Assert.True(QualificationOutputFolder.IsInside(Path.Combine(folder, "a", "b"), folder));
        Assert.True(QualificationOutputFolder.IsInside(Path.Combine(folder, "a", "..", "b"), folder));
        Assert.False(QualificationOutputFolder.IsInside(folder + "-new", folder));
        Assert.False(QualificationOutputFolder.IsInside(Path.Combine(folder, "..", "other"), folder));
        Assert.False(QualificationOutputFolder.IsInside(_root, folder));
    }

    [Fact]
    public void QualificationPipeline_RegistersNoHttpClient_NoAiProvider_AndNoJobClient()
    {
        Type[] forbidden = [typeof(IHttpClientFactory), typeof(HttpClient), typeof(HttpMessageHandler), typeof(IAiProvider), typeof(IAiRuntimeSettings), typeof(IBackgroundJobClient), typeof(IRecurringJobManager)];
        bool Offends(Type? type) => type is not null && forbidden.Any(candidate => candidate.IsAssignableFrom(type));

        var services = QualificationPipeline.Describe(new QualificationPipelineOptions { IncludeEnvironmentVariables = false });

        Assert.NotEmpty(services);
        var offending = services
            .Where(descriptor => Offends(descriptor.ServiceType) || Offends(descriptor.ImplementationType) || Offends(descriptor.ImplementationInstance?.GetType()))
            .Select(descriptor => descriptor.ServiceType.FullName)
            .ToList();
        Assert.True(offending.Count == 0, "The pipeline of a qualification registers a way for a picture to leave the process: " + string.Join(", ", offending));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.All(forbidden, type => Assert.Null(provider.GetService(type)));
    }

    [Fact]
    public void QualificationPipeline_TheEgressCheck_FindsAnHttpClientFactoryWhereOneIsRegistered()
    {
        var services = QualificationPipeline.Describe(new QualificationPipelineOptions { IncludeEnvironmentVariables = false, ConfigureServices = collection => collection.AddHttpClient() });

        Assert.Contains(services, descriptor => typeof(IHttpClientFactory).IsAssignableFrom(descriptor.ServiceType));
    }
}
