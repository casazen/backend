#!/usr/bin/env python3
"""Build Sessions/billing230-install.py from transcripts + git stash."""
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TRANSCRIPT = Path(
    r"C:\Users\luca.la-malfa\.cursor\projects\C-Users-luca-la-malfa-private-project-casazen-backend"
    r"\agent-transcripts\a4ac3565-a14c-43c5-aeac-94e820e51386\a4ac3565-a14c-43c5-aeac-94e820e51386.jsonl"
)
TRANSCRIPT67 = Path(
    r"C:\Users\luca.la-malfa\.cursor\projects\C-Users-luca-la-malfa-private-project-casazen-backend"
    r"\agent-transcripts\67bf6f2f-6371-4449-819b-b118065a628d\67bf6f2f-6371-4449-819b-b118065a628d.jsonl"
)


def extract_writes(path: Path) -> dict[str, str]:
    files: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        for part in obj.get("message", {}).get("content", []):
            if part.get("type") != "tool_use" or part.get("name") != "Write":
                continue
            inp = part.get("input", {})
            p = inp.get("path", "").replace("\\\\", "/").replace("\\", "/")
            idx = p.lower().find("/backend/")
            if idx < 0:
                continue
            rel = p[idx + 9 :]
            if rel.startswith("Sessions/"):
                continue
            files[rel] = inp.get("contents", "")
    return files


def git_show(stash_path: str) -> str:
    r = subprocess.run(
        ["git", "show", stash_path],
        capture_output=True,
        text=True,
        cwd=ROOT,
        check=False,
    )
    if r.returncode != 0:
        raise SystemExit(f"git show failed: {stash_path}\n{r.stderr}")
    return r.stdout


files = extract_writes(TRANSCRIPT)
for k, v in extract_writes(TRANSCRIPT67).items():
    files.setdefault(k, v)

stash_map = {
    "Casazen.Core/Entities/Org.cs": "stash@{1}:Casazen.Core/Entities/Org.cs",
    "Casazen.Infrastructure/Data/AppDbContext.cs": "stash@{1}:Casazen.Infrastructure/Data/AppDbContext.cs",
    "Casazen.Infrastructure/External/StripeWebhookHandler.cs": "stash@{1}:Casazen.Infrastructure/External/StripeWebhookHandler.cs",
    "Casazen.Infrastructure/Services/OrgService.cs": "stash@{1}:Casazen.Infrastructure/Services/OrgService.cs",
    "Casazen.Web/Extensions/ServiceCollectionExtensions.cs": "stash@{1}:Casazen.Web/Extensions/ServiceCollectionExtensions.cs",
    "Casazen.Web/Controllers/OrgsController.cs": "stash@{1}:Casazen.Web/Controllers/OrgsController.cs",
    "Casazen.Web/BackgroundJobs/StripeWebhookJob.cs": "stash@{1}:Casazen.Web/BackgroundJobs/StripeWebhookJob.cs",
    "Casazen.Web/Controllers/WebhooksController.cs": "stash@{1}:Casazen.Web/Controllers/WebhooksController.cs",
}
for rel, ref in stash_map.items():
    files[rel] = git_show(ref)

# Avoid PowerShell/git encoding corruption for large handler file
handler_path = ROOT / "Casazen.Infrastructure/External/StripeWebhookHandler.cs"
subprocess.run(
    ["git", "show", "stash@{1}:Casazen.Infrastructure/External/StripeWebhookHandler.cs"],
    stdout=handler_path.open("w", encoding="utf-8", newline="\n"),
    cwd=ROOT,
    check=True,
)
files["Casazen.Infrastructure/External/StripeWebhookHandler.cs"] = handler_path.read_text(encoding="utf-8")

adb = files["Casazen.Infrastructure/Data/AppDbContext.cs"]
old = """        modelBuilder.Entity<Org>()
            .HasIndex(o => o.Slug)
            .IsUnique();

        // OrgId indexes on the tenant-scoped tables + Users (AC2/AC9)."""
new = """        modelBuilder.Entity<Org>()
            .HasIndex(o => o.Slug)
            .IsUnique();

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

        // OrgId indexes on the tenant-scoped tables + Users (AC2/AC9)."""
if old in adb:
    adb = adb.replace(old, new)
if "public DbSet<PlatformInvoice>" not in adb:
    adb = adb.replace(
        "    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;",
        "    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;\n"
        "    public DbSet<PlatformInvoice> PlatformInvoices { get; set; } = null!;\n"
        "    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; } = null!;\n"
        "    public DbSet<PlatformBillingMetrics> PlatformBillingMetrics { get; set; } = null!;",
    )
files["Casazen.Infrastructure/Data/AppDbContext.cs"] = adb

irb_path = ROOT / "Casazen.Core/Services/IRentBillingService.cs"
if irb_path.exists():
    files["Casazen.Core/Services/IRentBillingService.cs"] = irb_path.read_text(encoding="utf-8")

files["Casazen.Infrastructure/Services/NullRentBillingService.cs"] = """using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>Placeholder until #269 RentBillingService ships; satisfies webhook DI.</summary>
public class NullRentBillingService : IRentBillingService
{
    public Task<RentSchedule?> GetScheduleAsync(Guid leaseId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<RentSchedule> UpsertScheduleAsync(Guid leaseId, string ownerId, UpsertRentScheduleRequest request) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<RentSchedule> DisableScheduleAsync(Guid leaseId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<(IReadOnlyList<RentLedgerEntry> Items, int TotalCount)> GetLedgerPageAsync(
        Guid leaseId, string ownerId, RentLedgerStatus? status, DateOnly? from, DateOnly? to, int page, int pageSize) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task<(Stream Content, string FileName)?> GetReceiptAsync(Guid leaseId, Guid entryId, string ownerId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task MaterializeAndChargePeriodAsync(Guid scheduleId) =>
        throw new NotImplementedException("Rent billing not yet implemented");

    public Task HandleRentPaymentSucceededAsync(Guid entryId) => Task.CompletedTask;

    public Task HandleRentPaymentFailedAsync(Guid entryId, bool canceled) => Task.CompletedTask;
}
"""

# Ensure EntitlementResult record exists in interface file
ie = files.get("Casazen.Core/Services/IEntitlementService.cs", "")
if "record EntitlementResult" not in ie:
    ie = ie.replace(
        "namespace Casazen.Core.Services;\n\n",
        "namespace Casazen.Core.Services;\n\n"
        "public sealed record EntitlementResult(\n"
        "    Guid OrgId,\n"
        "    string PlanTier,\n"
        "    int MaxProperties,\n"
        "    int PropertyCount,\n"
        "    bool CanAddProperty);\n\n",
    )
    files["Casazen.Core/Services/IEntitlementService.cs"] = ie

# ServiceCollectionExtensions billing + rent stub
sce = files["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"]
if "IStripeBillingService" not in sce:
    sce = sce.replace(
        "        services.AddScoped<IEntitlementService, EntitlementService>();\n",
        "        services.AddScoped<IEntitlementService, EntitlementService>();\n"
        "        services.AddScoped<IStripeBillingService, StripeBillingService>();\n"
        "        services.AddScoped<IVatCalculationService, VatCalculationService>();\n"
        "        services.AddScoped<IViesService, ViesService>();\n"
        "        services.AddScoped<ISdiEInvoiceService, SdiEInvoiceService>();\n"
        "        services.AddScoped<IBillingEntryGate, BillingEntryGate>();\n"
        "        services.AddScoped<IOssRevenueTracker, OssRevenueTracker>();\n"
        "        services.AddScoped<IRentBillingService, NullRentBillingService>();\n",
    )
if "RequireOrgBillingAdmin" not in sce:
    sce = sce.replace(
        '            .AddPolicy("LongTermLandlord", policy => policy.RequireRole("LongTermLandlord"));\n\n        RegisterContextPolicies(builder);',
        '            .AddPolicy("LongTermLandlord", policy => policy.RequireRole("LongTermLandlord"))\n'
        '            .AddPolicy("RequireOrgBillingAdmin", policy =>\n'
        '                policy.Requirements.Add(new OrgBillingAdminRequirement()));\n\n'
        '        services.AddScoped<IAuthorizationHandler, OrgBillingAdminAuthorizationHandler>();\n\n'
        '        RegisterContextPolicies(builder);',
    )
if "AddCasazenServices(this IServiceCollection services)" in sce and "IConfiguration configuration" not in sce.split("AddCasazenServices")[1][:120]:
    sce = sce.replace(
        "public static IServiceCollection AddCasazenServices(this IServiceCollection services)",
        "public static IServiceCollection AddCasazenServices(this IServiceCollection services, IConfiguration configuration)",
    )
files["Casazen.Web/Extensions/ServiceCollectionExtensions.cs"] = sce

# LeaseContract navigation for rent schedule
lc_path = ROOT / "Casazen.Core/Entities/LeaseContract.cs"
if lc_path.exists() and "RentSchedule" not in lc_path.read_text(encoding="utf-8"):
    lc = lc_path.read_text(encoding="utf-8").replace(
        "    public virtual LeaseRegistration? Registration { get; set; };\n    public virtual ICollection<LeaseEvent> Events { get; set; } = [];",
        "    public virtual LeaseRegistration? Registration { get; set; };\n    public virtual RentSchedule? RentSchedule { get; set; };\n    public virtual ICollection<LeaseEvent> Events { get; set; } = [];",
    )
    files["Casazen.Core/Entities/LeaseContract.cs"] = lc
elif "Casazen.Core/Entities/LeaseContract.cs" not in files:
    files["Casazen.Core/Entities/LeaseContract.cs"] = lc_path.read_text(encoding="utf-8")

swj = files["Casazen.Web/BackgroundJobs/StripeWebhookJob.cs"]
if "using Casazen.Core.Entities.Enums;" not in swj:
    swj = swj.replace(
        "using Casazen.Infrastructure.External;",
        "using Casazen.Core.Entities.Enums;\nusing Casazen.Infrastructure.External;",
    )
files["Casazen.Web/BackgroundJobs/StripeWebhookJob.cs"] = swj

wh = files["Casazen.Web/Controllers/WebhooksController.cs"]
if "using Casazen.Core.Entities.Enums;" not in wh:
    wh = wh.replace(
        "using System.Security.Cryptography;",
        "using Casazen.Core.Entities.Enums;\nusing System.Security.Cryptography;",
    )
files["Casazen.Web/Controllers/WebhooksController.cs"] = wh

apps = (ROOT / "Casazen.Web/appsettings.json").read_text(encoding="utf-8")
if '"Billing"' not in apps:
    insert = """  "Billing": {
    "PastDueGraceDays": 7,
    "PlatformVatNumber": "PLACEHOLDER_PIVA",
    "Prices": {
      "Starter": "price_starter",
      "Pro": "price_pro",
      "Scale": "price_scale"
    },
    "Display": {
      "Starter": { "Name": "Starter", "PriceMonthly": 29, "Features": ["Fino a 3 proprietà"] },
      "Pro": { "Name": "Pro", "PriceMonthly": 79, "Features": ["Fino a 50 proprietà"] },
      "Scale": { "Name": "Scale", "PriceMonthly": 199, "Features": ["Proprietà illimitate"] }
    }
  },
  "Sdi": {
    "Enabled": false,
    "ProviderApiKey": "PLACEHOLDER",
    "TransmissionEndpoint": "https://sdi.example.test"
  },
  "Vies": {
    "Enabled": false,
    "Endpoint": "https://ec.europa.eu/taxation_customs/vies/services/checkVatService"
  },
"""
    apps = apps.replace('  "DirectBooking": {', insert + '  "DirectBooking": {')
files["Casazen.Web/appsettings.json"] = apps

factory = (ROOT / "Casazen.Tests/Integration/CasazenWebApplicationFactory.cs").read_text(encoding="utf-8")
if "IStripeBillingService" not in factory:
    factory = factory.replace(
        "            RemoveService<IStripeService>(services);\n"
        "            services.AddSingleton<IStripeService, FakeStripeService>();",
        "            RemoveService<IStripeService>(services);\n"
        "            services.AddSingleton<IStripeService, FakeStripeService>();\n\n"
        "            RemoveService<IStripeBillingService>(services);\n"
        "            services.AddSingleton<IStripeBillingService, FakeStripeBillingService>();\n\n"
        "            RemoveService<IBillingEntryGate>(services);\n"
        "            services.AddSingleton<IBillingEntryGate>(sp =>\n"
        "            {\n"
        "                var gate = new Mock<IBillingEntryGate>();\n"
        "                gate.Setup(g => g.AssertCanChargeAsync(It.IsAny<CancellationToken>()))"
        ".Returns(Task.CompletedTask);\n"
        "                return gate.Object;\n"
        "            });",
    )
if "Billing:Sdi:Enabled" not in factory:
    factory = factory.replace(
        '                ["Seo:BootstrapOnStartup"] = "false",',
        '                ["Seo:BootstrapOnStartup"] = "false",\n'
        '                ["Billing:Sdi:Enabled"] = "false",\n'
        '                ["Billing:PlatformVatNumber"] = "IT12345678901",\n'
        '                ["Vies:Enabled"] = "false",',
    )
files["Casazen.Tests/Integration/CasazenWebApplicationFactory.cs"] = factory

est = (ROOT / "Casazen.Tests/Unit/Services/EntitlementServiceTests.cs").read_text(encoding="utf-8")
if "SyncFromSubscriptionAsync" not in est:
    est = est.rstrip() + """

    [Fact]
    public async Task SyncFromSubscriptionAsync_PastDueBeyondGrace_DowngradesToStarter()
    {
        await using var db = NewDb();
        var org = new OrgEntity
        {
            Name = "Org", Slug = $"org-{Guid.NewGuid():N}", DisplayName = "Org", ContactEmail = "o@x.it",
            PlanTier = PlanTier.Pro, SubscriptionStatus = SubscriptionStatus.PastDue,
            PastDueSince = DateTime.UtcNow.AddDays(-10), IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        var service = new EntitlementService(db, Config(new() { ["Billing:PastDueGraceDays"] = "7" }));

        await service.SyncFromSubscriptionAsync(org.Id);

        var updated = await db.Orgs.FindAsync(org.Id);
        Assert.Equal(PlanTier.Starter, updated!.PlanTier);
    }

    [Fact]
    public async Task GetEntitlementAsync_ActiveSubscription_UsesStoredTier()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Pro, properties: 1);
        var org = await db.Orgs.FindAsync(orgId);
        org!.SubscriptionStatus = SubscriptionStatus.Active;
        await db.SaveChangesAsync();
        var service = new EntitlementService(db, Config());

        var result = await service.GetEntitlementAsync(orgId);

        Assert.Equal("Pro", result.PlanTier);
        Assert.Equal(50, result.MaxProperties);
    }
}
"""
files["Casazen.Tests/Unit/Services/EntitlementServiceTests.cs"] = est

TARGETS = [
    "Casazen.Core/Entities/Enums/SubscriptionStatus.cs",
    "Casazen.Core/Entities/Enums/WebhookSource.cs",
    "Casazen.Core/Entities/Enums/RentCadence.cs",
    "Casazen.Core/Entities/Enums/RentLedgerStatus.cs",
    "Casazen.Core/Entities/PlatformInvoice.cs",
    "Casazen.Core/Entities/ProcessedStripeEvent.cs",
    "Casazen.Core/Entities/PlatformBillingMetrics.cs",
    "Casazen.Core/Entities/RentSchedule.cs",
    "Casazen.Core/Entities/RentLedgerEntry.cs",
    "Casazen.Core/Entities/Org.cs",
    "Casazen.Core/Services/IStripeBillingService.cs",
    "Casazen.Core/Services/IVatCalculationService.cs",
    "Casazen.Core/Services/IViesService.cs",
    "Casazen.Core/Services/ISdiEInvoiceService.cs",
    "Casazen.Core/Services/IBillingEntryGate.cs",
    "Casazen.Core/Services/IOssRevenueTracker.cs",
    "Casazen.Core/Services/IRentBillingService.cs",
    "Casazen.Core/Services/IEntitlementService.cs",
    "Casazen.Core/Services/IOrgService.cs",
    "Casazen.Infrastructure/Services/StripeBillingService.cs",
    "Casazen.Infrastructure/Services/VatCalculationService.cs",
    "Casazen.Infrastructure/Services/ViesService.cs",
    "Casazen.Infrastructure/Services/SdiEInvoiceService.cs",
    "Casazen.Infrastructure/Services/BillingEntryGate.cs",
    "Casazen.Infrastructure/Services/OssRevenueTracker.cs",
    "Casazen.Infrastructure/Services/NullRentBillingService.cs",
    "Casazen.Infrastructure/Services/EntitlementService.cs",
    "Casazen.Infrastructure/Services/OrgService.cs",
    "Casazen.Infrastructure/External/StripeWebhookHandler.cs",
    "Casazen.Infrastructure/Data/AppDbContext.cs",
    "Casazen.Web/Controllers/BillingController.cs",
    "Casazen.Web/Controllers/OrgsController.cs",
    "Casazen.Web/Controllers/WebhooksController.cs",
    "Casazen.Web/DTOs/Billing/BillingDtos.cs",
    "Casazen.Web/Infrastructure/OrgBillingAdminAuthorizationHandler.cs",
    "Casazen.Web/Extensions/ServiceCollectionExtensions.cs",
    "Casazen.Web/BackgroundJobs/StripeWebhookJob.cs",
    "Casazen.Web/appsettings.json",
    "Casazen.Tests/Integration/FakeStripeBillingService.cs",
    "Casazen.Tests/Integration/BillingIntegrationTests.cs",
    "Casazen.Tests/Integration/CasazenWebApplicationFactory.cs",
    "Casazen.Tests/Unit/Services/VatCalculationServiceTests.cs",
    "Casazen.Tests/Unit/Services/EntitlementServiceTests.cs",
]

missing = [t for t in TARGETS if t not in files or not files[t].strip()]
if missing:
    raise SystemExit("Missing content for: " + ", ".join(missing))

out_lines = [
    "#!/usr/bin/env python3",
    '"""Atomic installer for Issue #230 SaaS billing backend."""',
    "from pathlib import Path",
    "",
    "ROOT = Path(__file__).resolve().parents[1]",
    "FILES = {",
]
for rel in TARGETS:
    out_lines.append(f"    {rel!r}: {files[rel]!r},")
out_lines += [
    "}",
    "",
    'DELETE_PATHS = [ROOT / "Casazen.Infrastructure" / "External" / "WebhookSource.cs"]',
    "",
    "def main() -> None:",
    "    for path in DELETE_PATHS:",
    "        if path.exists():",
    "            path.unlink()",
    '            print(f"Deleted {path.relative_to(ROOT)}")',
    "    for rel, content in FILES.items():",
    '        path = ROOT / rel.replace("/", "\\\\")',
    "        path.parent.mkdir(parents=True, exist_ok=True)",
    '        path.write_text(content, encoding="utf-8")',
    '        print(f"Wrote {rel}")',
    "",
    'if __name__ == "__main__":',
    "    main()",
    "",
]

install_path = ROOT / "Sessions/billing230-install.py"
install_path.write_text("\n".join(out_lines), encoding="utf-8")
print(f"Generated {install_path} with {len(TARGETS)} files ({install_path.stat().st_size} bytes)")
