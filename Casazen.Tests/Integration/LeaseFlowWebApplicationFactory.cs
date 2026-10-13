using Casazen.Core.Services;
using Casazen.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// Lease full-flow factory. RLI (LT-01): <c>Features:RliProvider</c> keeps its default (off), so leases are registered
/// manually; the provider is a <see cref="FakeLeaseRegistrationProvider"/> that counts calls, to prove none happens.
/// APE is stubbed on the base factory. Contract templates (LT-03): only <c>CedolareSecca</c> has an approved template,
/// the test fixture <c>Fixtures/LeaseTemplates/CedolareSecca/test-fixture-v1.md</c> (test texts, not legal clauses);
/// the other regimes keep the committed default (not approved). Signature (LT-02): <c>Features:ESignProvider</c> keeps
/// its default (off), so contracts are signed offline; the e-sign provider is a <see cref="FakeLeaseESignProvider"/>
/// that counts calls, to prove none happens.
/// </summary>
public class LeaseFlowWebApplicationFactory : CasazenWebApplicationFactory
{
    public const string ApprovedFixtureVersion = "test-fixture-v1";

    public FakeLeaseRegistrationProvider RegistrationProvider { get; } = new();

    public FakeLeaseESignProvider ESignProvider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LeaseTemplates:TemplatesDirectory"] = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LeaseTemplates"),
                ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = ApprovedFixtureVersion,
                ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "true",
                ["LeaseTemplates:Variants:CedolareSecca:ApprovalReference"] = "integration test fixture",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<ILeaseRegistrationProvider>(services);
            services.AddSingleton<ILeaseRegistrationProvider>(RegistrationProvider);
            RemoveAllOf<ILeaseESignService>(services);
            services.AddSingleton<ILeaseESignService>(ESignProvider);
        });
    }
}

/// <summary>Lease flow with the RLI provider path on (<c>Features:RliProvider</c>) and the configured fake provider.</summary>
public class LeaseProviderFlowWebApplicationFactory : LeaseFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:RliProvider"] = "true" }));
    }
}

/// <summary>
/// Lease flow with the e-signature provider path on (<c>Features:ESignProvider</c>) and the configured fake provider
/// (LT-02). The webhook secret is the base factory's <c>esign-test-secret</c>.
/// </summary>
public class LeaseESignProviderFlowWebApplicationFactory : LeaseFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:ESignProvider"] = "true" }));
    }
}
