using Casazen.Web.Extensions;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Second limit of the contact form of the booking site (DB-03b): a fixed window per e-mail address <b>and org</b>, on top of the per-IP
/// policy <see cref="RateLimitPolicies.PublicContact"/>, so that one address cannot fill a host's mailbox from many IPs. Every message
/// that reaches the limit counts, sent or not, and the 429 always names the whole window (never the time that is left of it), so the
/// limit tells nothing about whether the org has a contact email or when somebody last wrote.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:HostContactPerEmail:PermitLimit</c> and
/// <c>WindowSeconds</c> (default 3 per day). The partition key is a hash of the org and the normalized address, never the address.
/// Counters live in memory, per replica.
/// </remarks>
public sealed class HostContactEmailRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("HostContactPerEmail", 3, TimeSpan.FromDays(1));

    public override bool NamesTheWholeWindow => true;
}
