using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertySenderService" />
/// <remarks>
/// <para><b>Reads.</b> Everything goes through the <see cref="AppDbContext"/> of the request, so the tenant filter hides the other orgs'
/// properties, mappings and connections. The channel manager is available for a property when the connector is on and the property has
/// a confirmed listing (<see cref="OctorateMappingStatus.Confirmed"/>) on a connection of this environment that is connected, the rule
/// of the default of PO-02 in <see cref="PropertySenderStore"/>. The police fields come from one read of the police account of that
/// accommodation through <see cref="IOctorateApiClient.GetPoliceAccountAsync"/> for the org of the property; a failure of Octorate is an
/// unknown answer, never an error of the host's page and never a verified one.</para>
/// <para><b>Writes.</b> The sender goes through <see cref="IPropertySenderStore.SetAsync"/>, the one write path, which also records the
/// audit line of the switch (ADR-029-R4) in the same transaction: no second line is written here. The delegation consent is recorded
/// just before it, only when every check has passed. The declaration of the activation is the only column this service writes itself.</para>
/// <para><b>Logs.</b> Ids, codes and Octorate's status; never a body, a credential or a name.</para>
/// </remarks>
public sealed class PropertySenderService(
    AppDbContext db,
    IPropertySenderStore store,
    IAlloggiatiDelegationService delegation,
    IQuesturaCredentialsService credentials,
    IOctorateApiClient octorate,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    ILogger<PropertySenderService> logger,
    TimeProvider? timeProvider = null) : IPropertySenderService
{
    /// <summary>How long the read of a sender waits for Octorate before it answers without the police fields.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<PropertySenderView> GetAsync(Guid propertyId, CancellationToken ct)
    {
        var info = await store.GetAsync(propertyId, ct);
        return await BuildAsync(info, ct);
    }

    public async Task<PropertySenderView> SwitchAsync(
        Guid propertyId,
        ComplianceSenderType newSender,
        bool confirmed,
        bool delegationConsent,
        string userId,
        CancellationToken ct,
        ComplianceSenderType? istatSender = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!Enum.IsDefined(newSender))
            throw new ArgumentOutOfRangeException(nameof(newSender), newSender, "Not a compliance sender.");

        // ISTAT follows Alloggiati (ADR-029-R5): a request that names another sender for it is refused before anything is read.
        if (istatSender is { } istat && istat != newSender)
            throw new DomainRuleException(SenderErrorCodes.IstatMismatch, "SenderIstatMismatch");

        var current = await store.GetAsync(propertyId, ct);
        if (!current.Applies)
            throw new DomainRuleException(PropertySenderStore.NotApplicableCode, "SenderNotApplicable");

        // «CasaZen sends» needs the Questura accounts, which are postponed: it is not offered, so two automatic senders cannot meet.
        if (newSender == ComplianceSenderType.CasaZen)
            throw new DomainRuleException(SenderErrorCodes.CasaZenNotAvailable, "SenderCasaZenNotAvailable");

        if (newSender == ComplianceSenderType.ChannelManager && !await ChannelManagerAvailableAsync(current, ct))
            throw new DomainRuleException(SenderErrorCodes.ChannelManagerNotAvailable, "SenderChannelManagerNotAvailable");

        // The sender in effect goes off when another one is chosen (ADR-029-R3): the host confirms it knows.
        if (newSender != current.AlloggiatiSender && !confirmed)
        {
            throw new DomainConflictException(SenderErrorCodes.SwitchConfirmationRequired, "SenderSwitchConfirmationRequired")
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["currentSender"] = current.AlloggiatiSender.ToString(),
                    ["newSender"] = newSender.ToString(),
                },
            };
        }

        // An automatic sender transmits for the org: the delegation is accepted before it is set (ADR-015-R10), never assumed.
        var grantDelegation = false;
        if (IsAutomatic(newSender))
        {
            var accepted = await DelegationAcceptedAsync(current.OrgId, ct);
            if (!accepted && !delegationConsent)
                throw new DomainForbiddenException(SenderErrorCodes.DelegationRequired, "AlloggiatiDelegationRequired");

            grantDelegation = !accepted;
        }

        if (grantDelegation)
            await delegation.GrantAsync(current.OrgId, userId, AlloggiatiDelegationTexts.CurrentVersion, ct);

        var stored = await store.SetAsync(propertyId, newSender, userId, ct);
        return await BuildAsync(stored, ct);
    }

    public async Task<SenderConnectionTestResult> TestConnectionAsync(Guid propertyId, CancellationToken ct)
    {
        var info = await store.GetAsync(propertyId, ct);
        var checkedAt = Now();

        if (!info.Applies || info.AlloggiatiSender == ComplianceSenderType.Manual)
            return Result(info, SenderConnectionTestOutcome.NotApplicable, null, null, checkedAt);

        if (info.AlloggiatiSender == ComplianceSenderType.CasaZen)
            return Result(info, SenderConnectionTestOutcome.NotAvailable, SenderReasonCodes.CasaZenNotAvailable, null, checkedAt);

        // The channel manager: only Octorate can prove it, so without a connection to read it from nothing is verified.
        var accommodationId = await ConfirmedAccommodationIdAsync(info.PropertyId, info.OrgId, ct);
        var available = accommodationId is not null && featureFlags.IsEnabled(FeatureFlags.OctorateConnector);
        var police = available ? await ReadPoliceAsync(info, accommodationId!, timeout: null, ct) : null;
        var (verdict, reason) = Judge(police, available);
        var outcome = verdict switch
        {
            PoliceVerdict.Active => SenderConnectionTestOutcome.Verified,
            PoliceVerdict.Inactive => SenderConnectionTestOutcome.Inactive,
            _ => SenderConnectionTestOutcome.Unverifiable,
        };
        return Result(info, outcome, reason, police, checkedAt);
    }

    public async Task<PropertySenderView> DeclareActivatedAsync(Guid propertyId, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > PropertyComplianceSender.UserIdMaxLength)
            throw new ArgumentException("The user id of a declaration is an id of at most 255 characters.", nameof(userId));

        var current = await store.GetAsync(propertyId, ct);
        if (!current.Applies)
            throw new DomainRuleException(PropertySenderStore.NotApplicableCode, "SenderNotApplicable");

        if (current.AlloggiatiSender != ComplianceSenderType.ChannelManager)
            throw new DomainRuleException(SenderErrorCodes.DeclarationNotApplicable, "SenderDeclarationNotApplicable");

        if (!await DelegationAcceptedAsync(current.OrgId, ct))
            throw new DomainForbiddenException(SenderErrorCodes.DelegationRequired, "AlloggiatiDelegationRequired");

        // A default sender has no row yet: the store creates it (the sender does not change, so no line in the activity log) and the
        // declaration goes on it. The row is read after the store wrote it, so the declaration is never older than the choice.
        await store.SetAsync(propertyId, ComplianceSenderType.ChannelManager, userId, ct);
        var row = await db.PropertyComplianceSenders.FirstOrDefaultAsync(s => s.PropertyId == propertyId, ct)
                  ?? throw new DomainConflictException(PropertySenderStore.ChangedConcurrentlyCode, "SenderChangedConcurrently");
        row.ActivationDeclaredAt = Now();
        row.ActivationDeclaredByUserId = userId;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The sender was changed by someone else between the read and the save: the declaration was about the one that is gone.
            throw new DomainConflictException(PropertySenderStore.ChangedConcurrentlyCode, "SenderChangedConcurrently");
        }

        return await BuildAsync(await store.GetAsync(propertyId, ct), ct);
    }

    /// <summary>The view of a sender: its state from what Octorate says now, the declaration, the credentials and the delegation.</summary>
    private async Task<PropertySenderView> BuildAsync(PropertySenderInfo info, CancellationToken ct)
    {
        var accommodationId = info.Applies ? await ConfirmedAccommodationIdAsync(info.PropertyId, info.OrgId, ct) : null;
        var channelManagerAvailable = info.Applies && accommodationId is not null && featureFlags.IsEnabled(FeatureFlags.OctorateConnector);

        PoliceReading? police = null;
        if (info.AlloggiatiSender == ComplianceSenderType.ChannelManager && channelManagerAvailable)
            police = await ReadPoliceAsync(info, accommodationId!, ReadTimeout, ct);

        var declaredAt = await StandingDeclarationAsync(info.PropertyId, ct);
        var questura = await credentials.GetStatusAsync(info.PropertyId, ct);
        var accepted = await DelegationAcceptedAsync(info.OrgId, ct);

        var (state, verification, reason) = Evaluate(info, police, channelManagerAvailable, declaredAt is not null);
        // What the host can choose now: nothing outside Italy; manual always; the channel manager when the property can use it.
        var available = new List<ComplianceSenderType>(2);
        if (info.Applies)
        {
            if (channelManagerAvailable)
                available.Add(ComplianceSenderType.ChannelManager);

            available.Add(ComplianceSenderType.Manual);
        }

        return new PropertySenderView(
            info.PropertyId,
            info.AlloggiatiSender,
            info.IstatSender,
            info.Source,
            info.Applies,
            state,
            verification,
            reason,
            police?.Valid,
            police?.Autorun,
            police?.LastCron,
            questura.Configured,
            channelManagerAvailable,
            available,
            accepted,
            AlloggiatiDelegationTexts.CurrentVersion,
            declaredAt,
            info.LastChangedAt);
    }

    /// <summary>
    /// The state of a sender and how it is known. <see cref="SenderState.Active"/> only for the channel manager with Octorate's own word that
    /// the police account is valid and its automatic send is on; what Octorate does not say is unknown, and the host's declaration
    /// fills that gap as a declaration (<see cref="SenderState.Declared"/>), never as a verification.
    /// </summary>
    internal static (SenderState State, SenderVerification Verification, string? Reason) Evaluate(
        PropertySenderInfo info,
        PoliceReading? police,
        bool channelManagerAvailable,
        bool declared)
    {
        if (!info.Applies)
            return (SenderState.NotConfigured, SenderVerification.NotApplicable, null);

        switch (info.AlloggiatiSender)
        {
            case ComplianceSenderType.Manual:
                return (SenderState.NotConfigured, SenderVerification.NotApplicable, null);
            case ComplianceSenderType.CasaZen:
                // Not available in this release: nothing sends for this sender, whatever the row says.
                return (SenderState.Inactive, SenderVerification.NotApplicable, SenderReasonCodes.CasaZenNotAvailable);
        }

        var (verdict, reason) = Judge(police, channelManagerAvailable);
        return verdict switch
        {
            PoliceVerdict.Active => (SenderState.Active, SenderVerification.Verified, null),
            PoliceVerdict.Inactive => (SenderState.Inactive, SenderVerification.Verified, reason),
            _ when declared => (SenderState.Declared, SenderVerification.UserDeclared, reason),
            _ => (SenderState.Unverified, SenderVerification.Unverifiable, reason),
        };
    }

    /// <summary>
    /// What a reading of the police account proves. Active needs Octorate's word on both members, <c>valid</c> true and <c>autorun</c> true:
    /// a member it leaves out is no proof (and Octorate never defined <c>valid</c> alone as «Activated», Q-002), so <c>valid</c> true with
    /// <c>autorun</c> missing is unknown. <c>valid</c> false, or the automatic send off, is inactive; anything else (not exposed, not read,
    /// Octorate unreachable) is unknown.
    /// </summary>
    internal static (PoliceVerdict Verdict, string? Reason) Judge(PoliceReading? police, bool channelManagerAvailable)
    {
        if (!channelManagerAvailable || police is null)
            return (PoliceVerdict.Unknown, SenderReasonCodes.NotConnected);

        if (police.FailureReason is { } failure)
            return (PoliceVerdict.Unknown, failure);

        return police switch
        {
            { Valid: false } => (PoliceVerdict.Inactive, SenderReasonCodes.PoliceAccountInvalid),
            { Valid: true, Autorun: false } => (PoliceVerdict.Inactive, SenderReasonCodes.PoliceAutorunOff),
            { Valid: true, Autorun: true } => (PoliceVerdict.Active, null),
            _ => (PoliceVerdict.Unknown, SenderReasonCodes.PoliceNotExposed),
        };
    }

    /// <summary>
    /// Reads the police fields of the accommodation on Octorate for the org of the property. Every failure of Octorate (not connected,
    /// refused, unreachable, slow) is an unknown reading with its reason: it never fails the call and never proves anything.
    /// </summary>
    private async Task<PoliceReading> ReadPoliceAsync(PropertySenderInfo info, string accommodationId, TimeSpan? timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit)
            linked.CancelAfter(limit);

        try
        {
            using (orgScope.Use(info.OrgId))
            {
                var account = await octorate.GetPoliceAccountAsync(accommodationId, linked.Token);
                return new PoliceReading(account.Valid, account.Autorun, account.LastCron, null);
            }
        }
        catch (OctorateAuthException ex) when (ex.IsNotConnected)
        {
            return PoliceReading.Failed(SenderReasonCodes.NotConnected);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "Octorate police account of property {PropertyId} not read: {Code} (Octorate HTTP {Status})",
                info.PropertyId, ex.Code, ex.OctorateStatusCode);
            return PoliceReading.Failed(SenderReasonCodes.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Octorate police account of property {PropertyId} not read: no answer in time", info.PropertyId);
            return PoliceReading.Failed(SenderReasonCodes.Unavailable);
        }
    }

    /// <summary>
    /// The accommodation id of the confirmed listing of the property, on a connection of this environment that is connected (the rule of
    /// the default of PO-02); <c>null</c> when there is none.
    /// </summary>
    private async Task<string?> ConfirmedAccommodationIdAsync(Guid propertyId, Guid orgId, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        return await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.Status == OctorateMappingStatus.Confirmed && m.PropertyId == propertyId && m.OrgId == orgId)
            .Where(m => db.OctorateConnections.Any(c => c.Id == m.ConnectionId
                                                        && c.OrgId == m.OrgId
                                                        && c.Environment == environment
                                                        && c.Status == OctorateConnectionStatus.Connected))
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> ChannelManagerAvailableAsync(PropertySenderInfo info, CancellationToken ct) =>
        info.Applies
        && featureFlags.IsEnabled(FeatureFlags.OctorateConnector)
        && await ConfirmedAccommodationIdAsync(info.PropertyId, info.OrgId, ct) is not null;

    /// <summary>
    /// The declaration of the host if it still stands: not older than the last choice of the sender, because a later change of the
    /// sender voids it. <c>null</c> when there is no row, no declaration or a voided one.
    /// </summary>
    private async Task<DateTime?> StandingDeclarationAsync(Guid propertyId, CancellationToken ct)
    {
        var row = await db.PropertyComplianceSenders
            .AsNoTracking()
            .Where(s => s.PropertyId == propertyId)
            .Select(s => new { s.LastChangedAt, s.ActivationDeclaredAt })
            .FirstOrDefaultAsync(ct);
        return row?.ActivationDeclaredAt is { } declaredAt && declaredAt >= row.LastChangedAt ? declaredAt : null;
    }

    /// <summary>True when the org accepted the delegation text in force: granted, not revoked, and of the current version.</summary>
    private async Task<bool> DelegationAcceptedAsync(Guid orgId, CancellationToken ct)
    {
        var info = await delegation.GetAsync(orgId, ct);
        return info.Valid && string.Equals(info.TextVersion, AlloggiatiDelegationTexts.CurrentVersion, StringComparison.Ordinal);
    }

    /// <summary>The channel manager and CasaZen send on their own; manual is the host's hand.</summary>
    private static bool IsAutomatic(ComplianceSenderType sender) =>
        sender is ComplianceSenderType.ChannelManager or ComplianceSenderType.CasaZen;

    private static SenderConnectionTestResult Result(
        PropertySenderInfo info,
        SenderConnectionTestOutcome outcome,
        string? reason,
        PoliceReading? police,
        DateTime checkedAt)
    {
        return new SenderConnectionTestResult(
            info.AlloggiatiSender, outcome, reason, police?.Valid, police?.Autorun, police?.LastCron, checkedAt);
    }

    /// <summary>Now, cut to the microsecond PostgreSQL keeps, so what is written and what is read back compare equal.</summary>
    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

    /// <summary>What a reading of the police account proves.</summary>
    internal enum PoliceVerdict
    {
        /// <summary>Octorate says the account is valid and its automatic send is on.</summary>
        Active,

        /// <summary>Octorate says the account is not valid, or its automatic send is off.</summary>
        Inactive,

        /// <summary>Octorate did not say (not exposed, not read, unreachable).</summary>
        Unknown,
    }

    /// <summary>The police fields of an accommodation as Octorate gave them, or why they could not be read.</summary>
    /// <param name="Valid"><c>policeAccount.valid</c>; <c>null</c> = not exposed.</param>
    /// <param name="Autorun"><c>policeAccount.autorun</c>; <c>null</c> = not exposed.</param>
    /// <param name="LastCron"><c>policeAccount.lastCron</c> (UTC); <c>null</c> = not exposed.</param>
    /// <param name="FailureReason">A <see cref="SenderReasonCodes"/> value when the read failed; <c>null</c> when Octorate answered.</param>
    internal sealed record PoliceReading(bool? Valid, bool? Autorun, DateTime? LastCron, string? FailureReason)
    {
        public static PoliceReading Failed(string reason) => new(null, null, null, reason);
    }
}
