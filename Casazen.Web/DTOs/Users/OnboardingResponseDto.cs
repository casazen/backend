namespace Casazen.Web.DTOs.Users;

public class OnboardingResponseDto
{
    public string[] RolesAssigned { get; set; } = [];
    public string RentalType { get; set; } = string.Empty;
}
