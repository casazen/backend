using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

/// <summary>PL-02: <see cref="UserRole.None"/> appended to the enum stored as integers, and default of a new user.</summary>
public class UserRoleTests
{
    [Fact]
    public void UserRole_ExistingValues_AreUnchangedAndNoneIsAppended()
    {
        // Stored as integers: a value inserted before the existing ones would change the meaning of every row.
        Assert.Equal(0, (int)UserRole.Admin);
        Assert.Equal(1, (int)UserRole.PropertyOwner);
        Assert.Equal(2, (int)UserRole.PropertyManager);
        Assert.Equal(3, (int)UserRole.Guest);
        Assert.Equal(4, (int)UserRole.Staff);
        Assert.Equal(5, (int)UserRole.LongTermLandlord);
        Assert.Equal(6, (int)UserRole.Supplier);
        Assert.Equal(7, (int)UserRole.None);
        Assert.Equal(UserRole.None, Enum.GetValues<UserRole>().Max());
    }

    [Fact]
    public void User_New_HasNoRole()
    {
        Assert.Equal(UserRole.None, new User().Role);
    }
}
