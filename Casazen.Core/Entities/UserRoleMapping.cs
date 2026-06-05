namespace Casazen.Core.Entities;

public static class UserRoleMapping
{
  private static readonly UserRole[] PrimaryRolePriority =
  [
      UserRole.Admin,
      UserRole.PropertyManager,
      UserRole.PropertyOwner,
      UserRole.LongTermLandlord,
      UserRole.Staff,
      UserRole.Guest,
  ];

  public static IReadOnlyList<string> GetAssignedRoleNames(User user)
  {
      if (!string.IsNullOrWhiteSpace(user.AssignedRolesCsv))
      {
          return user.AssignedRolesCsv
              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
              .ToArray();
      }

      return DeriveFromLegacyFields(user);
  }

  public static IReadOnlyList<UserRole> ParseRoles(IEnumerable<string> roleNames)
  {
      var parsed = new List<UserRole>();
      foreach (var name in roleNames)
      {
          if (Enum.TryParse<UserRole>(name, ignoreCase: true, out var role))
              parsed.Add(role);
      }

      return parsed.Distinct().ToList();
  }

  public static UserRole PickPrimaryRole(IReadOnlyList<UserRole> roles)
  {
      foreach (var candidate in PrimaryRolePriority)
      {
          if (roles.Contains(candidate))
              return candidate;
      }

      return roles[0];
  }

  public static RentalType? DeriveRentalType(IReadOnlyList<UserRole> roles)
  {
      var hasOwner = roles.Contains(UserRole.PropertyOwner);
      var hasLandlord = roles.Contains(UserRole.LongTermLandlord);

      if (hasOwner && hasLandlord)
          return RentalType.Both;
      if (hasOwner)
          return RentalType.ShortTerm;
      if (hasLandlord)
          return RentalType.LongTerm;

      return null;
  }

  public static string ToCsv(IReadOnlyList<UserRole> roles) =>
      string.Join(',', roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));

  private static IReadOnlyList<string> DeriveFromLegacyFields(User user)
  {
      if (user.RentalType == RentalType.Both)
          return ["PropertyOwner", "LongTermLandlord"];

      if (user.RentalType == RentalType.ShortTerm)
          return ["PropertyOwner"];

      if (user.RentalType == RentalType.LongTerm)
          return ["LongTermLandlord"];

      return [user.Role.ToString()];
  }
}
