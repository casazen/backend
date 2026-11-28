using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

public class UserRoleTests
{
    [Fact]
    public void UserRole_ExistingValues_AreUnchangedAndNoneIsAppended()
    {
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
