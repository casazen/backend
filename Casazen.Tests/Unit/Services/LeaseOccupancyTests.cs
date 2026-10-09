using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Repositories;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Repositories;
using Xunit;
using static Casazen.Tests.Unit.Services.PropertyModeHarness;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PC-05 and PM-02 (D16): the leases that tie a property up are defined once, <see cref="LeaseOccupancy"/>. The rule that
/// refuses to delete a property (<see cref="PropertyRepository.SoftDeleteAsync"/>, asked from today) and the one that
/// refuses to bring a long-term property back to short stays (asked from the day chosen) give the same answers.
/// </summary>
public class LeaseOccupancyTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData(LeaseStatus.Draft, 5, false)]
    [InlineData(LeaseStatus.Rejected, 5, false)]
    [InlineData(LeaseStatus.AwaitingSignature, 5, true)]
    [InlineData(LeaseStatus.PartiallySigned, 5, true)]
    [InlineData(LeaseStatus.Signed, 5, true)]
    [InlineData(LeaseStatus.RegistrationPending, 5, true)]
    [InlineData(LeaseStatus.SentToProvider, 5, true)]
    [InlineData(LeaseStatus.Registered, 5, true)]
    // The last day counts: a lease that ends on the day asked for still ties the property that day.
    [InlineData(LeaseStatus.Registered, 0, true)]
    [InlineData(LeaseStatus.Registered, -1, false)]
    [InlineData(LeaseStatus.Registered, -300, false)]
    public void RunsOnOrAfter_FromTheStartOfADayOfRome_FollowsStatusAndLastDay(LeaseStatus status, int endsAfterDay, bool ties)
    {
        var propertyId = Guid.NewGuid();
        var day = Day(20);
        var lease = new LeaseContract { PropertyId = propertyId, Status = status, EndDate = day.AddDays(endsAfterDay) };

        var rule = LeaseOccupancy.RunsOnOrAfter(propertyId, RomeCalendar.StartOfDayUtc(day)).Compile();

        Assert.Equal(ties, rule(lease));
    }

    [Fact]
    public void RunsOnOrAfter_ALeaseOfAnotherProperty_NeverTies()
    {
        var lease = new LeaseContract { PropertyId = Guid.NewGuid(), Status = LeaseStatus.Registered, EndDate = Day(400) };

        Assert.False(LeaseOccupancy.RunsOnOrAfter(Guid.NewGuid(), RomeCalendar.StartOfDayUtc(Day(20))).Compile()(lease));
    }

    [Theory]
    [InlineData(LeaseStatus.Registered, 0, PropertySoftDeleteOutcome.HasActiveLeases)]
    [InlineData(LeaseStatus.Signed, 30, PropertySoftDeleteOutcome.HasActiveLeases)]
    [InlineData(LeaseStatus.Registered, -1, PropertySoftDeleteOutcome.Deleted)]
    [InlineData(LeaseStatus.Draft, 30, PropertySoftDeleteOutcome.Deleted)]
    [InlineData(LeaseStatus.Rejected, 30, PropertySoftDeleteOutcome.Deleted)]
    public async Task SoftDelete_AsksTheSameRuleFromToday(LeaseStatus status, int endsAfterToday, PropertySoftDeleteOutcome expected)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedLeaseAsync(property, Today.AddDays(-400), Today.AddDays(endsAfterToday), status);

        var outcome = await new PropertyRepository(_h.Db).SoftDeleteAsync(property.Id, Now.UtcDateTime, Today);

        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData(LeaseStatus.Registered, 0, 1)]
    [InlineData(LeaseStatus.Signed, 30, 31)]
    [InlineData(LeaseStatus.Registered, -1, 1)]
    public async Task ChangeOfMode_AsksItFromTheDayChosen_FreeTheDayAfterTheEnd(LeaseStatus status, int endsAfterToday, int earliestAfterToday)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        await _h.SeedLeaseAsync(property, Today.AddDays(-400), Today.AddDays(endsAfterToday), status);

        var preview = await _h.Service.PreviewAsync(property.Id, RentalMode.Short);

        Assert.Equal(Today.AddDays(earliestAfterToday), preview.EarliestDate);
    }
}
