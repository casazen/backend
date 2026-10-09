using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-03b, the crossing of the scope per property (AM-03) with the payments of the suppliers (SP-15a). The three host actions that
/// move money for an intervention (<c>final-amount/confirm</c>, <c>payment-session</c>, <c>mark-paid</c>, short-rent and long-rent)
/// are decided twice: the request is read inside the caller's scope (<c>GetByIdForHostAsync</c>, 404 outside it) and the row is
/// authorized as a <see cref="HostResource"/> that carries the id of its property, so a collaborator "Solo alcuni" cannot act on
/// the request of a property it was not given even if a service handed it the row. These tests run the real controllers over the
/// real authorization handler and the real scope resolver, with the service answering as it would if it had forgotten the scope.
/// The permission a collaborator holds is a separate matter: <c>servicerequest.write</c> covers create, match and mark-paid, and
/// the two actions that charge the host card or confirm a higher price stay on <c>property.write</c>, which a collaborator does not
/// have (the product owner decides whether it may).
/// </summary>
public class ServiceRequestPaymentScopeTests
{
    private const string Collaborator = "auth0|collaboratore";
    private static readonly Guid OrgId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Granted = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Hidden = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public enum HostAction
    {
        ConfirmFinalAmount,
        CreatePaymentSession,
        MarkPaid,
    }

    public enum Context
    {
        ShortRent,
        LongRent,
    }

    /// <summary>The permissions of the roles: what a collaborator holds, and what a manager holds on top.</summary>
    private static readonly string[] CollaboratorPermissions = ["property.read", "servicerequest.write"];
    private static readonly string[] ManagerPermissions = ["property.read", "property.write", "servicerequest.write"];

    private static readonly UserAuthorizationSnapshot SelectedCollaborator = new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgId: OrgId,
        OrgMember: new OrgMemberSnapshot(
            OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { Granted }));

    private static readonly UserAuthorizationSnapshot Manager = new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgId: OrgId,
        OrgMember: new OrgMemberSnapshot(OrgId, OrgRole.PropertyManager, OrgMemberStatus.Active));

    private sealed class Harness
    {
        public Mock<IServiceRequestService> Requests { get; } = new();
        public Mock<ISupplierPaymentService> Payments { get; } = new();
        public ControllerBase Controller { get; }
        public string[] Held { get; }

        public Harness(Context context, UserAuthorizationSnapshot snapshot, string[] permissions)
        {
            Held = permissions;
            var authorization = HostAuthorizationTestHarness.Create(
                OrgId,
                hasPermission: (contextKey, permission) =>
                    permissions.Contains(permission, StringComparer.Ordinal)
                    && string.Equals(contextKey, context == Context.ShortRent ? "short-rent" : "long-rent", StringComparison.Ordinal),
                scopeResolver: HostAuthorizationTestHarness.ScopeResolver(snapshot));
            var scopes = HostAuthorizationTestHarness.ScopeResolver(snapshot);
            var orgContext = new Mock<IOrgContextResolver>();
            orgContext.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);

            var http = new DefaultHttpContext
            {
                User = HostAuthorizationTestHarness.User(Collaborator),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            Controller = context == Context.ShortRent
                ? new ServiceRequestsController(
                    Requests.Object,
                    Mock.Of<ISupplierMatchService>(),
                    Mock.Of<IHostResourceLookup>(),
                    authorization,
                    orgContext.Object,
                    scopes,
                    Mock.Of<ISupplierOrgContextResolver>(),
                    Payments.Object)
                : new LongRentServiceRequestsController(
                    Requests.Object,
                    Mock.Of<ISupplierService>(),
                    Mock.Of<IComuneDirectory>(),
                    authorization,
                    orgContext.Object,
                    scopes,
                    new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
                    Payments.Object);
            Controller.ControllerContext = new ControllerContext { HttpContext = http };
        }

        /// <summary>The service answers with <paramref name="request"/> for the id, whatever the scope (or null: not found).</summary>
        public Guid Serves(ServiceRequest? request, Context context)
        {
            var id = request?.Id ?? Guid.NewGuid();
            var rental = context == Context.ShortRent ? ServiceRequestRentalContext.ShortRent : ServiceRequestRentalContext.LongRent;
            Requests
                .Setup(r => r.GetByIdForHostAsync(id, It.IsAny<HostScope>(), rental, It.IsAny<CancellationToken>()))
                .ReturnsAsync(request);
            if (request is not null)
            {
                Requests
                    .Setup(r => r.ConfirmFinalAmountAsync(id, OrgId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(request);
                Requests
                    .Setup(r => r.MarkPaidAsync(id, OrgId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(request);
                Payments
                    .Setup(p => p.CreateHostSessionAsync(id, OrgId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ServicePaymentSession(Guid.NewGuid(), "secret", "pk_test", "acct_test", 6_000, "eur"));
            }

            return id;
        }

        public async Task<IActionResult?> CallAsync(HostAction action, Guid id)
        {
            switch (Controller)
            {
                case ServiceRequestsController shortRent:
                    return action switch
                    {
                        HostAction.ConfirmFinalAmount => (await shortRent.ConfirmFinalAmount(id, CancellationToken.None)).Result,
                        HostAction.CreatePaymentSession => (await shortRent.CreatePaymentSession(id, CancellationToken.None)).Result,
                        _ => (await shortRent.MarkPaid(id, CancellationToken.None)).Result,
                    };
                case LongRentServiceRequestsController longRent:
                    return action switch
                    {
                        HostAction.ConfirmFinalAmount => (await longRent.ConfirmFinalAmount(id, CancellationToken.None)).Result,
                        HostAction.CreatePaymentSession => (await longRent.CreatePaymentSession(id, CancellationToken.None)).Result,
                        _ => (await longRent.MarkPaid(id, CancellationToken.None)).Result,
                    };
                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>True when the action went through to the service that moves the money or the state.</summary>
        public bool Executed(HostAction action, Guid id)
        {
            try
            {
                switch (action)
                {
                    case HostAction.ConfirmFinalAmount:
                        Requests.Verify(r => r.ConfirmFinalAmountAsync(id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
                        break;
                    case HostAction.CreatePaymentSession:
                        Payments.Verify(p => p.CreateHostSessionAsync(id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
                        break;
                    default:
                        Requests.Verify(r => r.MarkPaidAsync(id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
                        break;
                }

                return true;
            }
            catch (MockException)
            {
                return false;
            }
        }
    }

    private static ServiceRequest RequestOn(Guid propertyId, Context context) => new()
    {
        OrgId = OrgId,
        PropertyId = propertyId,
        Property = new Property { Id = propertyId, OrgId = OrgId, OwnerId = "auth0|titolare", Name = "Casa", City = "Ostuni", Address = "Via Roma 1" },
        SupplierOrgId = Guid.NewGuid(),
        RentalContext = context == Context.ShortRent ? ServiceRequestRentalContext.ShortRent : ServiceRequestRentalContext.LongRent,
        Category = "cleaning",
        Status = ServiceRequestStatus.Completato,
        FinalAmountNeedsConfirmation = true,
    };

    public static TheoryData<Context, HostAction> Actions
    {
        get
        {
            var data = new TheoryData<Context, HostAction>();
            foreach (var context in Enum.GetValues<Context>())
            {
                foreach (var action in Enum.GetValues<HostAction>())
                    data.Add(context, action);
            }

            return data;
        }
    }

    private static string[] Everything(Context context) => context == Context.ShortRent
        ? ManagerPermissions
        : ["property.read", "property.write"];

    // --- A request outside the reach of the caller ---------------------------------------------------

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ARequestOfAPropertyNotGiven_IsNotFoundWhenTheServiceScopesTheRead(Context context, HostAction action)
    {
        var harness = new Harness(context, SelectedCollaborator, Everything(context));
        var id = harness.Serves(null, context);

        var result = await harness.CallAsync(action, id);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.False(harness.Executed(action, id));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ARequestOfAPropertyNotGiven_IsForbidden_EvenWithEveryPermission_IfAServiceHandedTheRowOver(Context context, HostAction action)
    {
        // The second layer: the service returns the row of the hidden property as if it had forgotten the scope, the caller holds
        // every permission of the context, and the resource still carries the property it is bound to.
        var harness = new Harness(context, SelectedCollaborator, Everything(context));
        var id = harness.Serves(RequestOn(Hidden, context), context);

        var result = await harness.CallAsync(action, id);

        Assert.IsType<ForbidResult>(result);
        Assert.False(harness.Executed(action, id));
    }

    // --- A request of a property the collaborator was given ----------------------------------------------

    [Theory]
    [InlineData(HostAction.ConfirmFinalAmount)]
    [InlineData(HostAction.CreatePaymentSession)]
    public async Task ThePaymentsThatChargeOrConfirmAPrice_NeedPropertyWrite_ACollaboratorWithServiceRequestWriteIsRefused(HostAction action)
    {
        // The default of AM-03 stays: a collaborator marks an intervention paid but does not confirm a higher final amount or pay
        // online (property.write). Whether it may is a decision of the product owner.
        var harness = new Harness(Context.ShortRent, SelectedCollaborator, CollaboratorPermissions);
        var id = harness.Serves(RequestOn(Granted, Context.ShortRent), Context.ShortRent);

        var result = await harness.CallAsync(action, id);

        Assert.IsType<ForbidResult>(result);
        Assert.False(harness.Executed(action, id));
    }

    [Fact]
    public async Task MarkPaid_OnAPropertyTheCollaboratorWasGiven_IsAllowedWithServiceRequestWrite()
    {
        var harness = new Harness(Context.ShortRent, SelectedCollaborator, CollaboratorPermissions);
        var id = harness.Serves(RequestOn(Granted, Context.ShortRent), Context.ShortRent);

        var result = await harness.CallAsync(HostAction.MarkPaid, id);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(harness.Executed(HostAction.MarkPaid, id));
    }

    [Theory]
    [InlineData(HostAction.ConfirmFinalAmount)]
    [InlineData(HostAction.CreatePaymentSession)]
    [InlineData(HostAction.MarkPaid)]
    public async Task ThePositiveControl_WhoHoldsThePermissionAndReachesTheProperty_IsAllowed(HostAction action)
    {
        // Without this the refusals above could be a harness that refuses everything.
        var harness = new Harness(Context.ShortRent, SelectedCollaborator, ManagerPermissions);
        var id = harness.Serves(RequestOn(Granted, Context.ShortRent), Context.ShortRent);

        var result = await harness.CallAsync(action, id);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(harness.Executed(action, id));
    }

    [Theory]
    [InlineData(HostAction.ConfirmFinalAmount)]
    [InlineData(HostAction.CreatePaymentSession)]
    [InlineData(HostAction.MarkPaid)]
    public async Task AManagerWhoReachesEveryProperty_IsAllowedOnAnyOfThem(HostAction action)
    {
        var harness = new Harness(Context.ShortRent, Manager, ManagerPermissions);
        var id = harness.Serves(RequestOn(Hidden, Context.ShortRent), Context.ShortRent);

        var result = await harness.CallAsync(action, id);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(harness.Executed(action, id));
    }

    [Theory]
    [InlineData(HostAction.ConfirmFinalAmount)]
    [InlineData(HostAction.CreatePaymentSession)]
    [InlineData(HostAction.MarkPaid)]
    public async Task TheLongRentPositiveControl_AManagerWhoReachesTheProperty_IsAllowed(HostAction action)
    {
        var harness = new Harness(Context.LongRent, Manager, Everything(Context.LongRent));
        var id = harness.Serves(RequestOn(Hidden, Context.LongRent), Context.LongRent);

        var result = await harness.CallAsync(action, id);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(harness.Executed(action, id));
    }

    // --- A request that belongs to no property ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ARequestWithNoProperty_IsRefusedToEveryone_FailClosed(Context context, HostAction action)
    {
        var harness = new Harness(context, Manager, Everything(context));
        var orphan = RequestOn(Granted, context);
        orphan.PropertyId = null;
        orphan.Property = null;
        var id = harness.Serves(orphan, context);

        var result = await harness.CallAsync(action, id);

        // 403 on the short-rent endpoints; the long-rent ones, which are about properties only, answer 404.
        Assert.True(result is ForbidResult || result is ObjectResult { StatusCode: StatusCodes.Status404NotFound }, result?.GetType().Name);
        Assert.False(harness.Executed(action, id));
    }
}
