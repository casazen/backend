using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The invitation of the owners to the portal, from the import to the acceptance (E10/F02/US02/T02, NF-109, ADR-011): the batch is previewed and
/// then confirmed with the fingerprint of the preview, the import never sends, the lifecycle is told on every read, and the acceptance is one-time,
/// idempotent for the same account and grants only the ownerships of the invitation. The three proofs of the brief come first.
/// </summary>
public class OwnerInvitationFlowTests : IAsyncLifetime
{
    private const string Admin = "auth0|admin";
    private const string PublicAddress = "https://app.example.test";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly RecordingEmailQueue _queue = new();
    private readonly Mock<IAuth0ManagementService> _auth0 = new();
    private AppDbContext _db = null!;
    private HostScopeWorld _world = null!;

    private Guid Granted => _world.Granted.Id;

    private Guid Hidden => _world.Hidden.Id;

    public async Task InitializeAsync()
    {
        _db = NewDb();
        _world = await HostScopeScenario.SeedAsync(_db);
        _auth0
            .Setup(a => a.SetOwnerPortalClaimsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (data, permission): the owner import sends no invitation, and a batch is sent only by the confirmation of a preview. Without the
    /// fingerprint of the preview nothing is sent; with the fingerprint of an old preview (an address changed since) nothing is sent either; with the
    /// right one the invitations go, one for each recipient, each with a line in the activity log.
    /// </summary>
    [Fact]
    public async Task OwnerInvitation_ImportDoesNotSendAndConfirmRequiresPreview_Test()
    {
        var csv = "propertyId,ownerKey,legalName,email,sharePercent,ownershipStart\n"
                  + $"{Granted},A-1,Anna Verdi,anna@example.com,100,2025-01-01\n"
                  + $"{Hidden},B-1,Bruno Neri,bruno@example.com,100,2025-01-01";
        await using (var importDb = NewDb())
        {
            var onboarding = Onboarding(importDb);
            var imported = await onboarding.ImportAsync(_world.OrgWide, Admin, "owners.csv", Encoding.UTF8.GetBytes(csv), dryRun: false, Ct);
            Assert.Equal(2, imported.Summary.Applied);
        }

        Assert.Empty(_queue.Sent);
        Assert.Empty(await _db.OwnerInvitations.AsNoTracking().ToListAsync());

        var service = Service(_db);
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => service.ConfirmBatchAsync(_world.OrgWide, Admin, null, null, Ct));
        Assert.Equal(OwnerInvitationErrors.PreviewRequired, missing.Code);
        var forged = await Assert.ThrowsAsync<DomainConflictException>(() => service.ConfirmBatchAsync(_world.OrgWide, Admin, null, new string('a', 64), Ct));
        Assert.Equal(OwnerInvitationErrors.PreviewStale, forged.Code);
        Assert.Empty(_queue.Sent);

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        Assert.Equal(["Anna Verdi", "Bruno Neri"], preview.Recipients.Select(r => r.DisplayName));
        Assert.Empty(preview.Exclusions);
        Assert.Equal((100, 0, 100), (preview.Quota.Limit, preview.Quota.Used, preview.Quota.Remaining));
        Assert.Empty(_queue.Sent);

        var anna = await _db.PropertyOwners.SingleAsync(o => o.ExternalKey == "A-1");
        anna.Email = "anna.nuova@example.com";
        await _db.SaveChangesAsync();
        var stale = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, preview.Fingerprint, Ct));
        Assert.Equal(OwnerInvitationErrors.PreviewStale, stale.Code);
        Assert.Empty(_queue.Sent);

        var fresh = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        Assert.NotEqual(preview.Fingerprint, fresh.Fingerprint);
        var result = await Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, fresh.Fingerprint, Ct);

        Assert.Equal((2, 0), (result.Sent, result.Failed.Count));
        Assert.Equal(["anna.nuova@example.com", "bruno@example.com"], _queue.Sent.Select(s => s.To).Order());
        Assert.All(_queue.Sent, s => Assert.Contains($"{PublicAddress}/portale-proprietari/invito?token=", s.Content.HtmlBody, StringComparison.Ordinal));
        Assert.All(await _db.OwnerInvitations.AsNoTracking().ToListAsync(), i => Assert.Equal(OwnerInvitationStatus.Pending, i.Status));
        var lines = await _db.OrgActivityEntries.AsNoTracking().Where(l => l.Type == OrgActivityType.OwnerInvitationChanged).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal((Admin, OrgActivitySubjectType.OwnerInvitation, "{\"status\":\"sent\"}"), (l.ActorUserId, l.SubjectType, l.DetailsJson)));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, fresh.Fingerprint, Ct));
        Assert.Equal(2, _queue.Sent.Count);
    }

    [Fact]
    public async Task OwnerInvitation_AgencyDomainWithOwnerSignInReady_LinksToTheAgencyDomain_Test()
    {
        const string domain = "proprietari.agenzia-inviti.test";
        var org = await _db.Orgs.SingleAsync(o => o.Id == _world.OrgId);
        org.PlanTier = PlanTier.Pro;
        org.PublicHostMode = PublicHostMode.CustomDomain;
        org.CustomDomain = domain;
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        org.OwnerPortalOnOwnDomain = true;
        org.UseOwnDomainForLinks = true;
        org.OwnerLoginHostState = OwnerLoginHostState.Pending;
        await _db.SaveChangesAsync();
        var waiting = await AddOwnerAsync("Anna Verdi", "anna@example.com");
        var ready = await AddOwnerAsync("Bruno Neri", "bruno@example.com");

        await Service(NewDb(), withAgencyLinks: true).CreateInvitationAsync(_world.OrgId, waiting, Admin, Ct);
        var pending = _queue.Sent[^1].Content.HtmlBody;
        org.OwnerLoginHostState = OwnerLoginHostState.Ready;
        await _db.SaveChangesAsync();
        await Service(NewDb(), withAgencyLinks: true).CreateInvitationAsync(_world.OrgId, ready, Admin, Ct);
        var onTheDomain = _queue.Sent[^1].Content.HtmlBody;

        Assert.Contains($"{PublicAddress}/portale-proprietari/invito?token=", pending, StringComparison.Ordinal);
        Assert.DoesNotContain(domain, pending, StringComparison.Ordinal);
        Assert.Contains($"https://{domain}/portale-proprietari/invito?token=", onTheDomain, StringComparison.Ordinal);
        Assert.Matches("token=[0-9a-f]{64}", onTheDomain);
    }

    /// <summary>
    /// Proof (idempotency, permission): accepting twice with the same account finds it done and answers the same, with one identity and one line in
    /// the log; the token is single use for everybody else; an account whose e-mail is not verified, or is not the invited one, is refused.
    /// </summary>
    [Fact]
    public async Task OwnerInvitation_AcceptTwiceIsIdempotent_Test()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var token = await InviteAsync(owner);
        var service = Service(NewDb());

        var stored = Assert.Single(await _db.OwnerInvitations.AsNoTracking().ToListAsync());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);

        var first = await service.AcceptAsync(Accept(token, "auth0|anna", "Anna@Example.com"), Ct);
        var second = await Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct);

        Assert.False(first.AlreadyAccepted);
        Assert.True(second.AlreadyAccepted);
        Assert.Equal(first.OwnerUserId, second.OwnerUserId);
        Assert.Equal(first.AgencyIds, second.AgencyIds);
        Assert.Equal(first.PropertyIds, second.PropertyIds);
        Assert.Equal([_world.OrgId], first.AgencyIds);
        Assert.Equal([Granted], first.PropertyIds);
        Assert.Equal(OwnerClaimsRefresh.Published, first.ClaimsRefresh);

        var user = Assert.Single(await _db.OwnerUsers.AsNoTracking().ToListAsync());
        Assert.Equal(("auth0|anna", first.OwnerUserId), (user.Auth0Sub, user.Id));
        Assert.Equal(first.OwnerUserId, Assert.Single(await _db.PropertyOwnerships.AsNoTracking().ToListAsync()).OwnerUserId);
        var invitation = Assert.Single(await _db.OwnerInvitations.AsNoTracking().ToListAsync());
        Assert.Equal((OwnerInvitationStatus.Accepted, first.OwnerUserId), (invitation.Status, invitation.OwnerUserId));
        Assert.Single(await _db.OrgActivityEntries.AsNoTracking().Where(l => l.DetailsJson == "{\"status\":\"accepted\"}").ToListAsync());
        _auth0.Verify(
            a => a.SetOwnerPortalClaimsAsync("auth0|anna", first.OwnerUserId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        var other = await Assert.ThrowsAsync<DomainGoneException>(() => Service(NewDb()).AcceptAsync(Accept(token, "auth0|someone-else", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Used, other.Code);
        Assert.Single(await _db.OwnerUsers.AsNoTracking().ToListAsync());

        var secondOwner = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);
        var secondToken = await InviteAsync(secondOwner);
        var unverified = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => Service(NewDb()).AcceptAsync(Accept(secondToken, "auth0|bruno", "bruno@example.com", verified: false), Ct));
        Assert.Equal(OwnerInvitationErrors.EmailNotVerified, unverified.Code);
        var wrongAddress = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => Service(NewDb()).AcceptAsync(Accept(secondToken, "auth0|bruno", "intruso@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.EmailMismatch, wrongAddress.Code);
        Assert.Equal(1, await _db.OwnerUsers.CountAsync());
        Assert.Null((await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.PropertyId == Hidden)).OwnerUserId);

        var unknown = await Assert.ThrowsAsync<DomainGoneException>(() => Service(NewDb()).AcceptAsync(Accept(new string('0', 64), "auth0|anna", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Invalid, unknown.Code);
        var malformed = await Assert.ThrowsAsync<DomainGoneException>(() => Service(NewDb()).AcceptAsync(Accept("not-a-token", "auth0|anna", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Invalid, malformed.Code);
    }

    /// <summary>
    /// Proof (tenant isolation): the acceptance grants only the ownerships of the invitation: of the org that invited, of that registry owner, on a
    /// property of that org. A period of the same registry owner in another org, and one that points at another org's property, are not granted; nor
    /// is one that is over.
    /// </summary>
    [Fact]
    public async Task OwnerInvitation_CannotGrantAnotherOrganizationsProperty_Test()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var ended = await AddPeriodAsync(_world.OrgId, Hidden, owner, new DateOnly(2018, 1, 1), new DateOnly(2020, 12, 31));
        var foreignOrg = await AddPeriodAsync(_world.OtherOrgId, _world.OtherOrgProperty.Id, owner, new DateOnly(2025, 1, 1));
        var tampered = await AddPeriodAsync(_world.OrgId, _world.OtherOrgProperty.Id, owner, new DateOnly(2025, 1, 1));
        var token = await InviteAsync(owner);

        var result = await Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct);

        Assert.Equal([Granted], result.PropertyIds);
        Assert.Equal([_world.OrgId], result.AgencyIds);
        var stored = await _db.PropertyOwnerships.AsNoTracking().ToListAsync();
        Assert.Equal(result.OwnerUserId, stored.Single(p => p.PropertyId == Granted).OwnerUserId);
        Assert.Null(stored.Single(p => p.Id == ended).OwnerUserId);
        Assert.Null(stored.Single(p => p.Id == foreignOrg).OwnerUserId);
        Assert.Null(stored.Single(p => p.Id == tampered).OwnerUserId);

        var foreignOwner = new PropertyOwner { OrgId = _world.OtherOrgId, FirstName = "Altro", LastName = "Org", Email = "altro@example.com", CreatedAt = DateTime.UtcNow };
        _db.PropertyOwners.Add(foreignOwner);
        await _db.SaveChangesAsync();
        var notFound = await Assert.ThrowsAsync<NotFoundException>(() => Service(NewDb()).CreateInvitationAsync(_world.OrgId, foreignOwner.Id, Admin, Ct));
        Assert.Equal(OwnerInvitationErrors.PropertyOwnerNotFound, notFound.Code);
        var unknownOwner = await Assert.ThrowsAsync<NotFoundException>(() => Service(NewDb()).CreateInvitationAsync(_world.OrgId, Guid.NewGuid(), Admin, Ct));
        Assert.Equal(notFound.Code, unknownOwner.Code);
    }

    [Fact]
    public async Task Accept_AnOwnershipThatStartsLaterIsNotGranted_UntilItHasStarted()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var later = await AddPeriodAsync(_world.OrgId, Hidden, owner, new DateOnly(2026, 10, 20));
        var token = await InviteAsync(owner);

        var first = await Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct);

        Assert.Equal([Granted], first.PropertyIds);
        Assert.Null((await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.Id == later)).OwnerUserId);

        _clock.Advance(TimeSpan.FromDays(12));
        var second = await Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct);

        Assert.True(second.AlreadyAccepted);
        Assert.Equal(new[] { Granted, Hidden }.Order(), second.PropertyIds);
        Assert.Equal(first.OwnerUserId, (await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.Id == later)).OwnerUserId);
    }

    [Fact]
    public async Task Preview_LeavesOutWhoIsNotReady_WithTheReasons_AndWhoCannotBeInvited()
    {
        var ready = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var noOwnership = await AddOwnerAsync("Bruno Neri", "bruno@example.com");
        var noEmail = await AddReadyOwnerAsync("Carla Gialli", string.Empty, Hidden, share: 50m);
        var invited = await AddReadyOwnerAsync("Dario Blu", "dario@example.com", Hidden, share: 50m);
        await InviteAsync(invited);

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);

        Assert.Equal([ready], preview.Recipients.Select(r => r.OwnerId));
        var reasons = preview.Exclusions.ToDictionary(x => x.OwnerId, x => x.Reasons);
        Assert.Contains(OwnerReadinessReasons.OwnershipNotReady, reasons[noOwnership]);
        Assert.Contains(OwnerReadinessReasons.EmailMissing, reasons[noEmail]);
        Assert.Contains(OwnerInvitationExclusions.AlreadyInvited, reasons[invited]);
    }

    [Fact]
    public async Task Preview_AnEmptyListIsRefused_NeverMeansAll_AndAnUnknownIdIsOneAnswerWithNoName()
    {
        await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var foreignOwner = new PropertyOwner { OrgId = _world.OtherOrgId, FirstName = "Altro", LastName = "Org", Email = "altro@example.com", CreatedAt = DateTime.UtcNow };
        _db.PropertyOwners.Add(foreignOwner);
        await _db.SaveChangesAsync();

        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => Service(NewDb()).PreviewBatchAsync(_world.OrgWide, [], Ct));
        Assert.Equal(OwnerInvitationErrors.SelectionEmpty, empty.Code);

        Guid[] selection = [foreignOwner.Id, Guid.NewGuid()];
        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, selection, Ct);
        Assert.Empty(preview.Recipients);
        Assert.All(preview.Exclusions, x =>
        {
            Assert.Equal([OwnerInvitationExclusions.OwnerNotFound], x.Reasons);
            Assert.Null(x.DisplayName);
        });
        var nothing = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, selection, preview.Fingerprint, Ct));
        Assert.Equal(OwnerInvitationErrors.NothingToSend, nothing.Code);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Preview_ACollaboratorWithSomePropertiesInvitesOnlyTheOwnersInsideItsReach()
    {
        var onGranted = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var onHidden = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.Restricted, null, Ct);
        Assert.Equal([onGranted], preview.Recipients.Select(r => r.OwnerId));

        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => Service(NewDb()).CreateInvitationAsync(_world.OrgId, onHidden, Admin, Ct, _world.Restricted));
        Assert.Equal(OwnerInvitationErrors.PropertyOwnerNotFound, hidden.Code);
        Assert.Empty(_queue.Sent);
        await Service(NewDb()).CreateInvitationAsync(_world.OrgId, onGranted, Admin, Ct, _world.Restricted);
        Assert.Single(_queue.Sent);

        var named = await Service(NewDb()).PreviewBatchAsync(_world.Restricted, [onHidden], Ct);
        Assert.Empty(named.Recipients);
        var hiddenOwner = Assert.Single(named.Exclusions);
        Assert.Equal([OwnerInvitationExclusions.OwnerNotFound], hiddenOwner.Reasons);
        Assert.Null(hiddenOwner.DisplayName);
    }

    [Fact]
    public async Task ACollaboratorWithSomePropertiesDoesNotOfferALinkToAnOwnerWhoAlsoHoldsPropertiesItDoesNotReach()
    {
        var shared = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted, share: 50m);
        await AddPeriodAsync(_world.OrgId, Hidden, shared, new DateOnly(2025, 1, 1));
        var alone = await AddReadyOwnerAsync("Carla Gialli", "carla@example.com", Granted, share: 50m);

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.Restricted, null, Ct);

        Assert.Equal([alone], preview.Recipients.Select(r => r.OwnerId));
        var excluded = Assert.Single(preview.Exclusions);
        Assert.Equal(shared, excluded.OwnerId);
        Assert.Equal([OwnerInvitationExclusions.SharedOutsideReach], excluded.Reasons);
        var sent = await Service(NewDb()).ConfirmBatchAsync(_world.Restricted, Admin, null, preview.Fingerprint, Ct);
        Assert.Equal(1, sent.Sent);
        Assert.Equal(["carla@example.com"], _queue.Sent.Select(s => s.To));

        var orgWide = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        Assert.Equal([shared], orgWide.Recipients.Select(r => r.OwnerId));

        var single = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(NewDb()).CreateInvitationAsync(_world.OrgId, shared, Admin, Ct, _world.Restricted));
        Assert.Equal(OwnerInvitationErrors.SharedOutsideReach, single.Code);
        await Service(NewDb()).CreateInvitationAsync(_world.OrgId, shared, Admin, Ct);
        var invitation = await _db.OwnerInvitations.AsNoTracking().SingleAsync(i => i.PropertyOwnerId == shared);
        var resend = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(NewDb()).ResendAsync(_world.Restricted, Admin, invitation.Id, Ct));
        Assert.Equal(OwnerInvitationErrors.SharedOutsideReach, resend.Code);
        Assert.Equal(2, _queue.Sent.Count);
    }

    [Fact]
    public async Task Preview_AnOwnerWhoseEveryPeriodIsOver_IsNotOffered_AfterAHandOver()
    {
        // Anna held Granted until the end of 2025 and sold it to Bruno, who holds it since 2026: she has nothing the link could open.
        var former = await AddOwnerAsync("Anna Verdi", "anna@example.com");
        await AddPeriodAsync(_world.OrgId, Granted, former, new DateOnly(2020, 1, 1), new DateOnly(2025, 12, 31));
        var current = await AddOwnerAsync("Bruno Neri", "bruno@example.com");
        await AddPeriodAsync(_world.OrgId, Granted, current, new DateOnly(2026, 1, 1));

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        var list = await Service(NewDb()).ListAsync(_world.OrgWide, Ct);

        Assert.Equal([current], preview.Recipients.Select(r => r.OwnerId));
        Assert.Empty(preview.Exclusions);
        Assert.Equal([current], list.Select(i => i.OwnerId));
    }

    [Fact]
    public async Task TheListAndThePreviewSeeEveryOwner_EvenPastTheCutOfTheReadiness()
    {
        var total = OwnerOnboardingLimits.MaxListed + 1;
        _db.PropertyOwners.AddRange(Enumerable.Range(0, total).Select(i => new PropertyOwner
        {
            OrgId = _world.OrgId,
            FirstName = "Owner",
            LastName = $"{i:D4}",
            LegalName = $"Owner {i:D4}",
            Email = $"owner{i}@example.com",
            CreatedAt = DateTime.UtcNow,
        }));
        await _db.SaveChangesAsync();
        var last = await _db.PropertyOwners.AsNoTracking().SingleAsync(o => o.LegalName == $"Owner {total - 1:D4}");

        var list = await Service(NewDb()).ListAsync(_world.OrgWide, Ct);
        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, [last.Id], Ct);

        Assert.Equal(total, list.Count);
        Assert.Contains(list, i => i.OwnerId == last.Id);
        var excluded = Assert.Single(preview.Exclusions);
        Assert.DoesNotContain(OwnerInvitationExclusions.OwnerNotFound, excluded.Reasons);
    }

    [Fact]
    public async Task Confirm_ABatchThatDoesNotFitTheLimitOfTheDay_IsRefusedWholeAndNoneIsSent()
    {
        await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);
        _db.OwnerInvitations.Add(new OwnerInvitation
        {
            OrgId = _world.OrgId,
            PropertyOwnerId = Guid.NewGuid(),
            Email = "x@example.com",
            TokenHash = new string('b', 64),
            Status = OwnerInvitationStatus.Revoked,
            ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(3),
            InvitedByUserId = Admin,
            SendLog = [.. Enumerable.Repeat(_clock.GetUtcNow().UtcDateTime.AddHours(-2), 99)],
        });
        await _db.SaveChangesAsync();

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        Assert.Equal((100, 99, 1), (preview.Quota.Limit, preview.Quota.Used, preview.Quota.Remaining));

        var refused = await Assert.ThrowsAsync<DomainTooManyRequestsException>(
            () => Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, preview.Fingerprint, Ct));
        Assert.Equal(OwnerInvitationErrors.DailyLimitReached, refused.Code);
        Assert.Empty(_queue.Sent);
        Assert.Equal(1, await _db.OwnerInvitations.CountAsync());
    }

    [Fact]
    public async Task Resend_ReplacesTheLinkInForce_TheOldTokenStopsWorking_AndOnlyOneIsLive()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var oldToken = await InviteAsync(owner);
        var oldInvitation = await _db.OwnerInvitations.AsNoTracking().SingleAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        var item = await Service(NewDb()).ResendAsync(_world.OrgWide, Admin, oldInvitation.Id, Ct);

        Assert.Equal(OwnerPortalStates.Invited, item.State);
        Assert.NotEqual(oldInvitation.Id, item.InvitationId);
        var all = await _db.OwnerInvitations.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all.Count(i => i.Status == OwnerInvitationStatus.Pending));
        Assert.Equal(OwnerInvitationStatus.Revoked, all.Single(i => i.Id == oldInvitation.Id).Status);
        Assert.Equal(2, _queue.Sent.Count);

        var gone = await Assert.ThrowsAsync<DomainGoneException>(
            () => Service(NewDb()).AcceptAsync(Accept(oldToken, "auth0|anna", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Revoked, gone.Code);
        var newToken = TokenOf(_queue.Sent[^1]);
        var accepted = await Service(NewDb()).AcceptAsync(Accept(newToken, "auth0|anna", "anna@example.com"), Ct);
        Assert.Equal([Granted], accepted.PropertyIds);

        var resendUsed = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service(NewDb()).ResendAsync(_world.OrgWide, Admin, all[1].Id, Ct));
        Assert.Equal(OwnerInvitationErrors.Used, resendUsed.Code);
        var revokeUsed = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service(NewDb()).RevokeAsync(_world.OrgWide, Admin, all[1].Id, Ct));
        Assert.Equal(OwnerInvitationErrors.Used, revokeUsed.Code);
    }

    [Fact]
    public async Task Revoke_WithdrawsTheLink_ASecondTimeWritesNothing_AndAnotherOrgsInvitationIsNotFound()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var token = await InviteAsync(owner);
        var invitation = await _db.OwnerInvitations.AsNoTracking().SingleAsync();

        var first = await Service(NewDb()).RevokeAsync(_world.OrgWide, Admin, invitation.Id, Ct);
        var second = await Service(NewDb()).RevokeAsync(_world.OrgWide, Admin, invitation.Id, Ct);

        Assert.Equal((OwnerPortalStates.Revoked, OwnerPortalStates.Revoked), (first.State, second.State));
        Assert.Single(await _db.OrgActivityEntries.AsNoTracking().Where(l => l.DetailsJson == "{\"status\":\"revoked\"}").ToListAsync());
        var gone = await Assert.ThrowsAsync<DomainGoneException>(
            () => Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Revoked, gone.Code);

        var other = new OwnerInvitation
        {
            OrgId = _world.OtherOrgId,
            PropertyOwnerId = Guid.NewGuid(),
            Email = "x@example.com",
            TokenHash = new string('c', 64),
            ExpiresAt = DateTime.UtcNow.AddDays(3),
            InvitedByUserId = Admin,
        };
        _db.OwnerInvitations.Add(other);
        await _db.SaveChangesAsync();
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => Service(NewDb()).RevokeAsync(_world.OrgWide, Admin, other.Id, Ct));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => Service(NewDb()).RevokeAsync(_world.OrgWide, Admin, Guid.NewGuid(), Ct));
        Assert.Equal((OwnerInvitationErrors.NotFound, OwnerInvitationErrors.NotFound), (foreign.Code, unknown.Code));
    }

    [Fact]
    public async Task Lifecycle_ALinkThatLapsedIsExpired_AndTheOwnerCanBeInvitedAgain()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var token = await InviteAsync(owner);

        var before = Assert.Single(await Service(NewDb()).ListAsync(_world.OrgWide, Ct));
        Assert.Equal(OwnerPortalStates.Invited, before.State);

        _clock.Advance(TimeSpan.FromDays(8));
        var after = Assert.Single(await Service(NewDb()).ListAsync(_world.OrgWide, Ct));
        Assert.Equal((OwnerPortalStates.Expired, before.InvitationId), (after.State, after.InvitationId));
        var expired = await Assert.ThrowsAsync<DomainGoneException>(
            () => Service(NewDb()).AcceptAsync(Accept(token, "auth0|anna", "anna@example.com"), Ct));
        Assert.Equal(OwnerInvitationErrors.Expired, expired.Code);

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        Assert.Equal([owner], preview.Recipients.Select(r => r.OwnerId));
        await Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, preview.Fingerprint, Ct);
        var rows = await _db.OwnerInvitations.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync();
        Assert.Equal([OwnerInvitationStatus.Expired, OwnerInvitationStatus.Pending], rows.Select(r => r.Status));
    }

    [Fact]
    public async Task Lifecycle_EveryOwnerIsListedWithItsState()
    {
        var accepted = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var invited = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);
        var notReady = await AddOwnerAsync("Carla Gialli", "carla@example.com");
        await AcceptAsync(accepted, "auth0|anna", "anna@example.com");
        await InviteAsync(invited);

        var list = await Service(NewDb()).ListAsync(_world.OrgWide, Ct);

        var states = list.ToDictionary(i => i.OwnerId, i => i.State);
        Assert.Equal(OwnerPortalStates.Accepted, states[accepted]);
        Assert.Equal(OwnerPortalStates.Invited, states[invited]);
        Assert.Equal(OwnerPortalStates.NotReady, states[notReady]);
        Assert.NotNull(list.Single(i => i.OwnerId == accepted).AcceptedAt);
        Assert.Null(list.Single(i => i.OwnerId == notReady).InvitationId);
    }

    [Fact]
    public async Task TheLinkIsBuiltFromTheConfiguredPublicAddress_NeverFromADomainInTheCode()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        await InviteAsync(owner);

        var email = Assert.Single(_queue.Sent);

        Assert.Contains($"{PublicAddress}/portale-proprietari/invito?token=", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("app.casazen.com", email.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);

        var unconfigured = new PublicSiteLinks(Options.Create(new PublicSiteOptions()));
        var other = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);
        await using var db = NewDb();
        var service = new OwnerInvitationService(
            db, _queue, NullLogger<OwnerInvitationService>.Instance, unconfigured, new ActivityLog(db, _clock), Onboarding(db), _auth0.Object, _clock);
        await Assert.ThrowsAsync<EmailConfigurationException>(() => service.CreateInvitationAsync(_world.OrgId, other, Admin, Ct));
        Assert.Equal(1, await _db.OwnerInvitations.CountAsync());
    }

    [Theory]
    [InlineData("http://app.example.test", false)]
    [InlineData("not an address", false)]
    [InlineData("", false)]
    [InlineData("https://app.example.test?next=/x", false)]
    [InlineData("https://app.example.test", true)]
    public void ThePublicAddressOfTheLink_IsAnAbsoluteHttpsUrl_ElseTheStartupRefusesIt(string address, bool valid)
    {
        var result = new PublicSiteOptionsValidator().Validate(null, new PublicSiteOptions { PublicSiteBaseUrl = address });

        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public async Task AnEmailThatCannotBeQueued_WithdrawsTheInvitation_ItIsNeverInvitedForNothing()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var second = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden);
        _queue.Accept = to => to != "bruno@example.com";

        var preview = await Service(NewDb()).PreviewBatchAsync(_world.OrgWide, null, Ct);
        var result = await Service(NewDb()).ConfirmBatchAsync(_world.OrgWide, Admin, null, preview.Fingerprint, Ct);

        Assert.Equal(1, result.Sent);
        var failure = Assert.Single(result.Failed);
        Assert.Equal((second, OwnerInvitationErrors.EmailNotQueued), (failure.OwnerId, failure.Code));
        var rows = await _db.OwnerInvitations.AsNoTracking().ToListAsync();
        Assert.Equal(OwnerInvitationStatus.Revoked, rows.Single(i => i.PropertyOwnerId == second).Status);
        Assert.Empty(rows.Single(i => i.PropertyOwnerId == second).SendLog);
        Assert.Equal(OwnerInvitationStatus.Pending, rows.Single(i => i.PropertyOwnerId == owner).Status);

        _queue.Accept = _ => false;
        var single = await Assert.ThrowsAsync<DomainRuleException>(() => Service(NewDb()).CreateInvitationAsync(_world.OrgId, owner, Admin, Ct));
        Assert.Equal(OwnerInvitationErrors.EmailNotQueued, single.Code);
        Assert.Equal(0, await _db.OwnerInvitations.CountAsync(i => i.Status == OwnerInvitationStatus.Pending && i.PropertyOwnerId == owner && i.ExpiresAt > _clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task TheClaimsGoToTheIdentityProvider_AndTheAnswerSaysWhetherTheyDid()
    {
        var owner = await AddReadyOwnerAsync("Anna Verdi", "anna@example.com", Granted);
        var second = await AddReadyOwnerAsync("Bruno Neri", "bruno@example.com", Hidden, share: 50m);
        var third = await AddReadyOwnerAsync("Carla Gialli", "carla@example.com", Hidden, share: 50m);

        _auth0
            .Setup(a => a.SetOwnerPortalClaimsAsync("auth0|bruno", It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.NotConfigured);
        _auth0
            .Setup(a => a.SetOwnerPortalClaimsAsync("auth0|carla", It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode));

        Assert.Equal(OwnerClaimsRefresh.Published, (await AcceptAsync(owner, "auth0|anna", "anna@example.com")).ClaimsRefresh);
        Assert.Equal(OwnerClaimsRefresh.NotConfigured, (await AcceptAsync(second, "auth0|bruno", "bruno@example.com")).ClaimsRefresh);

        // A failure of the identity provider does not undo the acceptance: it is done, and accepting again retries the hand-over.
        var failed = await AcceptAsync(third, "auth0|carla", "carla@example.com");
        Assert.Equal(OwnerClaimsRefresh.Failed, failed.ClaimsRefresh);
        Assert.Equal(OwnerInvitationStatus.Accepted, (await _db.OwnerInvitations.AsNoTracking().SingleAsync(i => i.PropertyOwnerId == third)).Status);
    }

    [Fact]
    public void TheEndpoints_HaveTheirPolicyAndTheirFlag()
    {
        var batch = typeof(OwnerInvitationBatchController);
        Assert.Equal(CasazenPolicies.AgencyOwnerManage, batch.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal("OctorateOnboarding", batch.GetCustomAttribute<Casazen.Web.Infrastructure.FeatureGateAttribute>()?.Flag);
        Assert.Equal(CasazenPolicies.AgencyOwnerManage, typeof(OwnerInvitationsController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);

        var onboarding = typeof(OwnerOnboardingController);
        Assert.Equal(
            CasazenPolicies.AgencyOwnerManage,
            onboarding.GetMethod(nameof(OwnerOnboardingController.Import))!.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal(
            CasazenPolicies.AgencyOwnerView,
            onboarding.GetMethod(nameof(OwnerOnboardingController.GetReadiness))!.GetCustomAttribute<AuthorizeAttribute>()?.Policy);

        var accept = typeof(OwnerInvitationAcceptanceController).GetMethod(nameof(OwnerInvitationAcceptanceController.Accept))!;
        Assert.Equal(CasazenPolicies.Authenticated, accept.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.DoesNotContain(accept.GetParameters(), p => p.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromQueryAttribute>() is not null);
    }

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private OwnerOnboardingService Onboarding(AppDbContext db) =>
        new(db, new ActivityLog(db, _clock), _clock, NullLogger<OwnerOnboardingService>.Instance);

    private OwnerInvitationService Service(AppDbContext db, bool withAgencyLinks = false)
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = PublicAddress }));
        AgencyDomainLinks? agencyLinks = null;
        if (withAgencyLinks)
        {
            var entitlements = new Mock<IEntitlementService>();
            entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns((OrgEntity o) => o.PlanTier);
            var siteUrls = new PublicOrgSiteUrls(links, Options.Create(new Casazen.Core.Options.PublicHostOptions()), entitlements.Object);
            agencyLinks = new AgencyDomainLinks(
                db, links, siteUrls, new TestFeatureFlags(Casazen.Core.Features.FeatureFlags.WhiteLabelHost), NullLogger<AgencyDomainLinks>.Instance);
        }

        return new OwnerInvitationService(
            db, _queue, NullLogger<OwnerInvitationService>.Instance, links, new ActivityLog(db, _clock), Onboarding(db), _auth0.Object, _clock, agencyLinks);
    }

    private static AcceptOwnerInvitation Accept(string token, string sub, string email, bool verified = true) =>
        new(token, sub, email, verified, "Nome", "Cognome");

    private async Task<OwnerInvitationAccepted> AcceptAsync(Guid ownerId, string sub, string email)
    {
        var token = await InviteAsync(ownerId);
        return await Service(NewDb()).AcceptAsync(Accept(token, sub, email), Ct);
    }

    private async Task<string> InviteAsync(Guid ownerId)
    {
        await Service(NewDb()).CreateInvitationAsync(_world.OrgId, ownerId, Admin, Ct);
        return TokenOf(_queue.Sent[^1]);
    }

    private static string TokenOf((string? To, EmailContent Content) email)
    {
        var match = Regex.Match(email.Content.HtmlBody, "token=([0-9a-f]{64})");
        Assert.True(match.Success, "the e-mail has the link with the token");
        return match.Groups[1].Value;
    }

    private async Task<Guid> AddOwnerAsync(string name, string email)
    {
        var parts = name.Split(' ', 2);
        var owner = new PropertyOwner
        {
            OrgId = _world.OrgId,
            FirstName = parts[0],
            LastName = parts.Length > 1 ? parts[1] : string.Empty,
            LegalName = name,
            Email = email,
            CreatedAt = DateTime.UtcNow,
        };
        _db.PropertyOwners.Add(owner);
        await _db.SaveChangesAsync();
        return owner.Id;
    }

    private async Task<Guid> AddReadyOwnerAsync(string name, string email, Guid propertyId, decimal share = 100m)
    {
        var id = await AddOwnerAsync(name, email);
        if (share > 0m)
            await AddPeriodAsync(_world.OrgId, propertyId, id, new DateOnly(2025, 1, 1), null, share);

        return id;
    }

    private async Task<Guid> AddPeriodAsync(Guid orgId, Guid propertyId, Guid ownerId, DateOnly start, DateOnly? end = null, decimal share = 100m)
    {
        var period = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = ownerId,
            SharePercent = share,
            OwnershipStart = OwnerOnboardingService.StartInstant(start),
            OwnershipEnd = end is { } last ? OwnerOnboardingService.EndInstant(last) : null,
        };
        _db.PropertyOwnerships.Add(period);
        await _db.SaveChangesAsync();
        return period.Id;
    }

    private sealed class RecordingEmailQueue : IEmailQueue
    {
        public List<(string? To, EmailContent Content)> Sent { get; } = [];

        public Func<string?, bool> Accept { get; set; } = _ => true;

        public bool Enqueue(string? to, EmailContent content, string template)
        {
            if (!Accept(to))
                return false;

            Sent.Add((to, content));
            return true;
        }
    }
}
