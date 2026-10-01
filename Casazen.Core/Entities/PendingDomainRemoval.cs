using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Entities;

/// <summary>
/// A custom domain that was added to the Vercel project and must be removed from it (BK-17): the owner changed or dropped
/// it, or the org was deactivated. The periodic domain job removes it (a failed call is retried), unless another org uses the
/// same domain in the meantime. Not tenant-owned: it names a domain, never an org, and only the platform job reads it.
/// </summary>
[System.ComponentModel.DataAnnotations.Schema.Table("PendingDomainRemovals")]
public class PendingDomainRemoval
{
    /// <summary>Normalized FQDN (lower case, no scheme or port).</summary>
    [Key, MaxLength(253)]
    public string Domain { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public int Attempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Stable code of the last failure (<c>VercelCallStatus</c>), never a message from the provider.</summary>
    [MaxLength(64)]
    public string? LastError { get; set; }
}
