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

/// <summary>
/// The settings of <see cref="GuestPhoneLookupLockout"/>: <c>GuestArea:PhoneLockout:MaxFailures</c> (5) and <c>GuestArea:PhoneLockout:WindowMinutes</c>
/// (60), each at least 1, checked at startup (runbook <c>docs/runbooks/direct-booking.md</c> § 8.4).
/// </summary>
public sealed class GuestPhoneLookupLockoutOptions
{
    /// <summary>Configuration key of the failed attempts that lock a code.</summary>
    public const string MaxFailuresKey = "GuestArea:PhoneLockout:MaxFailures";

    /// <summary>Configuration key of the length of the window of a code, in minutes.</summary>
    public const string WindowMinutesKey = "GuestArea:PhoneLockout:WindowMinutes";

    public const int DefaultMaxFailures = 5;
    public const int DefaultWindowMinutes = 60;

    /// <summary>The most codes followed at once: the bound of the memory of the lockout (one entry per code), not a setting.</summary>
    public const int DefaultMaxTrackedCodes = 100_000;

    /// <summary>Failed attempts with the digits of the phone on one code, within its window, that lock the code.</summary>
    public int MaxFailures { get; set; } = DefaultMaxFailures;

    /// <summary>Length of the window of a code, in minutes: it starts with the first attempt on the code, and a lock lasts until it ends.</summary>
    public int WindowMinutes { get; set; } = DefaultWindowMinutes;

    /// <summary>The most codes the lockout follows at once. Not read from the configuration.</summary>
    public int MaxTrackedCodes { get; set; } = DefaultMaxTrackedCodes;

    /// <summary>True when every value is at least 1.</summary>
    public bool IsValid() => MaxFailures >= 1 && WindowMinutes >= 1 && MaxTrackedCodes >= 1;

    /// <summary>Fills <paramref name="options"/> from <paramref name="configuration"/>; a missing key keeps its default.</summary>
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
/// code is refused with the same 429 until room is made (the cache drops the expired and the least recently used entries): the lock is never
/// skipped.</para>
/// </remarks>
public sealed class GuestPhoneLookupLockout : IDisposable
{
    /// <summary>The name of the limit in the log.</summary>
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

    /// <summary>Failed attempts on one code, within its window, that lock it.</summary>
    public int MaxFailures { get; }

    /// <summary>The window of a code; the <c>Retry-After</c> of a lock always names all of it.</summary>
    public TimeSpan Window { get; }

    /// <summary>The most codes followed at once.</summary>
    public int MaxTrackedCodes { get; }

    /// <summary>How many codes are followed now, never more than <see cref="MaxTrackedCodes"/>.</summary>
    public int TrackedCodes => _codes.Count;

    /// <summary>
    /// The key of <paramref name="bookingCode"/>: the SHA-256 of the code in its stored form (no dash, upper case, O read as 0, I and L as 1), or of
    /// the typed text trimmed and in upper case when it is not a code, in upper-case hex. Never the code itself.
    /// </summary>
    public static string KeyOf(string bookingCode)
    {
        ArgumentNullException.ThrowIfNull(bookingCode);
        var normalized = BookingCodes.TryNormalize(bookingCode, out var code) ? code : bookingCode.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// Lets one attempt on <paramref name="bookingCode"/> through, counted as failed until <see cref="Reset"/>. False when the code is locked (the
    /// <see cref="MaxFailures"/> attempts of its window are spent) or the store is full: then the caller answers 429 and reads no booking.
    /// </summary>
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

                // The window is over: the lock with it, and the next attempt opens a new window.
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

    /// <summary>A match: the count of <paramref name="bookingCode"/> goes back to zero.</summary>
    public void Reset(string bookingCode)
    {
        var key = KeyOf(bookingCode);
        lock (_gate)
            _codes.Remove(key);
    }

    public void Dispose() => _codes.Dispose();

    /// <summary>The attempts on one code within its window.</summary>
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

            // No code, digits or IP in the log: the policy, the route and the trace id identify the event.
            logger.LogWarning(
                "Rate limit exceeded: policy {Policy} on {Method} {Route}. TraceId={TraceId}",
                GuestPhoneLookupLockout.PolicyName,
                httpContext.Request.Method,
                (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
                ApiProblemDetails.GetTraceId(httpContext));
            context.Result = SupplierBookingEmailRateLimitFilter.RateLimitedResult(httpContext, lockout.Window);
            return;
        }

        // The attempt was counted as failed when it was let through: only a match (the action ran to its end) takes the count back to zero. A 404,
        // or any other error, leaves it counted.
        var executed = await next();
        if (executed.Exception is null && !executed.Canceled)
            lockout.Reset(request.BookingCode);
    }
}
