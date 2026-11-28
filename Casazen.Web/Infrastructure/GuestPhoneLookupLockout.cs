using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Infrastructure;

public sealed class GuestPhoneLookupLockoutOptions
{
    public const string MaxFailuresKey = "GuestArea:PhoneLockout:MaxFailures";

    public const string WindowMinutesKey = "GuestArea:PhoneLockout:WindowMinutes";

    public const int DefaultMaxFailures = 5;
    public const int DefaultWindowMinutes = 60;

    public const int DefaultMaxTrackedCodes = 100_000;

    public int MaxFailures { get; set; } = DefaultMaxFailures;

    public int WindowMinutes { get; set; } = DefaultWindowMinutes;

    public int MaxTrackedCodes { get; set; } = DefaultMaxTrackedCodes;

    public bool IsValid() => MaxFailures >= 1 && WindowMinutes >= 1 && MaxTrackedCodes >= 1;

    public static void Read(GuestPhoneLookupLockoutOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        options.MaxFailures = configuration.GetValue(MaxFailuresKey, DefaultMaxFailures);
        options.WindowMinutes = configuration.GetValue(WindowMinutesKey, DefaultWindowMinutes);
    }
}

/// <summary>
/// The lock of a booking code for the guest session proved with the last four digits of the phone (NF-143), next to the per-IP policy
/// <see cref="RateLimitPolicies.PublicGuestSession"/> and the per-code limit <see cref="GuestSessionCodeRateLimiter"/>. Four digits have 10,000
/// values: <see cref="MaxFailures"/> failed attempts (5) on one code within its window (<see cref="Window"/>, 60 minutes) lock the code for the rest
/// of the window, whatever the IP and even with the right digits; a match clears the count. Only the phone proof is counted and locked: the e-mail
/// proof keeps its own limits.
/// </summary>
/// <remarks>
/// <para><b>Blind to the existence of a code.</b> The key is computed from what the guest typed, before any lookup: a SHA-256 hash of the code in its
/// stored form (<see cref="BookingCodes.TryNormalize"/>), or of the typed text when it is not a code; never the code, never the digits. A real code
/// and a made-up one count and lock alike.</para>
/// <para><b>Counted when it starts.</b> Every attempt counts as failed the moment it is let through and a match takes the count back to zero, so a
/// burst of parallel attempts cannot get past the lock.</para>
/// <para><b>Bounded memory.</b> Counters live in memory, per replica, like the other limits of the guests: a size-limited <see cref="MemoryCache"/>
/// (<see cref="MaxTrackedCodes"/> entries) whose entries expire with their window, so a flood of made-up codes cannot grow it. When it is full a new
/// code is refused with the same 429, never let through uncounted, until room is made. The cache makes room by dropping the entries whose window is
/// over and then the least recently used ones, so a flood of <see cref="MaxTrackedCodes"/> distinct codes within one window (at the default per-IP
/// limit, at least 833 client addresses for an hour) can make it forget a lock that is still running and win 5 more attempts on one code: a known
/// limit of the design.</para>
/// </remarks>
public sealed class GuestPhoneLookupLockout : IDisposable
{
    public const string PolicyName = "GuestPhoneLookupLockout";

    private readonly MemoryCache _codes;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    public GuestPhoneLookupLockout(IOptions<GuestPhoneLookupLockoutOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        if (!settings.IsValid())
        {
            throw new InvalidOperationException(
                $"{GuestPhoneLookupLockoutOptions.MaxFailuresKey} and {GuestPhoneLookupLockoutOptions.WindowMinutesKey} must be at least 1.");
        }

        MaxFailures = settings.MaxFailures;
        Window = TimeSpan.FromMinutes(settings.WindowMinutes);
        MaxTrackedCodes = settings.MaxTrackedCodes;
        _clock = timeProvider ?? TimeProvider.System;
        _codes = new MemoryCache(new MemoryCacheOptions { SizeLimit = settings.MaxTrackedCodes });
    }

    public int MaxFailures { get; }

    /// <summary>The window of a code; the <c>Retry-After</c> of a lock always names all of it.</summary>
    public TimeSpan Window { get; }

    public int MaxTrackedCodes { get; }

    public int TrackedCodes => _codes.Count;

    public static string KeyOf(string bookingCode)
    {
        ArgumentNullException.ThrowIfNull(bookingCode);
        var normalized = BookingCodes.TryNormalize(bookingCode, out var code) ? code : bookingCode.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public bool TryAcquire(string bookingCode)
    {
        var key = KeyOf(bookingCode);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_codes.TryGetValue(key, out Attempts? attempts) && attempts is not null)
            {
                if (attempts.WindowEndsAt > now)
                {
                    if (attempts.Failures >= MaxFailures)
                        return false;

                    attempts.Failures++;
                    return true;
                }

                _codes.Remove(key);
            }

            using (var entry = _codes.CreateEntry(key))
            {
                entry.Value = new Attempts(now + Window) { Failures = 1 };
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = Window;
            }

            // A full store does not keep the new entry: the attempt is refused rather than let through uncounted.
            return _codes.TryGetValue(key, out _);
        }
    }

    public void Reset(string bookingCode)
    {
        var key = KeyOf(bookingCode);
        lock (_gate)
            _codes.Remove(key);
    }

    public void Dispose() => _codes.Dispose();

    private sealed class Attempts(DateTimeOffset windowEndsAt)
    {
        public DateTimeOffset WindowEndsAt { get; } = windowEndsAt;

        public int Failures { get; set; }
    }
}

/// <summary>
/// Puts the opening of a guest session with the digits of the phone under <see cref="GuestPhoneLookupLockout"/>: a locked code answers 429
/// <c>rate_limited</c> with a <c>Retry-After</c> that names the whole window (never the time left of it, which would tell when the first attempt was
/// made), and the booking is not read. Runs after the model validation and after the other limits of the action (its order is 1), so only an attempt
/// that reaches the lookup is counted. A body with the e-mail proof, or any body while the flag <c>WhiteLabelHost</c> is off, is not counted.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestPhoneLookupLockoutAttribute : TypeFilterAttribute
{
    public GuestPhoneLookupLockoutAttribute()
        : base(typeof(GuestPhoneLookupLockoutFilter))
    {
        Order = 1;
    }
}

public sealed class GuestPhoneLookupLockoutFilter(
    GuestPhoneLookupLockout lockout,
    IFeatureFlags featureFlags,
    ILogger<GuestPhoneLookupLockoutFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var request = context.ActionArguments.Values.OfType<GuestSessionRequest>().FirstOrDefault();
        if (request is null || !request.UsesPhoneProof(featureFlags))
        {
            await next();
            return;
        }

        if (!lockout.TryAcquire(request.BookingCode))
        {
            var httpContext = context.HttpContext;

            logger.LogWarning(
                "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
                GuestPhoneLookupLockout.PolicyName,
                httpContext.Request.Method,
                (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
                ApiProblemDetails.GetTraceId(httpContext));
            context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, lockout.Window);
            return;
        }

        var executed = await next();
        if (executed.Exception is null && !executed.Canceled)
            lockout.Reset(request.BookingCode);
    }
}
