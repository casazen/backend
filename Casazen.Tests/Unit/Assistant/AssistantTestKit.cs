using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>What the tests of the assistant (E05/F05/US01) share: a database, the services over it at a fixed instant, a property with a listing worth reading, and the controllers' surroundings.</summary>
internal static class AssistantTestKit
{
    /// <summary>The instant the services run at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    // One provider for the whole test run: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>The name of a new InMemory store: contexts over the same name see the same data, as two requests over one database do.</summary>
    public static string NewStore() => $"assistant-{Guid.NewGuid()}";

    /// <summary>
    /// A context over <paramref name="store"/> with the encryption of the free text, like production, and no tenant context: the filter by organization is off (the
    /// tests of the services run like a job), so every service has to name the organization itself, which is what is proved.
    /// </summary>
    public static AppDbContext NewDb(string? store = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store ?? NewStore()).Options, tenantContext: null, Provider);

    public static AssistantRulesService Rules(AppDbContext db, TimeProvider? clock = null) =>
        new(db, NullLogger<AssistantRulesService>.Instance, clock ?? new FixedTimeProvider(Now));

    public static AssistantKnowledgeService Knowledge(AppDbContext db, TimeProvider? clock = null) =>
        new(db, Rules(db, clock), NullLogger<AssistantKnowledgeService>.Instance, clock ?? new FixedTimeProvider(Now));

    public static AssistantSituationService Situations(AppDbContext db, TimeProvider? clock = null) =>
        new(db, Rules(db, clock), NullLogger<AssistantSituationService>.Instance, clock ?? new FixedTimeProvider(Now));

    public static KnowledgeEntryInput Faq(string title = "Posso arrivare prima?", string text = "Sì, dalle 14 se la casa è pronta.", KnowledgeTopic topic = KnowledgeTopic.CheckIn) =>
        new(topic, KnowledgeEntryKind.Faq, title, text, null, null);

    public static KnowledgeEntryInput Fact(
        KnowledgeTopic topic,
        string text,
        string? language = null,
        bool? isActive = null,
        KnowledgeEntryKind kind = KnowledgeEntryKind.Fact) =>
        new(topic, kind, null, text, language, isActive);

    /// <summary>The rules of an organization as the host's screen would send them, the threshold and the lists as given.</summary>
    public static AssistantRulesInput RulesInput(
        bool enabled = true,
        AssistantTone tone = AssistantTone.Friendly,
        IReadOnlyList<string>? languages = null,
        string? signature = "Marco, Casa Rossi",
        decimal? threshold = 0.9m,
        IReadOnlyList<string>? forbidden = null,
        IReadOnlyList<string>? allowed = null,
        TimeOnly? from = null,
        TimeOnly? to = null) =>
        new(enabled, tone, languages ?? ["it", "en", "de"], signature, from, to, allowed, forbidden, threshold);

    /// <summary>A property of <paramref name="orgId"/> with a listing the knowledge base can be read from.</summary>
    public static async Task<Property> AddPropertyAsync(AppDbContext db, Guid orgId, string name = "Trullo dei Sogni", Action<Property>? configure = null)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = name,
            Address = "Via degli Ulivi 12",
            City = "Ostuni",
            PostalCode = "72017",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        configure?.Invoke(property);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return property;
    }

    /// <summary>A complete listing: every field the builder reads is filled, in Italian and in English where the page takes both.</summary>
    public static void FullListing(Property property)
    {
        property.CheckInFrom = new TimeOnly(15, 0);
        property.CheckInTo = new TimeOnly(20, 30);
        property.CheckOutBy = new TimeOnly(10, 0);
        property.Amenities = [PropertyAmenity.WiFi, PropertyAmenity.FreeParking, PropertyAmenity.Garage, PropertyAmenity.Pool, PropertyAmenity.AirConditioning];
        property.HouseRules = "Niente scarpe in casa.";
        property.SmokingPolicy = SmokingPolicy.OutdoorsOnly;
        property.PartiesPolicy = PartiesPolicy.No;
        property.ChildrenPolicy = ChildrenPolicy.Yes;
        property.PetsPolicy = PetsPolicy.OnRequest;
        property.QuietHoursFrom = new TimeOnly(22, 0);
        property.QuietHoursTo = new TimeOnly(7, 30);
        property.SiteProfileJson = SitePropertyProfileJson.Serialize(new SitePropertyProfileDocument
        {
            Zone = new LocalizedText("Campagna di Ostuni", "Ostuni countryside"),
            Near = new LocalizedText("10 minuti dal mare"),
            FloorNote = new LocalizedText("Piano terra", "Ground floor"),
            OutdoorNote = new LocalizedText("Giardino con uliveto", "Garden with olive grove"),
            CheckInMethod = new LocalizedText("In autonomia, con cassetta a codice", "Self check-in with a key box"),
            RulesNote = new LocalizedText("Rispettate il vicinato"),
        });
    }

    // ─── the controllers' surroundings: the real scope resolver and the real resource handler over the scenario of the scope tests ───

    /// <summary>
    /// The real scope resolver: the collaborator of the scenario is «Solo alcuni» and was given the first property only; the administrator (<paramref name="administrator"/>) reaches the
    /// whole organization.
    /// </summary>
    public static IHostScopeResolver ScopeOf(HostScopeWorld world, bool administrator = false)
    {
        var member = administrator
            ? new OrgMemberSnapshot(world.OrgId, OrgRole.Owner, OrgMemberStatus.Active)
            : new OrgMemberSnapshot(world.OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { world.Granted.Id });
        return HostAuthorizationTestHarness.ScopeResolver(
            new UserAuthorizationSnapshot(Exists: true, IsActive: true, Role: UserRole.None, SupplierOrgId: null, Memberships: [], OrgId: world.OrgId, OrgMember: member));
    }

    /// <summary>
    /// A real <see cref="IAuthorizationService"/> with the real resource handler: org and reach are the production ones, the permissions are granted unless <paramref name="hasPermission"/>
    /// says no. <paramref name="administrator"/> is also the answer of the policy of the organization's administrators (<see cref="CasazenPolicies.OrgBillingAdmin"/>), which the harness
    /// does not register: the collaborator «Solo alcuni» is not one, the owner is.
    /// </summary>
    public static IAuthorizationService Authorization(HostScopeWorld world, bool administrator = false, Func<string, string, bool>? hasPermission = null) =>
        new OrganizationAdministratorAuthorization(HostAuthorizationTestHarness.Create(world.OrgId, hasPermission, ScopeOf(world, administrator)), administrator);

    public static ClaimsPrincipal Collaborator(HostScopeWorld world) => HostAuthorizationTestHarness.User(world.CollaboratorId);

    public static ClaimsPrincipal Administrator(HostScopeWorld world) => HostAuthorizationTestHarness.User(world.OwnerUserId, "PropertyOwner");

    /// <summary>The property records as the application reads them: the row, whatever the org (the controller checks the org and the reach itself).</summary>
    public static IPropertyService Properties(AppDbContext db)
    {
        var properties = new Mock<IPropertyService>();
        properties
            .Setup(p => p.GetPropertyRecordAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => db.Properties.AsNoTracking().FirstOrDefault(p => p.Id == id));
        return properties.Object;
    }

    public static IOrgContextResolver Org(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return resolver.Object;
    }

    public static ControllerContext ContextOf(ClaimsPrincipal user) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer()).BuildServiceProvider(),
        },
    };

    /// <summary>The service of the harness for the checks of a row and for the policies it knows, and a fixed answer for the policy of the organization's administrators.</summary>
    private sealed class OrganizationAdministratorAuthorization(IAuthorizationService inner, bool administrator) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            inner.AuthorizeAsync(user, resource, requirements);

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            policyName == CasazenPolicies.OrgBillingAdmin
                ? Task.FromResult(administrator ? AuthorizationResult.Success() : AuthorizationResult.Failed())
                : inner.AuthorizeAsync(user, resource, policyName);
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
