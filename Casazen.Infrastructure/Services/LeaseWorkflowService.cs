using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class LeaseWorkflowService(
    ILeaseContractRepository leaseRepository,
    ILeaseRegistrationRepository registrationRepository,
    ILeaseEventRepository eventRepository,
    ILeaseTemplateService templateService,
    ILeaseESignService eSignService,
    ILeaseRegistrationService registrationService,
    IPropertyRepository propertyRepository,
    ILeaseRegistrationAuthorizationRepository authorizationRepository,
    IApeComplianceService apeCompliance,
    ICanoneConcordatoEligibilityService canoneConcordatoEligibility,
    IOptions<RliOptions> rliOptions,
    ILogger<LeaseWorkflowService> logger) : ILeaseWorkflowService
{
    private static readonly HashSet<string> EuCitizenships =
    [
        "AT","BE","BG","CY","CZ","DE","DK","EE","ES","FI","FR","GR","HR",
        "HU","IE","IT","LT","LU","LV","MT","NL","PL","PT","RO","SE","SI","SK"
    ];

    public async Task<LeaseContract> CreateDraftAsync(Guid propertyId, string ownerId, CreateLeaseRequest request)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId)
            ?? throw new InvalidOperationException($"Property {propertyId} not found.");

        if (property.OwnerId != ownerId)
            throw new UnauthorizedAccessException("Property does not belong to this owner.");

        await apeCompliance.EnsurePropertyHasValidApeAsync(propertyId);

        if (request.EndDate <= request.StartDate)
            throw new InvalidOperationException("Lease end date must be after start date.");

        EnsureCanoneConcordatoMinimumTerm(request.FiscalRegime, request.StartDate, request.EndDate);

        if (request.FiscalRegime == FiscalRegime.CanoneConcordato)
            await EnsureCanoneConcordatoRentIsValidAsync(propertyId, ownerId, request);

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
            RegistrationDeadline = request.StartDate.AddDays(30),
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

            lease.Status = LeaseStatus.Signed;
            lease.SignedPdfStoragePath = esignEvent.SignedDocumentPath;
            await leaseRepository.UpdateAsync(lease);
            await eventRepository.AddAsync(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.AllPartiesSigned
            });
            logger.LogInformation("All parties signed. LeaseId={LeaseId}", lease.Id);
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

    public async Task<LeaseRegistration> TriggerRegistrationAsync(
        Guid leaseId, string ownerId, RegistrationAuthorizationRequest authorization)
    {
        var lease = await GetVerifiedLeaseAsync(leaseId, ownerId);

        var existing = await registrationRepository.GetByLeaseIdAsync(lease.Id);
        if (existing is not null && existing.Status != RegistrationStatus.Failed)
            throw new InvalidOperationException("Registration has already been submitted for this lease.");

        if (lease.Status != LeaseStatus.Signed)
            throw new InvalidOperationException($"Lease must be Signed before registration. Current: {lease.Status}");

        if (string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath))
            throw new InvalidOperationException("Signed lease PDF must be stored before registration.");

        EnsureCanoneConcordatoMinimumTerm(lease.FiscalRegime, lease.StartDate, lease.EndDate);

        var expectedTos = rliOptions.Value.TosVersion;
        if (!authorization.AttestationAccepted
            || string.IsNullOrWhiteSpace(authorization.TosVersion)
            || !string.Equals(authorization.TosVersion, expectedTos, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Landlord authorization (delega) is required before RLI submission.");
        }

        if (!rliOptions.Value.FilingEnabled)
            throw new InvalidOperationException("RLI filing is currently disabled.");

        await apeCompliance.EnsurePropertyHasValidApeAsync(lease.PropertyId);

        // Reserve the single per-lease registration row before calling the external provider so that
        // concurrent requests cannot both submit. A row left Failed by the provider is claimed atomically
        // (Failed -> Pending) and reused, keeping the one-registration-per-lease invariant.
        LeaseRegistration registration;
        if (existing is null)
        {
            registration = new LeaseRegistration
            {
                LeaseContractId = lease.Id,
                Status = RegistrationStatus.Pending
            };
            if (!await registrationRepository.TryReserveSubmissionAsync(registration))
                throw new InvalidOperationException("Registration has already been submitted for this lease.");
        }
        else
        {
            registration = existing;
            if (!await registrationRepository.TryReserveRetryAsync(registration))
                throw new InvalidOperationException("Registration has already been submitted for this lease.");
        }

        await authorizationRepository.AddAsync(new LeaseRegistrationAuthorization
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            AuthorizerUserId = ownerId,
            TosVersion = authorization.TosVersion,
            AttestationAccepted = true,
            Scope = "rli-filing",
        });
        await eventRepository.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.RegistrationAuthorized,
            Payload = authorization.TosVersion,
        });

        var externalId = await registrationService.SubmitRegistrationAsync(lease);
        var submittedAt = DateTime.UtcNow;

        registration.Status = RegistrationStatus.SentToProvider;
        registration.ExternalRegistrationId = externalId;
        registration.RegistrationCode = null;
        registration.ReceiptStoragePath = null;
        registration.SubmittedAt = submittedAt;
        registration.ConfirmedAt = null;
        await registrationRepository.UpdateAsync(registration);

        lease.Status = LeaseStatus.SentToProvider;
        await leaseRepository.UpdateAsync(lease);
        await eventRepository.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.RegistrationSubmitted
        });

        logger.LogInformation("Registration submitted. LeaseId={LeaseId} ExternalId={ExternalId}", leaseId, externalId);
        return registration;
    }

    public async Task<Stream> GetRegistrationReceiptAsync(Guid leaseId, string ownerId)
    {
        await GetVerifiedLeaseAsync(leaseId, ownerId);
        var registration = await registrationRepository.GetByLeaseIdAsync(leaseId)
            ?? throw new InvalidOperationException("No registration found for this lease.");

        if (registration.Status != RegistrationStatus.Registered || registration.ExternalRegistrationId is null)
            throw new InvalidOperationException("Receipt is not available yet.");

        return await registrationService.DownloadReceiptAsync(registration.ExternalRegistrationId);
    }

    public Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, Guid? propertyId = null)
        => leaseRepository.GetSummariesAsync(scope, propertyId);

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
        string ownerId,
        CreateLeaseRequest request)
    {
        if (request.CanoneConcordatoCharacteristics is null)
            throw new InvalidOperationException(
                "Canone concordato characteristics are required for canone concordato leases.");

        var eligibility = await canoneConcordatoEligibility.CalculateAsync(
            propertyId,
            ownerId,
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

    private static void EnsureCanoneConcordatoMinimumTerm(
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
