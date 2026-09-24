using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Lease drafting and signing. The RLI registration (provider or manual, LT-01) is <see cref="RliRegistrationService"/>.
/// </summary>
public class LeaseWorkflowService(
    ILeaseContractRepository leaseRepository,
    ILeaseEventRepository eventRepository,
    ILeaseTemplateService templateService,
    ILeaseESignService eSignService,
    IPropertyRepository propertyRepository,
    IApeComplianceService apeCompliance,
    ICanoneConcordatoEligibilityService canoneConcordatoEligibility,
    ILogger<LeaseWorkflowService> logger,
    TimeProvider? timeProvider = null) : ILeaseWorkflowService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly HashSet<string> EuCitizenships =
    [
        "AT","BE","BG","CY","CZ","DE","DK","EE","ES","FI","FR","GR","HR",
        "HU","IE","IT","LT","LU","LV","MT","NL","PL","PT","RO","SE","SI","SK"
    ];

    public async Task<LeaseContract> CreateDraftAsync(Guid propertyId, CreateLeaseRequest request)
    {
        // Who may create a lease on the property (owner or org-wide member with lease.create) is decided by the
        // caller with the TN-3 resource check; the tenant filter keeps other orgs' properties invisible here.
        var property = await propertyRepository.GetByIdAsync(propertyId)
            ?? throw new InvalidOperationException($"Property {propertyId} not found.");

        await apeCompliance.EnsurePropertyHasValidApeAsync(propertyId);

        if (request.EndDate <= request.StartDate)
            throw new InvalidOperationException("Lease end date must be after start date.");

        EnsureCanoneConcordatoMinimumTerm(request.FiscalRegime, request.StartDate, request.EndDate);

        if (request.FiscalRegime == FiscalRegime.CanoneConcordato)
            await EnsureCanoneConcordatoRentIsValidAsync(propertyId, request);

        var parties = request.Parties.ToList();
        if (!parties.Any(p => p.Role == PartyRole.Landlord))
            throw new InvalidOperationException("At least one Landlord party is required.");
        if (!parties.Any(p => p.Role == PartyRole.Tenant))
            throw new InvalidOperationException("At least one Tenant party is required.");

        var lease = new LeaseContract
        {
            PropertyId = propertyId,
            OrgId = property.OrgId,
            FiscalRegime = request.FiscalRegime,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            MonthlyRent = request.MonthlyRent,
            // No stipula yet: the RLI deadline is fixed when every party has signed (LT-04, A7-04).
            DataRetentionUntil = request.StartDate.AddYears(10),
            Parties = parties.Select(p => new Party
            {
                Role = p.Role,
                FirstName = p.FirstName,
                LastName = p.LastName,
                FiscalCode = p.FiscalCode,
                Citizenship = p.Citizenship,
                ContactEmail = p.ContactEmail,
                IsExtraEU = !EuCitizenships.Contains(p.Citizenship.ToUpperInvariant())
            }).ToList()
        };

        await leaseRepository.AddAsync(lease);
        await eventRepository.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.Created
        });

        logger.LogInformation("Lease draft created. LeaseId={LeaseId} PropertyId={PropertyId}", lease.Id, propertyId);
        return lease;
    }

    public async Task<SigningInitiatedResult> InitiateSigningAsync(Guid leaseId, string ownerId)
    {
        var lease = await GetVerifiedLeaseAsync(leaseId, ownerId);

        if (lease.Status != LeaseStatus.Draft)
            throw new InvalidOperationException($"Lease must be in Draft status to initiate signing. Current: {lease.Status}");

        EnsureCanoneConcordatoMinimumTerm(lease.FiscalRegime, lease.StartDate, lease.EndDate);

        await apeCompliance.EnsurePropertyHasValidApeAsync(lease.PropertyId);

        var pdfBytes = await templateService.GeneratePdfAsync(lease);
        var sessionResult = await eSignService.InitiateSigningAsync(lease, pdfBytes);

        lease.Status = LeaseStatus.AwaitingSignature;
        lease.ExternalSigningSessionId = sessionResult.ExternalSessionId;
        await leaseRepository.UpdateAsync(lease);
        await eventRepository.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.SigningInitiated
        });

        logger.LogInformation("Signing initiated. LeaseId={LeaseId} SessionId={SessionId}", leaseId, sessionResult.ExternalSessionId);
        return new SigningInitiatedResult(lease.Id, lease.Status, sessionResult.Signers);
    }

    public async Task HandleESignEventAsync(string providerPayload)
    {
        var esignEvent = await eSignService.ParseWebhookEventAsync(providerPayload);

        var lease = await leaseRepository.GetByExternalSigningSessionIdAsync(esignEvent.ExternalSessionId);
        if (lease is null)
        {
            logger.LogWarning("ESign webhook received but no lease found for SessionId={SessionId}", esignEvent.ExternalSessionId);
            return;
        }

        if (esignEvent.AllSigned)
        {
            if (lease.Status != LeaseStatus.AwaitingSignature)
            {
                logger.LogInformation(
                    "Ignoring all-signed webhook for lease {LeaseId} in status {Status}",
                    lease.Id,
                    lease.Status);
                return;
            }

            if (string.IsNullOrWhiteSpace(esignEvent.SignedDocumentPath))
            {
                logger.LogWarning(
                    "ESign all-signed webhook missing signed document path. LeaseId={LeaseId} SessionId={SessionId}",
                    lease.Id,
                    esignEvent.ExternalSessionId);
                return;
            }

            // The provider reports no signing time: the stipula is the Rome day on which CasaZen records the last
            // signature, the same instant as the AllPartiesSigned event (LT-04).
            var signedAt = _clock.GetUtcNow().UtcDateTime;
            lease.Status = LeaseStatus.Signed;
            lease.SignedPdfStoragePath = esignEvent.SignedDocumentPath;
            lease.RecordStipula(signedAt);
            await leaseRepository.UpdateAsync(lease);
            await eventRepository.AddAsync(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.AllPartiesSigned,
                OccurredAt = signedAt,
            });
            logger.LogInformation(
                "All parties signed. LeaseId={LeaseId} StipulaDate={StipulaDate:yyyy-MM-dd} RegistrationDeadline={RegistrationDeadline:yyyy-MM-dd}",
                lease.Id,
                lease.StipulaDate,
                lease.RegistrationDeadline);
        }
        else
        {
            // The payload names the signer by party id, never by email: event payloads hold no personal data (A7-17).
            var signer = string.IsNullOrWhiteSpace(esignEvent.SignerEmail)
                ? null
                : lease.Parties.FirstOrDefault(p =>
                    string.Equals(p.ContactEmail.Trim(), esignEvent.SignerEmail.Trim(), StringComparison.OrdinalIgnoreCase));
            await eventRepository.AddAsync(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.PartySignedDocument,
                Payload = signer?.Id.ToString()
            });
        }
    }

    public async Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, Guid? propertyId = null)
    {
        // The deadline of a lease not signed yet depends on today (LT-04): resolved here, not stored.
        var today = _clock.TodayInRome();
        var summaries = await leaseRepository.GetSummariesAsync(scope, propertyId);
        return summaries
            .Select(s => s with
            {
                RegistrationDeadline = RliRegistrationDeadline.Resolve(s.Status, s.StipulaDate, s.StartDate, today),
            })
            .ToList();
    }

    public Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId)
        => leaseRepository.GetByIdWithDetailsAsync(leaseId);

    private async Task<LeaseContract> GetVerifiedLeaseAsync(Guid leaseId, string ownerId)
    {
        var lease = await leaseRepository.GetByIdWithDetailsAsync(leaseId)
            ?? throw new NotFoundException($"Lease {leaseId} not found.")
            {
                Code = "lease_not_found",
                MessageKey = "LeaseNotFound",
            };

        if (lease.Property is null || lease.Property.OwnerId != ownerId)
            throw new UnauthorizedAccessException("Lease does not belong to this owner.");

        return lease;
    }

    private async Task EnsureCanoneConcordatoRentIsValidAsync(
        Guid propertyId,
        CreateLeaseRequest request)
    {
        if (request.CanoneConcordatoCharacteristics is null)
            throw new InvalidOperationException(
                "Canone concordato characteristics are required for canone concordato leases.");

        var eligibility = await canoneConcordatoEligibility.CalculateAsync(
            propertyId,
            request.CanoneConcordatoCharacteristics);

        if (eligibility is not
            {
                Available: true,
                CanoneMinMensile: decimal minMonthly,
                CanoneMaxMensile: decimal maxMonthly
            })
        {
            throw new InvalidOperationException("Canone concordato rent band is unavailable for this property.");
        }

        if (request.MonthlyRent < minMonthly || request.MonthlyRent > maxMonthly)
        {
            throw new InvalidOperationException(
                "Monthly rent must be within the calculated canone concordato range.");
        }
    }

    /// <summary>Canone concordato leases cover at least the initial 3-year term (contratto tipo 3+2).</summary>
    internal static void EnsureCanoneConcordatoMinimumTerm(
        FiscalRegime fiscalRegime,
        DateTime startDate,
        DateTime endDate)
    {
        if (fiscalRegime != FiscalRegime.CanoneConcordato)
            return;

        var minimumEndDate = startDate.Date.AddYears(3).AddDays(-1);
        if (endDate.Date < minimumEndDate)
        {
            throw new InvalidOperationException(
                "Canone concordato leases must cover at least the initial 3-year term required for contratto tipo 3+2.");
        }
    }
}
