using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: the guard of the one destructive-looking move of the invitations, a person leaving its empty org for the one that
/// invited it. The list of tables is read from the model, and these tests are the tripwire that keeps it that way: a tenant
/// table added tomorrow is part of the "org is empty" rule from the first day, or this suite fails.
/// </summary>
public class OrgEmptinessCheckerTests
{
    private const string OwnerId = "auth0|anna";

    private static AppDbContext NewDb() => OrgTeamTestData.NewDb();

    private static async Task<Guid> SeedEmptyOrgAsync(AppDbContext db)
    {
        var org = OrgTeamTestData.AddOrg(db);
        OrgTeamTestData.AddUser(db, OwnerId, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddMember(db, OwnerId, org.Id, OrgRole.Owner);
        db.ConsentRecords.Add(new ConsentRecord { UserId = OwnerId, OrgId = org.Id, Type = ConsentType.Tos, Version = "v1" });
        db.SignupAttributions.Add(new SignupAttribution { OrgId = org.Id, UtmSource = "google" });
        await db.SaveChangesAsync();
        return org.Id;
    }

    // ─── The tables ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnboardingRows_AreExactlyWhatTheOnboardingLeavesAndAllTenantOwned()
    {
        // A new entry here is a decision, not a convenience: it makes an org with that table filled still "empty". The wizard drafts
        // are one (ADR-024): a half-filled form is not use of the org, and its rows are anonymized but never deleted. So are the notification
        // settings of a person (NF-65): the channels of each kind of event, its quiet hours and the slots of the email rule; the alerts
        // raised for the person (ADR-025) and a part of the activation plan put off with «Do it later» (E10/F01/US04/T02). Every other table
        // of the plan (the outbox, the routines, the Octorate tables, the CRM, the fiscal tables...) is NOT here: a row there is real use.
        Assert.Equal(
            [
                typeof(ConsentRecord),
                typeof(NotificationEmailSlot),
                typeof(OnboardingPartState),
                typeof(OrgSlugAlias),
                typeof(PopupAlert),
                typeof(SignupAttribution),
                typeof(UserNotificationChannelSetting),
                typeof(UserNotificationPreferences),
                typeof(WizardDraft),
            ],
            OrgEmptinessChecker.OnboardingRows.OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.All(OrgEmptinessChecker.OnboardingRows, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), t.Name));

        // The one row-level exception is the type of "To do" a wizard raises when a person leaves half way: nothing else. The table itself
        // stays one of the checked ones (a row of any other type of activity makes the org used).
        Assert.Equal("WizardResume", OrgEmptinessChecker.WizardResumeActivityType);
        using var db = NewDb();
        Assert.Contains(typeof(InternalActivity), OrgEmptinessChecker.CheckedTenantTables(db));
        Assert.DoesNotContain(typeof(InternalActivity), OrgEmptinessChecker.OnboardingRows);
    }

    /// <summary>
    /// Privacy tripwire for the two types this task adds to <see cref="OrgEmptinessChecker.OnboardingRows"/>: they hold a user id, codes, references
    /// and dates, never a free text or a person's data (a name, an address, a note). A new text column on either must make somebody decide again
    /// whether the table can stay a row that does not make the org "used".
    /// </summary>
    [Fact]
    public void OnboardingRows_TheTypesOfTheAlertsAndOfThePlan_HoldIdsAndCodesOnly()
    {
        static string[] TextColumns(Type type) =>
            [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && !p.IsDefined(typeof(NotMappedAttribute)))
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal)];

        // PopupAlert: who is alerted and which kind of entity the alert points to (the client reads the detail from the API of that entity).
        Assert.Equal(["EntityType", "UserId"], TextColumns(typeof(PopupAlert)));

        // OnboardingPartState: who changed the row last (the Auth0 subject). The part, the status and the dates are not text.
        Assert.Equal(["UpdatedByUserId"], TextColumns(typeof(OnboardingPartState)));
    }

    [Fact]
    public void CheckedTenantTables_EveryTenantOwnedEntityOfTheModel_IsCheckedOrAnExplicitException()
    {
        using var db = NewDb();

        var tenantOwned = db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && !e.IsOwned() && e.BaseType is null)
            .Select(e => e.ClrType)
            .ToHashSet();
        var checkedTables = OrgEmptinessChecker.CheckedTenantTables(db).ToHashSet();

        var neither = tenantOwned
            .Where(t => !checkedTables.Contains(t) && !OrgEmptinessChecker.OnboardingRows.Contains(t) && t != typeof(OrgMember))
            .Select(t => t.Name)
            .ToList();
        Assert.True(neither.Count == 0, "Tenant tables the empty-org check does not look at: " + string.Join(", ", neither));
        Assert.Empty(checkedTables.Except(tenantOwned));

        // The ones that matter most are in, the new tables of the team included.
        foreach (var table in new[]
                 {
                     typeof(Property), typeof(Booking), typeof(Guest), typeof(Payment), typeof(LeaseContract), typeof(PlatformInvoice),
                     typeof(CalendarBlock), typeof(OrgInvitation), typeof(OrgSiteDocument), typeof(PropertyDocument),
                 })
            Assert.Contains(table, checkedTables);

        // OrgMember is checked in its own way (the person's own row is expected), the onboarding rows are the exception.
        Assert.DoesNotContain(typeof(OrgMember), checkedTables);
        Assert.All(OrgEmptinessChecker.OnboardingRows, t => Assert.DoesNotContain(t, checkedTables));
        Assert.True(checkedTables.Count >= 30, $"Only {checkedTables.Count} tenant tables are checked.");
    }

    /// <summary>
    /// The tripwire in action: for every table the check looks at, a single row with the org's id makes the org "in use".
    /// A table whose entity cannot be built with its defaults is listed, and the test fails until somebody gives it a row
    /// here: a table can never silently fall out of the check.
    /// </summary>
    [Fact]
    public async Task CheckAsync_AOneRowInAnyTenantTable_MakesTheOrgInUse()
    {
        List<Type> tables;
        using (var model = NewDb())
            tables = OrgEmptinessChecker.CheckedTenantTables(model).ToList();

        var notExercised = new List<string>();
        var notBlocking = new List<string>();
        foreach (var table in tables)
        {
            await using var db = NewDb();
            var orgId = await SeedEmptyOrgAsync(db);
            if (!await TryAddRowAsync(db, table, orgId))
            {
                notExercised.Add(table.Name);
                continue;
            }

            var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);
            if (result.IsEmpty || !result.Blockers.Contains($"data:{table.Name}"))
                notBlocking.Add(table.Name);
        }

        Assert.True(
            notBlocking.Count == 0,
            "A row in these tenant tables does not stop an org from being left: " + string.Join(", ", notBlocking));
        Assert.True(
            notExercised.Count == 0,
            "These tenant tables cannot be built from their defaults by this test, so nothing proves they are checked: " +
            string.Join(", ", notExercised) + ". Give them a row in TryAddRowAsync.");
    }

    private static async Task<bool> TryAddRowAsync(AppDbContext db, Type table, Guid orgId)
    {
        object? row;
        try
        {
            row = Activator.CreateInstance(table);
        }
        catch (MissingMethodException)
        {
            return false;
        }

        if (row is null)
            return false;

        var orgIdProperty = table.GetProperty(nameof(ITenantOwned.OrgId));
        if (orgIdProperty is null || !orgIdProperty.CanWrite)
            return false;
        orgIdProperty.SetValue(row, orgId);

        // Specific rows for the entities whose defaults are not a valid row of the in-memory provider.
        PrepareRow(row, db);

        db.Add(row);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Gives an entity the few values the in-memory provider insists on (required members without a default).</summary>
    private static void PrepareRow(object row, AppDbContext db)
    {
        switch (row)
        {
            case OrgSlugAlias alias:
                alias.Slug = $"alias-{Guid.NewGuid():N}";
                break;
        }
    }

    // ─── The org itself ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_AnUntouchedOrg_IsEmpty()
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_WizardDrafts_LiveOrAnonymized_DoNotMakeTheOrgInUse()
    {
        // ADR-024: a wizard saves its progress from the first step and the rows are anonymized, never deleted. If they counted, the person
        // who opened a wizard before accepting an invitation would get 409 invitation_user_has_organization, for good (AM-02).
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        db.WizardDrafts.Add(new WizardDraft { OrgId = orgId, UserId = OwnerId, WizardType = WizardType.ListingCreate, Version = 1 });
        db.WizardDrafts.Add(new WizardDraft { OrgId = orgId, UserId = string.Empty, WizardType = WizardType.BookingCreate, Version = 3 });
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_SavedNotificationPreferences_DoNotMakeTheOrgInUse()
    {
        // NF-65: a person saves how it wants to be alerted (channels and quiet hours, PUT api/me/notification-preferences) in the org it has,
        // and may still accept an invitation: the rows are never deleted, so if they counted the acceptance would be refused for good
        // with 409 invitation_user_has_organization (AM-02). The slot of the email rule is the same kind of row.
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        var preferences = new NotificationPreferenceService(
            db,
            Mock.Of<IContextAuthorizationService>(),
            TimeProvider.System,
            NullLogger<NotificationPreferenceService>.Instance);

        await preferences.SaveAsync(
            OwnerId,
            new NotificationPreferencesUpdate(
                new QuietHoursUpdate(new TimeOnly(23, 0), new TimeOnly(7, 0)),
                [new NotificationEventUpdate(NotificationEventType.NewGuestMessage, NotificationChannels.Email | NotificationChannels.CounterOnly)]),
            CancellationToken.None);
        db.NotificationEmailSlots.Add(new NotificationEmailSlot
        {
            OrgId = orgId,
            UserId = OwnerId,
            ThreadKey = $"ota:{Guid.NewGuid():D}",
            LastEmailAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.UserNotificationChannelSettings.CountAsync());
        Assert.Equal(1, await db.UserNotificationPreferences.CountAsync());
        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Blockers);
    }

    /// <summary>
    /// Proof (data, E10/F01/US04/T02): what the onboarding itself writes for a person who has done nothing yet does not make the org "used": a saved
    /// wizard draft, an alert raised for the person, the two kinds of alert settings, a part of the activation plan put off with «Do it later» and the
    /// "To do" the plan raises for it (<c>WizardResume</c>), open or closed. If any of them counted, the person who only started a wizard or saved a
    /// preference would be refused with 409 <c>invitation_user_has_organization</c> for good (AM-02): none of these rows is ever deleted.
    /// </summary>
    [Fact]
    public async Task OrgEmptiness_OnboardingRowsOnly_StillEmpty_Test()
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        var now = DateTime.UtcNow;
        var openPart = Guid.NewGuid();
        var closedPart = Guid.NewGuid();
        db.WizardDrafts.Add(new WizardDraft { OrgId = orgId, UserId = OwnerId, WizardType = WizardType.OnboardingActivation, Version = 1 });
        db.PopupAlerts.Add(new PopupAlert
        {
            OrgId = orgId,
            UserId = OwnerId,
            AlertType = AlertType.BookingNeedsReview,
            EntityType = PopupAlert.BookingEntityType,
            EntityId = Guid.NewGuid(),
        });
        db.UserNotificationPreferences.Add(new UserNotificationPreferences { OrgId = orgId, UserId = OwnerId });
        db.UserNotificationChannelSettings.Add(new UserNotificationChannelSetting
        {
            OrgId = orgId,
            UserId = OwnerId,
            EventType = NotificationEventType.NewGuestMessage,
            Channels = NotificationChannels.Email,
        });
        db.OnboardingPartStates.Add(new OnboardingPartState
        {
            OrgId = orgId,
            PartKey = OnboardingPartKey.Listings,
            Status = OnboardingPartStatus.Deferred,
            DeferredAt = now,
            RemindAt = now.AddDays(1),
            ActivityId = openPart,
            UpdatedByUserId = OwnerId,
        });
        db.InternalActivities.Add(Todo(orgId, nameof(ActivityType.WizardResume), openPart, closed: false));
        db.InternalActivities.Add(Todo(orgId, nameof(ActivityType.WizardResume), closedPart, closed: true));
        await db.SaveChangesAsync();

        // Every kind of row is really there: the test is not empty of the very things it is about.
        Assert.Equal(
            (1, 1, 1, 1, 1, 2),
            (await db.WizardDrafts.CountAsync(),
                await db.PopupAlerts.CountAsync(),
                await db.UserNotificationPreferences.CountAsync(),
                await db.UserNotificationChannelSettings.CountAsync(),
                await db.OnboardingPartStates.CountAsync(),
                await db.InternalActivities.CountAsync()));

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Blockers);
    }

    /// <summary>
    /// Proof (permission, data, E10/F01/US04/T02): the check keeps failing closed. A "To do" of any type but <c>WizardResume</c> (the staff work, with
    /// its plain-text types, and every other kind of "To do", open or closed, even a different spelling of the type) makes the org used, and it does so
    /// next to the onboarding rows too, which add no blocker of their own: a person with real data is never merged into another organization.
    /// </summary>
    [Theory]
    [InlineData("Cleaning", false, false)]
    [InlineData("Maintenance", false, true)]
    [InlineData("CheckIn", false, false)]
    [InlineData(nameof(ActivityType.MessageToApprove), true, false)]
    [InlineData(nameof(ActivityType.FiscalDocument), true, true)]
    [InlineData(nameof(ActivityType.IntegrationError), true, false)]
    [InlineData(nameof(ActivityType.ReconnectChannelManager), true, false)]
    [InlineData("wizardresume", true, false)]
    public async Task OrgEmptiness_AnyOtherTenantRow_StillBlocks_Test(string activityType, bool isTodo, bool closed)
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        db.WizardDrafts.Add(new WizardDraft { OrgId = orgId, UserId = OwnerId, WizardType = WizardType.OnboardingActivation, Version = 1 });
        db.PopupAlerts.Add(new PopupAlert
        {
            OrgId = orgId,
            UserId = OwnerId,
            AlertType = AlertType.BookingNeedsReview,
            EntityType = PopupAlert.BookingEntityType,
            EntityId = Guid.NewGuid(),
        });
        db.InternalActivities.Add(Todo(orgId, nameof(ActivityType.WizardResume), Guid.NewGuid(), closed: false));
        db.InternalActivities.Add(Todo(orgId, activityType, Guid.NewGuid(), closed, isTodo));
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Equal([$"data:{nameof(InternalActivity)}"], result.Blockers);
    }

    /// <summary>
    /// Proof (data, E10/F01/US04/T02): the tables the plan adds besides the onboarding ones are all checked, and a single row in any of them makes the
    /// org used: the outbox, the routines, the Octorate tables, the fiscal tables, the marketplace, the copy of the messages and the assistant settings.
    /// </summary>
    [Fact]
    public async Task OrgEmptiness_TheOtherTablesOfThePlan_StillBlock_Test()
    {
        Type[] tablesOfThePlan =
        [
            typeof(OutboxEvent),
            typeof(Routine),
            typeof(RoutineExecution),
            typeof(OctorateConnection),
            typeof(OctorateListingMapping),
            typeof(FiscalDocument),
            typeof(OrgFiscalIssuerProfile),
            typeof(MarketplaceActivation),
            typeof(OtaMessageThread),
            typeof(OtaMessageSyncState),
            typeof(AssistantSituationSetting),
            typeof(PropertyFlowActivation),
            typeof(OnboardingAssistRequest),
        ];

        List<Type> checkedTables;
        using (var model = NewDb())
            checkedTables = [.. OrgEmptinessChecker.CheckedTenantTables(model)];
        Assert.All(tablesOfThePlan, table => Assert.Contains(table, checkedTables));
        Assert.All(tablesOfThePlan, table => Assert.DoesNotContain(table, OrgEmptinessChecker.OnboardingRows));

        foreach (var table in tablesOfThePlan)
        {
            await using var db = NewDb();
            var orgId = await SeedEmptyOrgAsync(db);
            Assert.True(await TryAddRowAsync(db, table, orgId), $"{table.Name} cannot be built from its defaults: give it a row in TryAddRowAsync.");

            var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

            Assert.False(result.IsEmpty, table.Name);
            Assert.Contains($"data:{table.Name}", result.Blockers);
        }
    }

    /// <summary>A "To do" (with its action) or a row of the staff work (without) of the org, open or closed, of the given type.</summary>
    private static InternalActivity Todo(Guid orgId, string activityType, Guid entityId, bool closed, bool isTodo = true) => new()
    {
        OrgId = orgId,
        ActivityType = activityType,
        Action = isTodo ? ActivityAction.ResumeWizard : null,
        EntityType = isTodo ? "OnboardingPart" : null,
        EntityId = isTodo ? entityId : null,
        Urgency = isTodo ? ActivityUrgency.Medium : null,
        Reason = isTodo ? "deferred" : null,
        Status = closed ? InternalActivity.StatusCompleted : InternalActivity.StatusPending,
        CompletedAt = closed ? DateTime.UtcNow : null,
        ClosedBy = closed ? InternalActivity.ClosedBySystem : null,
        ScheduledAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task CheckAsync_AnOrgThatDoesNotExist_IsNotEmpty()
    {
        await using var db = NewDb();

        var result = await new OrgEmptinessChecker(db).CheckAsync(Guid.NewGuid(), OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Equal(["not_found"], result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_ASupplierOrg_IsNotEmpty()
    {
        await using var db = NewDb();
        var org = OrgTeamTestData.AddOrg(db, OrgType.Supplier);
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(org.Id, OwnerId);

        Assert.Contains("not_a_host_org", result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_SeveralReasons_AreAllReported()
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.PlanTier = PlanTier.Scale;
        org.StripeCustomerId = "cus_1";
        org.SubscriptionStatus = SubscriptionStatus.Active;
        org.SubscriptionId = "sub_1";
        org.StripeConnectedAccountId = "acct_1";
        org.CustomDomain = "x.example.com";
        org.LogoUrl = "https://x/logo.png";
        OrgTeamTestData.AddUser(db, "auth0|other", orgId);
        db.Properties.Add(new Property { OwnerId = OwnerId, OrgId = orgId, Name = "P", Address = "A", City = "C" });
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Equal(
            ["branding", "connect_account", "data:Property", "domain", "other_users", "plan", "stripe_customer", "subscription"],
            result.Blockers.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(OrgMemberStatus.Active)]
    [InlineData(OrgMemberStatus.Deactivated)]
    public async Task CheckAsync_AnotherMemberOfTheOrg_BlocksIt(OrgMemberStatus status)
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        OrgTeamTestData.AddUser(db, "auth0|colleague", orgId: null);
        OrgTeamTestData.AddMember(db, "auth0|colleague", orgId, OrgRole.Collaborator, status);
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.Contains("other_members", result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_ReadsTheOrgWhateverTheTenantOfTheRequest_SoftDeletedPropertiesIncluded()
    {
        var database = Guid.NewGuid().ToString();
        Guid orgId;
        await using (var seed = OrgTeamTestData.NewDb(database))
        {
            orgId = await SeedEmptyOrgAsync(seed);
            seed.Properties.Add(new Property { OwnerId = OwnerId, OrgId = orgId, Name = "P", Address = "A", City = "C", IsDeleted = true });
            await seed.SaveChangesAsync();
        }

        // The request belongs to another org: the tenant filter would hide the rows of this one, and the soft-delete
        // filter the deleted property.
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options,
            new FixedTenantContext(Guid.NewGuid()));

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Contains("data:Property", result.Blockers);
    }

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
