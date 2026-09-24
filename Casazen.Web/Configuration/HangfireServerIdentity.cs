namespace Casazen.Web.Configuration;

/// <summary>
/// Identity of this process's Hangfire server. The server name is set explicitly to the machine (container) name, so
/// Hangfire's server id is <c>&lt;machine&gt;:&lt;pid&gt;:&lt;guid&gt;</c> (lowercase) whatever the host exports as
/// <c>HOSTNAME</c>/<c>COMPUTERNAME</c>, and the health check can tell this instance's server from the others in the
/// same schema (e.g. the previous container during a deploy).
/// </summary>
public static class HangfireServerIdentity
{
    public static string ServerName { get; } = Environment.MachineName;

    /// <summary>Prefix of the id of the server running in this process.</summary>
    public static string CurrentProcessIdPrefix { get; } =
        $"{ServerName}:{Environment.ProcessId}:".ToLowerInvariant();

    public static bool IsCurrentProcess(string? serverId) =>
        serverId is not null && serverId.StartsWith(CurrentProcessIdPrefix, StringComparison.OrdinalIgnoreCase);
}
