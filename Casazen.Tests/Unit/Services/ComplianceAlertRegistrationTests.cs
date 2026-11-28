using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

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
                     (typeof(ITicinoNotificationService), typeof(TicinoNotificationService)),
                     (typeof(ISwissNotificationService), typeof(SwissNotificationService)),
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
