using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Infrastructure;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

internal sealed class StubArchiveSource(ArchiveDocumentType type, string contextKey, string permissionKey) : IDocumentArchiveSource
{
    public ArchiveDocumentType Type => type;

    public string ContextKey => contextKey;

    public string PermissionKey => permissionKey;

    public IQueryable<ArchiveRow> Query(HostScope scope) => Enumerable.Empty<ArchiveRow>().AsQueryable();

    public Task<ArchiveFile?> OpenAsync(Guid id, HostScope scope, CancellationToken cancellationToken) => Task.FromResult<ArchiveFile?>(null);
}

public class DocumentArchiveAccessTests
{
    private const string User = "auth0|someone";

    private static IReadOnlyList<IDocumentArchiveSource> Current()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var storage = new InMemoryFileStorage();
        return
        [
            new PropertyDocumentArchiveSource(db, storage),
            new LeaseDocumentArchiveSource(db, storage),
            new TouristTaxRemittanceArchiveSource(db, storage),
        ];
    }

    /// <summary>The sources the archive will have when the Alloggiati receipts and the ISTAT proofs come: both read <c>guest.read</c>.</summary>
    private static IReadOnlyList<IDocumentArchiveSource> WithComplianceProofs() =>
    [
        .. Current(),
        new StubArchiveSource(ArchiveDocumentType.AlloggiatiReceipt, OrgRoleCatalog.ShortRent, "guest.read"),
        new StubArchiveSource(ArchiveDocumentType.IstatProof, OrgRoleCatalog.ShortRent, "guest.read"),
    ];

    private static List<(string Context, IReadOnlyList<string> Permissions)> SeededHolding(string roleKey) =>
        [.. OrgRoleCatalog.SeededRoles.Where(r => r.RoleKey == roleKey).Select(r => (r.ContextKey, r.Permissions))];

    private static List<(string Context, IReadOnlyList<string> Permissions)> OwnerHolding() =>
        [.. ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner", "LongTermLandlord"]).Select(c => (c.ContextKey, c.Permissions))];

    private static Mock<IContextAuthorizationService> Holding(List<(string Context, IReadOnlyList<string> Permissions)> held)
    {
        var authorization = new Mock<IContextAuthorizationService>();
        authorization
            .Setup(a => a.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string context, string permission, CancellationToken _) =>
                held.Any(h => h.Context == context && h.Permissions.Contains(permission)));
        return authorization;
    }

    private static async Task<ArchiveDocumentType[]> TypesAsync(
        IEnumerable<IDocumentArchiveSource> sources, Mock<IContextAuthorizationService> authorization, string? userId = User) =>
        [.. (await DocumentArchiveAccessResolver.ResolveAsync(sources, authorization.Object, userId)).Types.Order()];

    /// <summary>
    /// PROOF TEST: permission rule. The Accountant holds <c>property.read</c>, <c>booking.read</c>, <c>payment.read</c> and <c>lease.read</c> and
    /// not <c>guest.read</c>, so it reads the property, lease and tax documents and never an Alloggiati receipt or an ISTAT proof, which carry
    /// guest data and ask <c>guest.read</c>. The owner, who holds it, reads them: the rule is the permission, not a list of roles.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_AccountantNeverSeesGuestProofs_Test()
    {
        var accountant = SeededHolding(OrgRoleCatalog.AccountantRoleKey);
        Assert.DoesNotContain(accountant, h => h.Permissions.Contains("guest.read"));

        var accountantTypes = await TypesAsync(WithComplianceProofs(), Holding(accountant));
        var ownerTypes = await TypesAsync(WithComplianceProofs(), Holding(OwnerHolding()));

        Assert.Equal(
            new[] { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax }.Order(),
            accountantTypes);
        Assert.DoesNotContain(ArchiveDocumentType.AlloggiatiReceipt, accountantTypes);
        Assert.DoesNotContain(ArchiveDocumentType.IstatProof, accountantTypes);
        Assert.Contains(ArchiveDocumentType.AlloggiatiReceipt, ownerTypes);
        Assert.Contains(ArchiveDocumentType.IstatProof, ownerTypes);
    }

    [Fact]
    public async Task ACollaborator_ReadsThePropertyDocuments_AndNeitherTheLeasesNorThePayments()
    {
        var types = await TypesAsync(Current(), Holding(SeededHolding(OrgRoleCatalog.StaffRoleKey)));

        Assert.Equal(new[] { ArchiveDocumentType.PropertyDocument }, types);
    }

    [Fact]
    public async Task TheOwnerOfBothAreas_ReadsEveryTypeThereIsASourceFor()
    {
        var types = await TypesAsync(Current(), Holding(OwnerHolding()));

        Assert.Equal(
            new[] { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax }.Order(),
            types);
    }

    [Fact]
    public async Task TheDocumentsOfTheProperties_AreReadByAPermissionOfEitherRentalContext()
    {
        var shortRentOnly = await TypesAsync(Current(), Holding([(OrgRoleCatalog.ShortRent, ["property.read"])]));
        var longRentOnly = await TypesAsync(Current(), Holding([(OrgRoleCatalog.LongRent, ["property.read"])]));
        var neither = await TypesAsync(Current(), Holding([(OrgRoleCatalog.ShortRent, ["booking.read"])]));

        Assert.Equal(new[] { ArchiveDocumentType.PropertyDocument }, shortRentOnly);
        Assert.Equal(new[] { ArchiveDocumentType.PropertyDocument }, longRentOnly);
        Assert.Empty(neither);
    }

    [Fact]
    public async Task APermissionCountsInTheContextThatGrantsIt()
    {
        var types = await TypesAsync(
            Current(), Holding([(OrgRoleCatalog.ShortRent, ["lease.read"]), (OrgRoleCatalog.LongRent, ["payment.read"])]));

        Assert.Empty(types);
    }

    [Fact]
    public async Task ATypeSharedByTwoSources_IsOpenOnlyWhenTheCallerMayReadBoth()
    {
        IDocumentArchiveSource[] sources =
        [
            new StubArchiveSource(ArchiveDocumentType.Invoice, OrgRoleCatalog.ShortRent, "payment.read"),
            new StubArchiveSource(ArchiveDocumentType.Invoice, OrgRoleCatalog.ShortRent, "ota.read"),
        ];

        var one = await TypesAsync(sources, Holding([(OrgRoleCatalog.ShortRent, ["payment.read"])]));
        var other = await TypesAsync(sources, Holding([(OrgRoleCatalog.ShortRent, ["ota.read"])]));
        var both = await TypesAsync(sources, Holding([(OrgRoleCatalog.ShortRent, ["payment.read", "ota.read"])]));

        Assert.Empty(one);
        Assert.Empty(other);
        Assert.Equal(new[] { ArchiveDocumentType.Invoice }, both);
    }

    [Fact]
    public async Task ACallerWithNoUserId_ReadsNothing_AndNothingIsAsked()
    {
        var authorization = Holding(OwnerHolding());

        var none = await TypesAsync(Current(), authorization, userId: " ");

        Assert.Empty(none);
        authorization.Verify(
            a => a.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EachDistinctPermission_IsAskedOnce_WhateverTheNumberOfSources()
    {
        IDocumentArchiveSource[] sources =
        [
            new StubArchiveSource(ArchiveDocumentType.Invoice, OrgRoleCatalog.ShortRent, "payment.read"),
            new StubArchiveSource(ArchiveDocumentType.Receipt, OrgRoleCatalog.ShortRent, "payment.read"),
            new StubArchiveSource(ArchiveDocumentType.CreditNote, OrgRoleCatalog.ShortRent, "payment.read"),
        ];
        var authorization = Holding([(OrgRoleCatalog.ShortRent, ["payment.read"])]);

        var types = await TypesAsync(sources, authorization);

        Assert.Equal(3, types.Length);
        authorization.Verify(
            a => a.HasPermissionAsync(User, OrgRoleCatalog.ShortRent, "payment.read", It.IsAny<CancellationToken>()), Times.Once);
    }
}
