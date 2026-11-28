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
    private readonly LeaseConcordatoAssessor _concordato = new(canoneConcordatoEligibility, timeProvider ?? TimeProvider.System);

    public async Task<LeaseContract> CreateDraftAsync(Guid propertyId, CreateLeaseRequest request)
    {
        var property = await propertyRepository.GetByIdAsync(propertyId)
            ?? throw new InvalidOperationException($"Property {propertyId} not found.");

        await apeCompliance.EnsurePropertyHasValidApeAsync(propertyId);

        var (contractType, taxRegime) = ResolveContractTerms(request);

        LeaseContractTerms.EnsureTerm(contractType, request.StartDate, request.EndDate);
        var term = LeaseTerm.Between(request.StartDate, request.EndDate)!.Value;

        var concordato = contractType == LeaseContractType.Concordato
            ? await AssessConcordatoRentAsync(propertyId, request, term)
            : null;

        var parties = request.Parties.ToList();
        LeasePartyRules.Ensure(parties.Select(p => (p.Role, (string?)p.FiscalCode)).ToList());

        var lease = new LeaseContract
        {
            PropertyId = propertyId,
            OrgId = property.OrgId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            MonthlyRent = request.MonthlyRent,
            SecurityDeposit = request.SecurityDeposit,
            ConcordatoAssessment = concordato,
            WizardMaxStep = LeaseDraftDefaults.LastDataStep,
            // No stipula yet: the RLI deadline is fixed when every party has signed (LT-04, A7-04). No retention date
            // either: it is computed from the end date and Gdpr:Retention:LeaseParties (LT-12, LeasePartyPrivacyService).
            Parties = parties.Select((p, index) => new Party
            {
                Role = p.Role,
                Position = parties.Take(index).Count(previous => previous.Role == p.Role),
                FirstName = p.FirstName,
                LastName = p.LastName,
                FiscalCode = ItalianFiscalCode.Normalize(p.FiscalCode),
                Citizenship = EuMemberStates.NormalizeCode(p.Citizenship),
                ContactEmail = p.ContactEmail,
                ContactPhone = LeaseDraftRules.NormalizeContactPhone(p.ContactPhone),
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
        var today = _clock.TodayInRome();
        var summaries = await leaseRepository.GetSummariesAsync(scope, propertyId);
        return WithResolvedDeadline(summaries, today);
    }

    public async Task<IReadOnlyList<LeaseSummaryDto>> GetLeasesAsync(HostScope scope, LeaseListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var today = _clock.TodayInRome();
        var summaries = await leaseRepository.GetSummariesAsync(scope, query, today);
        return WithResolvedDeadline(summaries, today);
    }

    private static IReadOnlyList<LeaseSummaryDto> WithResolvedDeadline(IReadOnlyList<LeaseSummaryDto> summaries, DateTime today) =>
        summaries
            .Select(s => s with
            {
                RegistrationDeadline = RliRegistrationDeadline.Resolve(s.Status, s.StipulaDate, s.StartDate, today),
            })
            .ToList();

    public Task<LeaseContract?> GetLeaseDetailAsync(Guid leaseId)
        => leaseRepository.GetByIdWithDetailsAsync(leaseId);

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

    private async Task<LeaseConcordatoAssessment> AssessConcordatoRentAsync(
        Guid propertyId, CreateLeaseRequest request, LeaseTerm term)
    {
        if (request.CanoneConcordatoCharacteristics is not { } characteristics)
            throw new DomainRuleException(ConcordatoErrorCodes.CharacteristicsRequired, "ConcordatoCharacteristicsRequired");

        var result = await _concordato.AssessAsync(propertyId, characteristics, term, request.MonthlyRent);
        switch (result.Kind)
        {
            case ConcordatoAssessmentKind.PropertyNotFound:
                throw new NotFoundException($"Property {propertyId} not found.") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
            case ConcordatoAssessmentKind.Unavailable:
                var (code, key) = ConcordatoErrorCodes.ForReason(result.Reason);
                throw new DomainRuleException(code, key);
        }

        if (!result.RentWithinRange && !result.Indicative)
        {
            throw new DomainRuleException(
                ConcordatoErrorCodes.RentOutOfRange, "ConcordatoRentOutOfRange", result.MinMonthly!.Value, result.MaxMonthly!.Value);
        }

        return result.Assessment!;
    }
}
