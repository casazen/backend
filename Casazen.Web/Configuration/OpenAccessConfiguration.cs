using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Startup check and startup log of the "accesso aperto" settings (BL-01, <see cref="OpenAccess"/>), in every environment.
/// A value that is not valid stops the startup with a message naming the variable: the container exits and Railway keeps
/// the previous deployment, so a typo in <c>Entitlement__OpenAccess__Enabled</c> or <c>Entitlement__OpenAccess__Tier</c>
/// never silently opens, nor silently closes, the access. With the settings missing (the default) the app starts as it did.
/// Runbook: <c>docs/runbooks/open-access.md</c>.
/// </summary>
public static class OpenAccessConfiguration
{
    public static IServiceCollection AddCasazenOpenAccessConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OpenAccessConfigurationOptions>().ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpenAccessConfigurationOptions>>(new OpenAccessConfigurationValidator(configuration));
        return services;
    }

    public static WebApplication LogOpenAccess(this WebApplication app)
    {
        var setting = OpenAccess.Read(app.Configuration);
        if (setting.Enabled)
        {
            app.Logger.LogWarning(
                "Open access is ON ({EnabledVariable}): every org is served as {Tier} at least, whatever its subscription. " +
                "Prices, stored plans and Stripe are not touched. Set it to false to go back to the paid plans ({Runbook}).",
                OpenAccess.EnabledVariable, setting.Tier, OpenAccess.Runbook);
        }

        return app;
    }
}

public sealed class OpenAccessConfigurationOptions;

public sealed class OpenAccessConfigurationValidator(IConfiguration configuration) : IValidateOptions<OpenAccessConfigurationOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAccessConfigurationOptions options)
    {
        var errors = OpenAccess.GetErrors(configuration);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
