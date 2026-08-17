using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
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

        var hasApe = property.PropertyDocuments?.Any(d => d.DocumentType == DocumentType.Ape) ?? false;
        if (!hasApe)
            throw new InvalidOperationException("APE document is required before creating a lease contract.");

        if (request.EndDate <= request.StartDate)
            throw new InvalidOperationException("Lease end date must be after start date.");

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
            await eventRepository.AddAsync(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.PartySignedDocument,
                Payload = esignEvent.SignerEmail
            });
        }
    }

    public async Task<LeaseRegistration> TriggerRegistrationAsync(
        Guid leaseId, string ownerId, RegistrationAuthorizationRequest authorization)
    {
        var lease = await GetVerifiedLeaseAsync(leaseId, ownerId);

        if (lease.Status != LeaseStatus.Signed)
            throw new InvalidOperationException($"Lease must be Signed before registration. Current: {lease.Status}");

        var existing = await registrationRepository.GetByLeaseIdAsync(lease.Id);
        if (existing is not null)
            throw new InvalidOperationException("Registration has already been submitted for this lease.");

        var expectedTos = rliOptions.Value.TosVersion;
        if (!authorization.AttestationAccepted
            || string.IsNullOrWhiteSpace(authorization.TosVersion)
            || !string.Equals(authorization.TosVersion, expectedTos, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Landlord authorization (delega) is required before RLI submission.");
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

        var registration = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = externalId,
            SubmittedAt = DateTime.UtcNow
        };

        await registrationRepository.AddAsync(registration);

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

    public async Task<LeaseRegistration?> GetRegistrationAsync(Guid leaseId, string ownerId)
    {
        await GetVerifiedLeaseAsync(leaseId, ownerId);
        return await registrationRepository.GetByLeaseIdAsync(leaseId);
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

    public async Task<IEnumerable<LeaseContract>> GetOwnerLeasesAsync(string ownerId, Guid? propertyId = null)
        => await leaseRepository.GetByOwnerAsync(ownerId, propertyId);

    public async Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId, string ownerId)
    {
        var lease = await leaseRepository.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null || lease.Property.OwnerId != ownerId) return null;
        return lease;
    }

    private async Task<LeaseContract> GetVerifiedLeaseAsync(Guid leaseId, string ownerId)
    {
        var lease = await leaseRepository.GetByIdWithDetailsAsync(leaseId)
            ?? throw new InvalidOperationException($"Lease {leaseId} not found.");

        if (lease.Property is null || lease.Property.OwnerId != ownerId)
            throw new UnauthorizedAccessException("Lease does not belong to this owner.");

        return lease;
    }
}
