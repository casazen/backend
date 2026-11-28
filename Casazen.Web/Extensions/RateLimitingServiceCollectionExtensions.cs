using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Extensions;

/// <summary>
/// Rate limiting of the anonymous endpoints (A3-12, A3-41, A5-10, A8-22, A9-10). Every policy is a fixed window
/// partitioned by client IP (<see cref="ClientIp.GetRateLimitKey"/>, resolved by the forwarded headers middleware), so
/// one client exhausting its quota never blocks the others; the guest check-in policies are partitioned by IP and
/// token hash, and the policies of signed-in endpoints (the access request of AM-02b, the global search of UI-13a) by person.
/// A rejected request gets 429 ProblemDetails with <c>code</c> <c>rate_limited</c> and <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Limits per client IP and window (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:{Policy}:PermitLimit</c>
/// and <c>RateLimiting:{Policy}:WindowSeconds</c>. The keys used before FD-10 (e.g.
/// <c>DirectBooking:RateLimitPermitLimit</c>) still apply when the new one is not set.
/// </remarks>
public static class RateLimitingServiceCollectionExtensions
{
    public const string SectionName = "RateLimiting";

    public const string DefaultTokenRouteValue = "token";

    public const string DoorTokenRouteValue = "doorToken";

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RateLimitPolicyDefinition> Policies { get; } =
    [
        new(RateLimitPolicies.PublicRead, 120, OneMinute),
        new(RateLimitPolicies.PublicBookingCreate, 10, OneMinute, "DirectBooking:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicBookingLookup, 30, OneMinute),
        new(RateLimitPolicies.PublicGuestBookingLookup, 10, TimeSpan.FromMinutes(5)),
        new(RateLimitPolicies.PublicGuestSession, 10, TimeSpan.FromMinutes(5)),
        new(RateLimitPolicies.GuestCheckIn, 10, OneMinute, "CheckIn:RateLimitPermitLimit", PartitionByToken: true),
        new(RateLimitPolicies.GuestCheckInSubmit, 3, OneMinute, "CheckIn:SubmitRateLimitPermitLimit", PartitionByToken: true),

        new(RateLimitPolicies.GuestIdentity, 30, OneMinute, PartitionByToken: true),
        // The uploads are the pictures of the document and the sequence: a whole sitting fits, with retakes, the guided retry and the retries of the network.
        new(RateLimitPolicies.GuestIdentityUpload, 20, OneMinute, PartitionByToken: true),

        new(RateLimitPolicies.PublicDoorCheckIn, 5, OneMinute, PartitionByToken: true, TokenRouteValue: DoorTokenRouteValue),
        new(RateLimitPolicies.PublicTouristTaxCalc, 30, OneMinute, "SeoTouristTax:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicSeoEvents, 60, OneMinute),
        new(RateLimitPolicies.PublicResolveHost, 60, OneMinute, "PublicHost:RateLimitPermitLimit"),
        new(RateLimitPolicies.PublicIcal, 60, OneMinute),
        new(RateLimitPolicies.PublicRegistration, 5, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.PublicComuni, 120, OneMinute),
        new(RateLimitPolicies.PublicStaySearch, 60, OneMinute),
        new(RateLimitPolicies.PublicSupplierSlots, 60, OneMinute),
        new(RateLimitPolicies.PublicSupplierQuote, 30, OneMinute),
        new(RateLimitPolicies.PublicSupplierBookingCreate, 5, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.PublicContact, 5, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.StorefrontChatRead, 90, TimeSpan.FromMinutes(1)),
        new(RateLimitPolicies.StorefrontChatWrite, 15, TimeSpan.FromMinutes(1)),
        new(RateLimitPolicies.PublicInvitationLookup, 20, OneMinute),
        new(RateLimitPolicies.PublicReviewSubmit, 10, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.GuestMessageSend, 30, TimeSpan.FromMinutes(10)),

        new(RateLimitPolicies.GuestBookingCancel, 10, TimeSpan.FromMinutes(10)),

        // A policy of a signed-in endpoint: partitioned by person, since the request emails other people (AM-02b).
        new(RateLimitPolicies.OrgAccessRequest, 5, TimeSpan.FromMinutes(10), PartitionByUser: true),

        // The global search of the palette (UI-13a): per signed-in person as well, since the palette asks at every pause in typing.
        new(RateLimitPolicies.GlobalSearch, 60, OneMinute, PartitionByUser: true),

        new(RateLimitPolicies.HostSupplierSlots, 60, OneMinute, PartitionByUser: true),

        // The host answering a guest (DB-14a): per signed-in person as well, since the answer is an email to somebody else.
        new(RateLimitPolicies.HostMessageSend, 60, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.TenantPrivacy, 10, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.RentBankImport, 30, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.OnboardingBulkImport, 30, TimeSpan.FromMinutes(10), PartitionByUser: true),

        // The autosave of the showcase editor (SP-13a): per signed-in person as well, since the editor saves at every pause in typing.
        new(RateLimitPolicies.SupplierShowcaseSave, 120, OneMinute, PartitionByUser: true),

        new(RateLimitPolicies.SupplierShowcaseMedia, 40, TimeSpan.FromMinutes(10), PartitionByUser: true),

        // The signed-in guest of the mobile app (GuestAccountController): per person, since the people of one network share an address.
        // The reads at every opening of a screen and pull to refresh; the session is minted when the guest enters the cancellation or the
        // messages of a booking and lasts hours; the deletion cannot be taken back, and a few tries leave room for the retry after a 502.
        new(RateLimitPolicies.GuestAccountRead, 60, OneMinute, PartitionByUser: true),
        new(RateLimitPolicies.GuestAccountSession, 20, TimeSpan.FromMinutes(10), PartitionByUser: true),
        new(RateLimitPolicies.GuestAccountDelete, 5, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.PublicMarketingConsent, 60, TimeSpan.FromMinutes(10)),

        new(RateLimitPolicies.AssistantSimulate, 20, TimeSpan.FromHours(1), PartitionByUser: true),

        // The owner portal operations (E07/F07/US01): per signed-in person, since an owner is a person outside the organization and the people of one network
        // share an address. The reads and light calls; the invitation of a contact (an e-mail to an address the owner chose); what writes and stores a file (a
        // message, a document); the download of a file (a line of audit each).
        new(RateLimitPolicies.OwnerPortalRead, 120, OneMinute, PartitionByUser: true),
        new(RateLimitPolicies.OwnerPortalInvite, 10, TimeSpan.FromMinutes(10), PartitionByUser: true),
        new(RateLimitPolicies.OwnerPortalWrite, 30, TimeSpan.FromMinutes(10), PartitionByUser: true),
        new(RateLimitPolicies.OwnerPortalDownload, 60, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.PublicFiscalDocumentDownload, 20, OneMinute),

        new(RateLimitPolicies.PublicAccountantPackage, 20, TimeSpan.FromMinutes(10)),

        // «Send a test» of the package to the accountant (E15/F01/US02/T02): per signed-in person, since each call makes a package and sends an e-mail to a third party.
        new(RateLimitPolicies.AccountantPackageSend, 5, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.GuestPortal, 120, OneMinute, PartitionByToken: true),
        new(RateLimitPolicies.GuestPortalWrite, 30, OneMinute, PartitionByToken: true),

        // The agency's retry of the owner sign-in on its own domain (E40/F01/US02/T01): per signed-in person, since each call may read and write the Auth0 tenant that every host shares.
        new(RateLimitPolicies.OwnerLoginRetry, 5, TimeSpan.FromMinutes(10), PartitionByUser: true),

        new(RateLimitPolicies.SiteConnectAuthorize, 10, TimeSpan.FromMinutes(10), PartitionByUser: true),
        new(RateLimitPolicies.SiteConnectToken, 20, TimeSpan.FromMinutes(10)),
        new(RateLimitPolicies.SiteApi, 120, OneMinute, PartitionBySiteConnection: true),

        new(RateLimitPolicies.SiteDomainVerify, 20, TimeSpan.FromMinutes(10), PartitionBySiteConnection: true),
    ];

    public static IServiceCollection AddCasazenRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });

        services.AddSingleton<AiRequestRateLimiter>();

        services.AddSingleton<GuestBookingEmailRateLimiter>();

        services.AddSingleton<GuestSessionCodeRateLimiter>();

        services.AddSingleton<GuestMessageSessionRateLimiter>();

        services.AddSingleton<GuestBookingCancelRateLimiter>();

        services.AddSingleton<SupplierBookingEmailRateLimiter>();

        services.AddSingleton<HostContactEmailRateLimiter>();

        services.AddSingleton<DoorCheckInIpRateLimiter>();

        services.AddSingleton<GuestPortalIpRateLimiter>();

        services.AddSingleton<SupplierBookingManageEmailRateLimiter>();

        services.AddOptions<RateLimiterOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var windows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
                foreach (var definition in Policies)
                {
                    var limiterOptions = ResolveOptions(definition, configuration);
                    windows[definition.Name] = limiterOptions.Window;

                    var partitionByToken = definition.PartitionByToken;
                    var partitionByUser = definition.PartitionByUser;
                    var tokenRouteValue = definition.TokenRouteValue;
                    var partitionBySiteConnection = definition.PartitionBySiteConnection;
                    options.AddPolicy(definition.Name, httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            GetPartitionKey(httpContext, partitionByToken, partitionByUser, tokenRouteValue, partitionBySiteConnection),
                            _ => limiterOptions));
                }

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, cancellationToken) =>
                    WriteRejectionAsync(context, windows, cancellationToken);
            });

        return services;
    }

    public static FixedWindowRateLimiterOptions ResolveOptions(RateLimitPolicyDefinition definition, IConfiguration configuration)
    {
        var section = configuration.GetSection($"{SectionName}:{definition.Name}");
        var permitLimit = section.GetValue<int?>("PermitLimit")
            ?? (definition.LegacyPermitLimitKey is null ? null : configuration.GetValue<int?>(definition.LegacyPermitLimitKey))
            ?? definition.DefaultPermitLimit;
        var windowSeconds = section.GetValue<int?>("WindowSeconds") ?? (int)definition.DefaultWindow.TotalSeconds;

        if (permitLimit < 1 || windowSeconds < 1)
        {
            throw new InvalidOperationException(
                $"Rate limit policy {definition.Name}: PermitLimit and WindowSeconds must be at least 1 " +
                $"(were {permitLimit} and {windowSeconds}).");
        }

        return new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
    }

    /// <summary>
    /// Client IP partition, plus a hash of the <c>{token}</c> route value (the one named by <paramref name="tokenRouteValue"/>) when the policy asks for it. A
    /// policy partitioned by person (<paramref name="partitionByUser"/>) counts the signed-in account instead, whatever the network it comes from;
    /// a caller with no account falls back to its IP. A policy partitioned by site (<paramref name="partitionBySiteConnection"/>, the plugin's calls) counts
    /// the connection of the credential of the scheme <c>SiteToken</c>, which the limiter sees after authorization; without one it falls back to the IP too.
    /// </summary>
    public static string GetPartitionKey(
        HttpContext httpContext,
        bool partitionByToken,
        bool partitionByUser = false,
        string tokenRouteValue = DefaultTokenRouteValue,
        bool partitionBySiteConnection = false)
    {
        if (partitionBySiteConnection && httpContext.User.GetSiteConnection() is { } site)
            return "site:" + site.ConnectionId.ToString("N");

        if (partitionByUser && httpContext.User.GetUserId() is { Length: > 0 } userId)
        {
            return "user:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)), 0, 16);
        }

        var ipKey = ClientIp.GetRateLimitKey(httpContext);
        if (!partitionByToken
            || httpContext.Request.RouteValues[tokenRouteValue]?.ToString() is not { Length: > 0 } token)
        {
            return ipKey;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 16);
        return $"{ipKey}|{hash}";
    }

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        IReadOnlyDictionary<string, TimeSpan> windows,
        CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var policy = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var leaseRetryAfter)
            ? leaseRetryAfter
            : policy is not null && windows.TryGetValue(policy, out var window) ? window : OneMinute;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<PrivateAnswerAttribute>() is not null)
            PrivateAnswerAttribute.Apply(httpContext.Response);

        var problem = ApiProblemDetails.Create(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemCodes.RateLimited,
            "RateLimitedDetail",
            [retryAfterSeconds]);
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingServiceCollectionExtensions).FullName!)
            .LogWarning(
                "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
                policy ?? "(none)",
                httpContext.Request.Method,
                (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
                ApiProblemDetails.GetTraceId(httpContext));

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            JsonOptions,
            ApiProblemDetails.ContentType,
            cancellationToken);
    }
}

/// <param name="Name">Policy name (<see cref="RateLimitPolicies"/>).</param>
/// <param name="DefaultPermitLimit">Requests per client IP (and token) per window when not configured.</param>
/// <param name="DefaultWindow">Fixed window length when not configured.</param>
/// <param name="LegacyPermitLimitKey">Configuration key of the limit used before FD-10, still honoured.</param>
/// <param name="PartitionByToken">Also partition by the hash of the <c>{token}</c> route value.</param>
/// <param name="PartitionByUser">Partition by the signed-in account instead of the client IP (a signed-in endpoint only).</param>
/// <param name="TokenRouteValue">The route value <paramref name="PartitionByToken"/> hashes: <c>token</c>, or <c>doorToken</c> for the door of a property.</param>
/// <param name="PartitionBySiteConnection">Partition by the connected site of the credential (the plugin's calls, scheme <c>SiteToken</c>) instead of the client IP.</param>
public sealed record RateLimitPolicyDefinition(
    string Name,
    int DefaultPermitLimit,
    TimeSpan DefaultWindow,
    string? LegacyPermitLimitKey = null,
    bool PartitionByToken = false,
    bool PartitionByUser = false,
    string TokenRouteValue = RateLimitingServiceCollectionExtensions.DefaultTokenRouteValue,
    bool PartitionBySiteConnection = false);
