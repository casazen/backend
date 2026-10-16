using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Integration;

/// <summary>
/// The host of the long-term area (LR-12): <c>Features:LongRentArea</c> on (and <c>Features:PropertyModeChange</c>, to show the mode of a
/// property is free again once its only draft is deleted), a clock the tests control (10:00 in Rome on 9 October 2026), over the usual
/// integration host (PostgreSQL in CI, EF InMemory in a local run). The contract templates are the real ones, which are not approved
/// (decision LR-D28): <c>sent</c> answers 422 as it does in production today.
/// </summary>
public class LongRentAreaFactory : CasazenWebApplicationFactory
{
    /// <summary>10:00 in Rome on 9 October 2026 (daylight saving time): "today" of every test is 2026-10-09.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = new(Now);

    /// <summary>True: the contract template of every regime is approved and complete (a fake), so <c>sent</c> can succeed.</summary>
    protected virtual bool ApprovedTemplates => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:LongRentArea"] = "true",
                ["Features:PropertyModeChange"] = "true",
            }));
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<TimeProvider>(services);
            services.AddSingleton<TimeProvider>(Clock);

            if (!ApprovedTemplates)
                return;

            RemoveAllOf<ILeaseTemplateService>(services);
            var templates = new Mock<ILeaseTemplateService>();
            templates.Setup(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>())).Returns((string?)null);
            templates.Setup(t => t.GeneratePdfAsync(It.IsAny<LeaseContract>())).ReturnsAsync("%PDF-1.4 test"u8.ToArray());
            services.AddSingleton(templates.Object);
        });
    }

    /// <summary>A property of the org, long-term, with its energy certificate and its cadastral data: nothing is missing at step 1.</summary>
    public async Task MakeReadyAsync(Guid propertyId, bool ape = true, bool cadastre = true, RentalMode mode = RentalMode.Long)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        property.RentalMode = mode;
        if (cadastre)
        {
            property.CadastralSheet = "14";
            property.CadastralParcel = "233";
            property.CadastralCategory = "A/2";
            property.CadastralIncome = 640m;
        }

        if (ape && !await db.PropertyDocuments.IgnoreQueryFilters().AnyAsync(d => d.PropertyId == propertyId && d.DocumentType == DocumentType.Ape))
        {
            db.PropertyDocuments.Add(new PropertyDocument
            {
                PropertyId = propertyId,
                OrgId = property.OrgId,
                FileName = "ape.pdf",
                StorageUrl = $"properties/{propertyId}/documents/ape.pdf",
                DocumentType = DocumentType.Ape,
                UploadedBy = property.OwnerId,
            });
        }

        await db.SaveChangesAsync();
    }
}

/// <summary>The same host with every contract template approved: the download for signature can succeed.</summary>
public sealed class LongRentAreaApprovedTemplatesFactory : LongRentAreaFactory
{
    protected override bool ApprovedTemplates => true;
}
