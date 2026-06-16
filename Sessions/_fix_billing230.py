import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

for p in [
    "Casazen.Infrastructure/Services/OnboardingService.cs",
    "Casazen.Infrastructure/Services/LegalDocumentService.cs",
    "Casazen.Infrastructure/Services/RentBillingService.cs",
    "Casazen.Infrastructure/Services/NullRentBillingService.cs",
    "Casazen.Infrastructure/Repositories/RentLedgerRepository.cs",
    "Casazen.Infrastructure/Repositories/RentScheduleRepository.cs",
    "Casazen.Core/Entities/RentSchedule.cs",
    "Casazen.Core/Entities/RentLedgerEntry.cs",
    "Casazen.Core/Entities/ConsentRecord.cs",
    "Casazen.Core/Entities/Enums/RentCadence.cs",
    "Casazen.Core/Entities/Enums/RentLedgerStatus.cs",
    "Casazen.Core/Entities/Enums/ConsentType.cs",
    "Casazen.Core/Services/IOnboardingService.cs",
    "Casazen.Core/Services/ILegalDocumentService.cs",
    "Casazen.Core/Services/IRentBillingService.cs",
]:
    (ROOT / p).unlink(missing_ok=True)

content = subprocess.check_output(
    ["git", "show", "HEAD:Casazen.Infrastructure/Data/AppDbContext.cs"], text=True
)
content = content.replace(
    "    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;\n\n    // Long-term lease",
    """    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;
    public DbSet<PlatformInvoice> PlatformInvoices { get; set; } = null!;
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; } = null!;
    public DbSet<PlatformBillingMetrics> PlatformBillingMetrics { get; set; } = null!;

    // Long-term lease""",
)
billing_config = """
        modelBuilder.Entity<Org>()
            .HasIndex(o => o.StripeCustomerId);

        modelBuilder.Entity<PlatformInvoice>()
            .HasOne(i => i.Org)
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.StripeInvoiceId)
            .IsUnique();

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.OrgId);

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.SdiStatus);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.AmountExVat)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.VatAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.TotalAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<ProcessedStripeEvent>()
            .HasIndex(e => e.EventId)
            .IsUnique();

        modelBuilder.Entity<PlatformBillingMetrics>()
            .Property(m => m.EuB2cCrossBorderRevenue)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformBillingMetrics>().HasData(
            new PlatformBillingMetrics
            {
                Id = 1,
                CalendarYear = 2026,
                EuB2cCrossBorderRevenue = 0m,
                OssThresholdReached = false,
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });

"""
content = content.replace(
    "        modelBuilder.Entity<Org>()\n            .HasIndex(o => o.Slug)\n            .IsUnique();\n\n        // OrgId indexes",
    "        modelBuilder.Entity<Org>()\n            .HasIndex(o => o.Slug)\n            .IsUnique();\n"
    + billing_config
    + "\n        // OrgId indexes",
)
(ROOT / "Casazen.Infrastructure/Data/AppDbContext.cs").write_text(content, encoding="utf-8")

sbs_path = ROOT / "Casazen.Infrastructure/Services/StripeBillingService.cs"
sbs = sbs_path.read_text(encoding="utf-8")
if "using Stripe;" not in sbs:
    sbs = sbs.replace(
        "using Microsoft.Extensions.Configuration;\n",
        "using Microsoft.Extensions.Configuration;\nusing Stripe;\n",
    )
sbs_path.write_text(sbs, encoding="utf-8")

print("fix complete")
