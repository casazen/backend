using Casazen.Infrastructure.Payments;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Stripe mode and plan prices of this environment, checked at startup (PL-11, A1-31). Runbook:
/// <c>docs/runbooks/stripe.md</c> § Environments.
/// <list type="bullet">
/// <item>Production takes only live keys; every other environment (Staging = the Railway test environment, Development,
/// Testing) only test keys. A key of the other mode stops the startup: test payments in production, or real charges from
/// the test environment.</item>
/// <item>In Production, once <c>Stripe__SecretKey</c> is set, every plan of the catalogue needs its Stripe Price id
/// (<c>Billing__Prices__&lt;Tier&gt;</c>, <see cref="BillingPrices"/>) and no two plans share one: a plan offered but not
/// payable stops the startup. Elsewhere such a plan is only not purchasable (422 <c>billing_plan_unavailable</c>) and
/// <c>/api/health/ready</c> reports <c>stripe: degraded</c>.</item>
/// </list>
/// Keys still missing or placeholders are not checked here: payments stay optional (FD-12), the health check reports
/// them. The return URLs of Checkout and portal come from <c>App__PublicSiteBaseUrl</c>, already required outside
/// Development and Testing.
/// </summary>
public static class BillingConfiguration
{
    private static readonly (string Key, string Variable)[] StripeKeys =
    [
        ("Stripe:SecretKey", "Stripe__SecretKey"),
        ("Stripe:PublishableKey", "Stripe__PublishableKey"),
    ];

    /// <summary>Registers the startup validation of the billing configuration (every environment).</summary>
    public static IServiceCollection AddCasazenBillingConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<BillingConfigurationOptions>().ValidateOnStart();
        services.AddSingleton<IValidateOptions<BillingConfigurationOptions>>(
            new BillingConfigurationValidator(configuration, environment));
        return services;
    }

    /// <summary>Problems that stop the startup, naming variables and never values.</summary>
    public static IReadOnlyList<string> GetStartupErrors(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = new List<string>();
        var production = environment.IsProduction();

        foreach (var (key, variable) in StripeKeys)
        {
            var value = configuration[key];
            if (RequiredConfiguration.IsMissing(value))
                continue;

            var mode = StripeKeyModes.Of(value);
            if (production && mode == StripeKeyMode.Test)
            {
                errors.Add(
                    $"{variable} is a Stripe test-mode key but ASPNETCORE_ENVIRONMENT is Production: set the live-mode " +
                    "keys, or ASPNETCORE_ENVIRONMENT=Staging if this is the test environment (docs/runbooks/stripe.md).");
            }
            else if (!production && mode == StripeKeyMode.Live)
            {
                errors.Add(
                    $"{variable} is a Stripe live-mode key but ASPNETCORE_ENVIRONMENT is {environment.EnvironmentName}: " +
                    "live keys only in Production, use the test-mode keys here (docs/runbooks/stripe.md).");
            }
        }

        if (production && !RequiredConfiguration.IsMissing(configuration["Stripe:SecretKey"]))
        {
            errors.AddRange(BillingPrices.GetProblems(configuration).Select(problem =>
                $"Billing: {problem} Every plan offered must be payable in Production (docs/runbooks/stripe.md)."));
        }

        return errors;
    }
}

/// <summary>
/// Marker of the billing startup validation (<see cref="BillingConfiguration"/>): the checks read the Stripe and
/// <c>Billing</c> sections directly, so the options carry no value.
/// </summary>
public sealed class BillingConfigurationOptions;

/// <summary>Runs <see cref="BillingConfiguration.GetStartupErrors"/> with <c>ValidateOnStart</c>.</summary>
public sealed class BillingConfigurationValidator(IConfiguration configuration, IHostEnvironment environment)
    : IValidateOptions<BillingConfigurationOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingConfigurationOptions options)
    {
        var errors = BillingConfiguration.GetStartupErrors(configuration, environment);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
