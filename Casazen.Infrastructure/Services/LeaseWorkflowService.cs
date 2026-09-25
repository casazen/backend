using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Lease drafting and reads. The signature (offline or provider, LT-02) is <see cref="LeaseSigningService"/>, the RLI
/// registration (provider or manual, LT-01) is <see cref="RliRegistrationService"/>.
/// </summary>
public class LeaseWorkflowService(
    ILeaseContractRepository leaseRepository,
    ILeaseEventRepository eventRepository,
    IPropertyRepository propertyRepository,
    IApeComplianceService apeCompliance,
    ICanoneConcordatoEligibilityService canoneConcordatoEligibility,
    ILogger<LeaseWorkflowService> logger,
    TimeProvider? timeProvider = null) : ILeaseWorkflowService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<LeaseContract> CreateDraftAsync(Guid propertyId, CreateLeaseRequest request)
    {
        // Who may create a lease on the property (owner or org-wide member with lease.create) is decided by the
        // caller with the TN-3 resource check; the tenant filter keeps other orgs' properties invisible here.
        var property = await propertyRepository.GetByIdAsync(propertyId)
            ?? throw new InvalidOperationException($"Property {propertyId} not found.");

        await apeCompliance.EnsurePropertyHasValidApeAsync(propertyId);

        var (contractType, taxRegime) = ResolveContractTerms(request);

        // Term from the dates, checked for the contract type (LT-10, A7-13): 4+4, 3+2, transitorio 1-18 months.
        LeaseContractTerms.EnsureTerm(contractType, request.StartDate, request.EndDate);
        var term = LeaseTerm.Between(request.StartDate, request.EndDate)!.Value;

        var concordato = contractType == LeaseContractType.Concordato
            ? await AssessConcordatoRentAsync(propertyId, request, term)
            : null;

        var parties = request.Parties.ToList();
        if (!parties.Any(p => p.Role == PartyRole.Landlord))
            throw new InvalidOperationException("At least one Landlord party is required.");
        if (!parties.Any(p => p.Role == PartyRole.Tenant))
            throw new InvalidOperationException("At least one Tenant party is required.");

        var lease = new LeaseContract
        {
            PropertyId = propertyId,
            OrgId = property.OrgId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            MonthlyRent = request.MonthlyRent,
            SecurityDeposit = request.SecurityDeposit,
            ConcordatoAssessment = concordato,
            // No stipula yet: the RLI deadline is fixed when every party has signed (LT-04, A7-04).
            DataRetentionUntil = request.StartDate.AddYears(10),
            Parties = parties.Select(p => new Party
            {
                Role = p.Role,
                FirstName = p.FirstName,
                LastName = p.LastName,
                FiscalCode = p.FiscalCode,
                Citizenship = EuMemberStates.NormalizeCode(p.Citizenship),
                ContactEmail = p.ContactEmail,
                // Not an EU citizen (27 member states, EuMemberStates): the Questura communication applies (LT-07).
                IsExtraEU = !EuMemberStates.IsEuCitizenship(p.Citizenship)
            }).ToList()
        };
        lease.SetContractTerms(contractType, taxRegime);

        await leaseRepository.AddAsync(lease);
        await eventRepository.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.Created
        });

        if (concordato is { RentWithinRange: false })
        {
            // Only an indicative range (Partial data) lets a rent outside it through (A7-23): logged, shown to the host.
            logger.LogWarning(
                "Lease draft created with a rent outside the indicative canone concordato range ({Completeness} data). LeaseId={LeaseId} PropertyId={PropertyId}",
                concordato.DataCompleteness, lease.Id, propertyId);
        }

        logger.LogInformation("Lease draft created. LeaseId={LeaseId} PropertyId={PropertyId} ContractType={ContractType}",
            lease.Id, propertyId, contractType);
        return lease;
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

    /// <summary>
    /// Contract type and tax regime of the request (LT-10): the new fields, or the legacy combined value when a client
    /// sends only that. 422 when neither is given, or the contract type comes without a tax regime.
    /// </summary>
    private static (LeaseContractType Type, LeaseTaxRegime? TaxRegime) ResolveContractTerms(CreateLeaseRequest request)
    {
        if (request.ContractType is { } contractType)
        {
            var taxRegime = request.TaxRegime
                ?? throw new DomainRuleException(LeaseTermErrorCodes.TaxRegimeRequired, "LeaseTaxRegimeRequired");
            return (contractType, taxRegime);
        }

        if (request.FiscalRegime is { } legacy)
        {
            var (type, legacyTaxRegime) = LeaseContractTerms.FromLegacy(legacy);
            return (type, request.TaxRegime ?? legacyTaxRegime);
        }

        throw new DomainRuleException(LeaseTermErrorCodes.ContractTypeRequired, "LeaseContractTypeRequired");
    }

    /// <summary>
    /// Canone concordato range recomputed on the server from the declared characteristics and the real term (A7-12).
    /// Verified agreement data (Complete): a rent outside the range is refused (422). Unconfirmed data (Partial): the
    /// range is indicative, the lease is created and the assessment records that the rent is outside it (A7-23).
    /// </summary>
    private async Task<LeaseConcordatoAssessment> AssessConcordatoRentAsync(
        Guid propertyId, CreateLeaseRequest request, LeaseTerm term)
    {
        if (request.CanoneConcordatoCharacteristics is not { } characteristics)
            throw new DomainRuleException(ConcordatoErrorCodes.CharacteristicsRequired, "ConcordatoCharacteristicsRequired");

        var range = await canoneConcordatoEligibility.CalculateAsync(propertyId, characteristics, term)
            ?? throw new NotFoundException($"Property {propertyId} not found.") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        if (range is not
            {
                Available: true,
                SubFascia: int subFascia,
                CanoneMinAnnuo: decimal minAnnual,
                CanoneMaxAnnuo: decimal maxAnnual,
                CanoneMinMensile: decimal minMonthly,
                CanoneMaxMensile: decimal maxMonthly,
            })
        {
            var (code, key) = ConcordatoErrorCodes.ForReason(range.ReasonCode);
            throw new DomainRuleException(code, key);
        }

        var annualRent = request.MonthlyRent * 12m;
        var withinRange = annualRent >= minAnnual && annualRent <= maxAnnual;
        if (!withinRange && !range.Indicative)
        {
            throw new DomainRuleException(
                ConcordatoErrorCodes.RentOutOfRange, "ConcordatoRentOutOfRange", minMonthly, maxMonthly);
        }

        return new LeaseConcordatoAssessment
        {
            Sqm = characteristics.Sqm,
            GarageSqm = characteristics.GarageSqm,
            BalconySqm = characteristics.BalconySqm,
            OtherAppurtenanceSqm = characteristics.OtherAppurtenanceSqm,
            PrivateGreenSqm = characteristics.PrivateGreenSqm,
            TypeAElementCount = characteristics.TypeAElementCount,
            TypeBElementCount = characteristics.TypeBElementCount,
            TypeCElementCount = characteristics.TypeCElementCount,
            TypeDElementCount = characteristics.TypeDElementCount,
            QualifyingTypeDElementCount = characteristics.QualifyingTypeDElementCount,
            StoveHeating = characteristics.StoveHeating,
            IsFurnished = characteristics.IsFurnished,
            AirConditioning = characteristics.AirConditioning,
            ZoneName = NullIfBlank(characteristics.ZoneName),
            CadastralSheet = NullIfBlank(characteristics.CadastralSheet),
            ContractYears = range.ContractYears ?? term.Months / 12,
            UsableSqm = range.UsableSqm ?? characteristics.Sqm,
            Zone = range.Zone ?? string.Empty,
            SubFascia = subFascia,
            CanoneMinAnnuo = minAnnual,
            CanoneMaxAnnuo = maxAnnual,
            CanoneMinMensile = minMonthly,
            CanoneMaxMensile = maxMonthly,
            DataCompleteness = range.DataCompleteness ?? DataCompleteness.Missing,
            RentWithinRange = withinRange,
            CalculatedAt = _clock.GetUtcNow().UtcDateTime,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
