using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02 (NF-52): the services of the alert stages and of the monthly summary are registered, each scoped, each with its implementation. Every line of this
/// list is a change someone makes on purpose: the port that says who is told, the source of the tourist tax deadline day (which answers «not known» until the
/// plan decides where the day comes from), the follow-up of a stage, and the two services.
/// </summary>
public class ComplianceAlertRegistrationTests
{
    [Fact]
    public void AddCasazenServices_RegistersTheAlertStagesAndTheMonthlySummary_AllScoped()
    {
        var services = new ServiceCollection();

        services.AddCasazenServices();

        foreach (var (service, implementation) in new (Type, Type)[]
                 {
                     (typeof(IComplianceAlertRecipientResolver), typeof(ComplianceAlertRecipientResolver)),
                     (typeof(ITouristTaxDeadlineSource), typeof(NoTouristTaxDeadlineSource)),
                     (typeof(IComplianceAlertFollowUp), typeof(ComplianceAlertFollowUp)),
                     (typeof(IComplianceAlertService), typeof(ComplianceAlertService)),
                     (typeof(IStayAlertService), typeof(StayAlertService)),
                     (typeof(IComplianceMonthlySummaryService), typeof(ComplianceMonthlySummaryService)),
                     (typeof(Casazen.Web.BackgroundJobs.ComplianceMonthlySummaryJob), typeof(Casazen.Web.BackgroundJobs.ComplianceMonthlySummaryJob)),
                 })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, descriptor.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    [Fact]
    public async Task TheTouristTaxDeadlineSource_AnswersNotKnown_SoTheAlertStaysSilent()
    {
        var day = await new NoTouristTaxDeadlineSource().DeadlineOfAsync(Guid.NewGuid(), new DateOnly(2026, 10, 1), CancellationToken.None);

        Assert.Null(day);
    }
}
