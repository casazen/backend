using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Options;
using Resend;

namespace Casazen.Web.Extensions;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// The single email pipeline: typed <see cref="EmailOptions"/> (section <c>Email</c>), links from
    /// <c>App:PublicSiteBaseUrl</c>, the Resend sender and the Hangfire queue. Outside Development and Testing the
    /// configuration is validated at startup, so a missing sender, API key or public URL stops the deploy with a clear
    /// error instead of losing every email (runbook <c>docs/runbooks/email.md</c>).
    /// </summary>
    public static IServiceCollection AddCasazenEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var emailOptions = services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .PostConfigure(options =>
            {
                // Railway may still carry the old variable name (Email__ResendApiKey).
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                    options.ApiKey = configuration[EmailOptions.LegacyResendApiKeyKey];
            });
        // The public URL of the web app (App__PublicSiteBaseUrl, alias Seo__PublicBaseUrl) is shared with the SEO pages,
        // the sitemap and CORS: one domain, no default in code (decision D3, runbook docs/runbooks/seo-domain.md).
        var publicSiteOptions = services.AddOptions<PublicSiteOptions>()
            .Bind(configuration.GetSection(PublicSiteOptions.SectionName))
            .PostConfigure(options => options.ApplySeoAlias(configuration));

        if (EmailConfigurationPolicy.RequiresCompleteConfiguration(environment))
        {
            services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
            services.AddSingleton<IValidateOptions<PublicSiteOptions>, PublicSiteOptionsValidator>();
            emailOptions.ValidateOnStart();
            publicSiteOptions.ValidateOnStart();
        }

        services.AddOptions<ResendClientOptions>()
            .Configure<IOptions<EmailOptions>>((resend, email) =>
            {
                resend.ApiToken = email.Value.ApiKey?.Trim() ?? string.Empty;
                resend.ThrowExceptions = false;
            });
        services.AddHttpClient<IResend, ResendClient>(client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddScoped<IEmailService, ResendEmailService>();
        services.AddSingleton<PublicSiteLinks>();
        services.AddScoped<IEmailQueue, HangfireEmailQueue>();
        services.AddScoped<EmailDeliveryJob>();
        // Check-in link email with its outcome recorded on the session (CO-09).
        services.AddScoped<IGuestCheckInLinkEmailQueue, GuestCheckInLinkEmailQueue>();
        services.AddScoped<GuestCheckInLinkEmailJob>();

        return services;
    }
}
