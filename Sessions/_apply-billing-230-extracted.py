#!/usr/bin/env python3
"""Apply Issue #230 SaaS billing backend files."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

FILES = {
    "Casazen.Core/Entities/Enums/SubscriptionStatus.cs": """namespace Casazen.Core.Entities.Enums;

public enum SubscriptionStatus
{
    None,
    Trialing,
    Active,
    PastDue,
    Canceled,
}
""",
    "Casazen.Core/Entities/Enums/WebhookSource.cs": """namespace Casazen.Core.Entities.Enums;

public enum WebhookSource
{
    Platform,
    Connected,
}
""",
    "Casazen.Core/Entities/PlatformInvoice.cs": """using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PlatformInvoices")]
public class PlatformInvoice
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    [Required, MaxLength(255)]
    public string StripeInvoiceId { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountExVat { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }
    [Required, MaxLength(32)]
    public string VatTreatment { get; set; } = string.Empty;
    public bool OssApplied { get; set; }
    [Required, MaxLength(32)]
    public string SdiStatus { get; set; } = "pending";
    [MaxLength(255)]
    public string? SdiTransmissionId { get; set; }
    [MaxLength(1000)]
    public string? FatturaPaXmlUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Org Org { get; set; } = null!;
}
""",
    "Casazen.Core/Entities/ProcessedStripeEvent.cs": """using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("ProcessedStripeEvents")]
public class ProcessedStripeEvent
{
    [Key, MaxLength(255)]
    public string EventId { get; set; } = string.Empty;
    [Required, MaxLength(128)]
    public string EventType { get; set; } = string.Empty;
    public WebhookSource Source { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
""",
    "Casazen.Core/Entities/PlatformBillingMetrics.cs": """using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PlatformBillingMetrics")]
public class PlatformBillingMetrics
{
    [Key]
    public int Id { get; set; }
    public int CalendarYear { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal EuB2cCrossBorderRevenue { get; set; }
    public bool OssThresholdReached { get; set; }
    public DateTime? OssSwitchoverAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
""",
}

def main() -> None:
    old = ROOT / "Casazen.Infrastructure" / "External" / "WebhookSource.cs"
    if old.exists():
        old.unlink()
    for rel, content in FILES.items():
        path = ROOT / rel.replace("/", "\\")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        print(f"Wrote {rel}")

if __name__ == "__main__":
    main()
