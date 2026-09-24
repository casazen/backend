using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// TN-2 architecture guard for the global tenant query filter. Every entity of <see cref="AppDbContext"/>
/// is either <see cref="ITenantOwned"/> (and then automatically filtered by the caller's org) or listed in
/// <see cref="NotTenantFiltered"/> with the reason it must stay unfiltered. Adding a DbSet that is neither
/// fails the build: decide explicitly, do not leave a tenant table without filter.
/// </summary>
public class TenantQueryFilterArchitectureTests
{
    /// <summary>Entities deliberately NOT tenant-filtered, each with its reason.</summary>
    private static readonly IReadOnlyDictionary<Type, string> NotTenantFiltered = new Dictionary<Type, string>
    {
        // Tenant, identity and authorization catalog.
        [typeof(OrgEntity)] = "The tenant itself: resolved by id, slug or domain for public sites, billing and onboarding, before a tenant is known.",
        [typeof(User)] = "Identity table: TenantContext reads User.OrgId to resolve the tenant (a filter here would recurse); users are looked up by their own sub, admins manage all users.",
        [typeof(UserContextMembership)] = "Authorization data keyed by UserId, read by the authorization handlers for the caller's own sub before the tenant is resolved.",
        [typeof(AppContextEntity)] = "Global catalog of application contexts (seeded reference data).",
        [typeof(Role)] = "Global role catalog (seeded reference data).",
        [typeof(RolePermission)] = "Global permission catalog (seeded reference data).",

        // Platform reference data and platform-wide state.
        [typeof(TouristTaxRate)] = "Platform reference data: tourist tax rates per comune, managed by admins and read by every org.",
        [typeof(CancellationPolicy)] = "Platform catalog of cancellation policies shared by the properties of every org.",
        [typeof(TerritorialRentAgreement)] = "Platform reference data: territorial rent agreements (canone concordato).",
        [typeof(ConcordatoRentBand)] = "Platform reference data: rent bands of a territorial agreement.",
        [typeof(TerritorialAgreementSignatory)] = "Platform reference data: signatories of a territorial agreement.",
        [typeof(HighTensionAreaComune)] = "Platform reference data: comuni in high housing-tension areas.",
        [typeof(SeoContentPage)] = "Platform SEO content (public comune pages) managed by admins.",
        [typeof(SeoContentRevision)] = "Revisions of platform SEO content managed by admins.",
        [typeof(PlatformAiBudget)] = "Platform-wide AI token budget, not per org.",
        [typeof(PlatformBillingMetrics)] = "Platform-wide billing metrics (OSS threshold), not per org.",
        [typeof(ProcessedStripeEvent)] = "Platform-wide Stripe webhook idempotency keys, written by the anonymous webhook.",
        [typeof(DataProtectionKey)] = "ASP.NET Core Data Protection key ring of the whole application (FD-07), not tenant data.",
        [typeof(AlloggiatiCodeEntry)] = "Platform reference data: official Alloggiati Web code tables (comuni, stati, documents), imported by admins and read by every org and by the anonymous guest portal (CO-12).",
        [typeof(AlloggiatiCodeTableImport)] = "Platform reference data: log of the admin imports of the official Alloggiati code tables, not tenant data (CO-12).",

        // Supplier marketplace: the supplier acts as User.SupplierOrgId, the tenant filter uses User.OrgId.
        [typeof(ServiceRequest)] = "Two parties: host OrgId and supplier SupplierOrgId. A host-org filter would hide the request from the supplier who takes, completes or rejects it, and host matching counts supplier load across orgs. Every query scopes explicitly by OrgId or SupplierOrgId (ServiceRequestService, ServiceRequestRepository).",
        [typeof(SupplierProfile)] = "Keyed by the supplier org: hosts of every org read active profiles to match and create requests, the public showcase reads them anonymously, admins approve them. The owner reaches it as User.SupplierOrgId, which the tenant filter (User.OrgId) does not know.",
        [typeof(SupplierAvailability)] = "Supplier-org data (see SupplierProfile): written and read by the supplier through User.SupplierOrgId, read anonymously by the public showcase; every query filters by the supplier OrgId explicitly.",
        [typeof(SupplierInviteRecord)] = "Admin-issued supplier invitations keyed by e-mail, before any supplier org exists.",
        [typeof(SupplierJob)] = "Legacy supplier jobs keyed by SupplierOrgId, to be removed by SU-11 (decision D12).",

        // Rows owned by a user, not by an org.
        [typeof(DeviceRegistration)] = "Owned by the user (UserId), not the org: DevicesController only touches the caller's own rows and must drop the same push token from users of any org; push delivery crosses orgs by design (supplier to host) with explicit OrgId and UserId predicates. No endpoint lists devices.",

        // Children never addressed by their own id: reached only through a tenant-filtered parent.
        [typeof(OtaSyncLog)] = "No reader or writer outside its unused repository (OTA partner APIs frozen, D10). Must become ITenantOwned before an endpoint exposes it.",
        [typeof(PropertyQuesturaCredentials)] = "No reader or writer yet. Must become ITenantOwned when the credentials UI is added (CO-14).",
        [typeof(Party)] = "Reached only through its LeaseContract (tenant-filtered and owner-verified by LeaseWorkflowService); no endpoint addresses a party by its own id.",
        [typeof(LeaseRegistration)] = "Reached only through its LeaseContract (tenant-filtered, then authorized by the controller and RliRegistrationService); the provider polling job runs without tenant.",
        [typeof(LeaseEvent)] = "Append-only log reached only through its LeaseContract (tenant-filtered and owner-verified); written by services and jobs for an already verified lease.",
    };

    private static readonly Guid CallerOrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    [Fact]
    public void EntityTypes_EveryDbSetAndModelEntity_IsTenantOwnedOrAllowListed()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var undecided = AllEntityClrTypes(db)
            .Where(t => !typeof(ITenantOwned).IsAssignableFrom(t) && !NotTenantFiltered.ContainsKey(t))
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.True(
            undecided.Count == 0,
            "Entities neither ITenantOwned nor in the TN-2 allow-list: " + string.Join(", ", undecided)
            + ". Implement ITenantOwned (with the parent's OrgId) or add a motivated allow-list entry.");
    }

    [Fact]
    public void AllowList_EveryEntry_IsAMotivatedEntityThatIsNotTenantOwned()
    {
        using var db = NewNpgsqlContext(NullTenantContext.Instance);
        var entities = AllEntityClrTypes(db);

        Assert.All(NotTenantFiltered, entry =>
        {
            Assert.Contains(entry.Key, entities); // no stale entry
            Assert.False(typeof(ITenantOwned).IsAssignableFrom(entry.Key), $"{entry.Key.Name} is ITenantOwned and allow-listed");
            Assert.True(entry.Value.Length >= 40, $"{entry.Key.Name}: the reason must say why the entity stays unfiltered");
        });
    }

    [Fact]
    public void TenantOwnedEntities_InModel_CarryTheTenantQueryFilter()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var tenantOwned = db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .ToList();

        // The ten hand-written filters of US-004 + Guest (TN-1) + the TN-2 entities.
        Assert.True(tenantOwned.Count >= 19, $"Only {tenantOwned.Count} ITenantOwned entities in the model");
        Assert.All(tenantOwned, entityType =>
        {
            var filter = entityType.FindDeclaredQueryFilter(AppDbContext.TenantQueryFilter);
            Assert.True(filter?.Expression is not null, $"{entityType.ClrType.Name} has no tenant query filter");
            Assert.Contains(nameof(ITenantOwned.OrgId), filter!.Expression!.ToString(), StringComparison.Ordinal);
            Assert.False(entityType.FindProperty(nameof(ITenantOwned.OrgId))!.IsNullable, $"{entityType.ClrType.Name}.OrgId is nullable");
        });
    }

    [Theory]
    [InlineData(typeof(Property))]
    [InlineData(typeof(Booking))]
    [InlineData(typeof(Guest))]
    [InlineData(typeof(LeaseContract))]
    [InlineData(typeof(Payment))]
    [InlineData(typeof(PropertyFiscalYear))]
    [InlineData(typeof(RentSchedule))]
    [InlineData(typeof(RentLedgerEntry))]
    [InlineData(typeof(LeaseRegistrationAuthorization))]
    [InlineData(typeof(CalendarBlock))]
    [InlineData(typeof(PropertyICalFeed))]
    [InlineData(typeof(PropertyDocument))]
    [InlineData(typeof(OtaIntegration))]
    [InlineData(typeof(PricingAdapterConfig))]
    [InlineData(typeof(PricingHistory))]
    [InlineData(typeof(AlloggiatiWebReport))]
    [InlineData(typeof(GuestCheckInSession))]
    [InlineData(typeof(ConsentRecord))]
    [InlineData(typeof(PlatformInvoice))]
    public void TenantQueryFilter_AuthenticatedCaller_AddsOrgIdPredicateToSql(Type entityType)
    {
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(entityType));
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var sql = ToQueryString(db, entityType);

        Assert.Matches("\"OrgId\" = @", sql);
    }

    [Fact]
    public void TenantQueryFilter_IgnoreQueryFiltersByKey_RemovesOrgIdPredicate()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var sql = db.PropertyDocuments.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).ToQueryString();

        Assert.DoesNotMatch("\"OrgId\" = @", sql);
    }

    private static AppDbContext NewNpgsqlContext(ITenantContext tenant) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenant);

    private static HashSet<Type> AllEntityClrTypes(AppDbContext db)
    {
        var dbSetTypes = typeof(AppDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0]);

        var modelTypes = db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.HasSharedClrType)
            .Select(e => e.ClrType);

        return dbSetTypes.Concat(modelTypes).ToHashSet();
    }

    private static string ToQueryString(AppDbContext db, Type entityType)
    {
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(entityType)
            .Invoke(db, null)!;
        return ((IQueryable)set).ToQueryString();
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
