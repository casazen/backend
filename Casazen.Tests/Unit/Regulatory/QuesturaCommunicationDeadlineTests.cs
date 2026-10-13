using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// LT-07 (A7-08): who counts as extra-EU (27 member states, ISO codes) and the 48 hours of art. 7 D.Lgs. 286/1998 from
/// the delivery of the property (declared, or the start date by default), on the Rome calendar.
/// </summary>
public class QuesturaCommunicationDeadlineTests
{
    [Theory]
    [InlineData("IT", true)]
    [InlineData("de", true)]
    [InlineData(" fr ", true)]
    [InlineData("GR", true)]
    [InlineData("HR", true)]
    [InlineData("US", false)]
    [InlineData("GB", false)]
    [InlineData("CH", false)]
    [InlineData("NO", false)]
    [InlineData("EL", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEuCitizenship_IsoCode_OnlyTheMemberStates(string? code, bool expected)
    {
        Assert.Equal(expected, EuMemberStates.IsEuCitizenship(code));
    }

    [Fact]
    public void Codes_ExactlyTwentySevenMemberStates()
    {
        Assert.Equal(27, EuMemberStates.Codes.Count);
        Assert.All(EuMemberStates.Codes, code => Assert.Matches("^[A-Z]{2}$", code));
    }

    [Fact]
    public void DeliveryDate_NotDeclared_IsTheStartDate()
    {
        var lease = Lease(extraEu: true);

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), QuesturaCommunicationDeadline.DeliveryDate(lease));
        Assert.Equal(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), QuesturaCommunicationDeadline.Deadline(lease));
    }

    [Fact]
    public void Deadline_DeclaredDeliveryDate_TwoCalendarDaysLater()
    {
        var lease = Lease(extraEu: true);
        lease.PropertyDeliveryDate = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), QuesturaCommunicationDeadline.Deadline(lease));
    }

    [Fact]
    public void DaysUntil_LateEveningUtc_CountsOnTheRomeCalendar()
    {
        // 22:30 UTC of 1/10 is already 2/10 in Rome: one day to 3/10.
        var today = Casazen.Core.Utilities.RomeCalendar.TodayAt(new DateTimeOffset(2026, 10, 1, 22, 30, 0, TimeSpan.Zero));

        Assert.Equal(1, QuesturaCommunicationDeadline.DaysUntil(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), today));
    }

    [Theory]
    [InlineData(true, LeaseStatus.Draft, true)]
    [InlineData(true, LeaseStatus.Registered, true)]
    [InlineData(true, LeaseStatus.Rejected, false)]
    [InlineData(false, LeaseStatus.Signed, false)]
    public void IsRequired_ExtraEuTenantOnLeaseNotRejected(bool extraEu, LeaseStatus status, bool expected)
    {
        var lease = Lease(extraEu);
        lease.Status = status;

        Assert.Equal(expected, QuesturaCommunicationDeadline.IsRequired(lease));
    }

    private static LeaseContract Lease(bool extraEu) => new()
    {
        Status = LeaseStatus.Signed,
        StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 9, 30, 0, 0, 0, DateTimeKind.Utc),
        Parties =
        [
            new Party { Role = PartyRole.Landlord, Citizenship = "US", IsExtraEU = true },
            new Party { Role = PartyRole.Tenant, Citizenship = extraEu ? "US" : "IT", IsExtraEU = extraEu },
        ],
    };
}
