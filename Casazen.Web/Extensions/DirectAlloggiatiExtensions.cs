using Hangfire;
using Casazen.Web.BackgroundJobs;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;

namespace Casazen.Web.Extensions;

public static class DirectAlloggiatiExtensions
{
    public static void ConfigureJobs(IRecurringJobManager jobs, bool live)
    {
        if (live)
        {
            jobs.AddOrUpdate<DirectAlloggiatiJob>("alloggiati-direct-send", job => job.DispatchAsync(CancellationToken.None),
                "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            jobs.AddOrUpdate<DirectAlloggiatiJob>("alloggiati-direct-receipts", job => job.ReceiptsAsync(CancellationToken.None),
                Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
        else
        {
            jobs.RemoveIfExists("alloggiati-direct-send");
            jobs.RemoveIfExists("alloggiati-direct-receipts");
        }
    }

    public static IServiceCollection AddDirectAlloggiati(this IServiceCollection services, bool live)
    {
        services.AddSingleton(new AlloggiatiTransportEnvironment(live));
        services.AddHttpClient<DirectAlloggiatiSoap>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
            client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<IDirectAlloggiatiAccount, DirectAlloggiatiAccount>();
        services.AddScoped<DirectAlloggiatiDispatch>();
        services.AddScoped<DirectAlloggiatiReceipts>();
        return services;
    }
}
