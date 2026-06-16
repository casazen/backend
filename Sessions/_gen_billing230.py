from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
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

parts = [
    '#!/usr/bin/env python3',
    '"""Atomic installer for Issue #230 SaaS billing backend."""',
    "from pathlib import Path",
    "",
    "ROOT = Path(__file__).resolve().parents[1]",
    "FILES = {",
]
for rel in TARGETS:
    p = ROOT / rel.replace("/", "\\")
    if not p.exists():
        raise SystemExit(f"missing source file: {rel}")
    content = p.read_text(encoding="utf-8")
    parts.append(f"    {rel!r}: {content!r},")
parts.extend([
    "}",
    "",
    "DELETE_PATHS = [",
    '    ROOT / "Casazen.Infrastructure" / "External" / "WebhookSource.cs",',
    "]",
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
])
(ROOT / "Sessions" / "billing230-install.py").write_text("\n".join(parts), encoding="utf-8")
print(f"Generated Sessions/billing230-install.py with {len(TARGETS)} files")
