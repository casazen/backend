using Casazen.Core.Services;
using Casazen.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// Lease full-flow factory: confirming RLI stub so poll can reach Registered.
/// APE is stubbed on the base factory. Contract templates (LT-03): only <c>CedolareSecca</c> has an approved template,
/// the test fixture <c>Fixtures/LeaseTemplates/CedolareSecca/test-fixture-v1.md</c> (test texts, not legal clauses);
/// the other regimes keep the committed default (not approved).
/// </summary>
public class LeaseFlowWebApplicationFactory : CasazenWebApplicationFactory
{
    public const string ApprovedFixtureVersion = "test-fixture-v1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rli:FilingEnabled"] = "true",
                ["LeaseTemplates:TemplatesDirectory"] = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LeaseTemplates"),
                ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = ApprovedFixtureVersion,
                ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "true",
                ["LeaseTemplates:Variants:CedolareSecca:ApprovalReference"] = "integration test fixture",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<ILeaseRegistrationService>(services);
            services.AddSingleton<ILeaseRegistrationService, ConfirmingLeaseRegistrationService>();
        });
    }
}
