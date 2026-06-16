#!/usr/bin/env python3
"""Generate Issue #271 PLG onboarding backend files."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

FILES = {
"Casazen.Core/Entities/Enums/ConsentType.cs": """namespace Casazen.Core.Entities.Enums;

public enum ConsentType
{
    Tos,
    Privacy,
    Dpa,
    Subprocessors,
    Marketing
}
""",
"Casazen.Core/Entities/ConsentRecord.cs": """using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table(\"ConsentRecords\")]
public class ConsentRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;
    public virtual User User { get; set; } = null!;

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    public ConsentType Type { get; set; }

    [Required, MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    public DateTime AcceptedAt { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }
}
""",
}

def main():
    for rel, content in FILES.items():
        path = ROOT / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        print(f"Wrote {rel}")

if __name__ == "__main__":
    main()
