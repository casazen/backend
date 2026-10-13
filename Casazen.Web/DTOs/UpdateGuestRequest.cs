using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

public class UpdateGuestRequest
{
    [Required(ErrorMessage = "FirstNameRequired")]
    [MaxLength(100, ErrorMessage = "FirstNameTooLong")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "LastNameRequired")]
    [MaxLength(100, ErrorMessage = "LastNameTooLong")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "EmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "EmailTooLong")]
    public string Email { get; set; } = string.Empty;

    [Phone(ErrorMessage = "InvalidPhone")]
    [MaxLength(20, ErrorMessage = "PhoneTooLong")]
    public string? PhoneNumber { get; set; }

    [MaxLength(500, ErrorMessage = "AddressTooLong")]
    public string? Address { get; set; }

    [MaxLength(50, ErrorMessage = "CityTooLong")]
    public string? City { get; set; }

    [MaxLength(10, ErrorMessage = "PostalCodeTooLong")]
    public string? PostalCode { get; set; }

    [MaxLength(100, ErrorMessage = "CountryTooLong")]
    public string? Country { get; set; }

    [MaxLength(1000, ErrorMessage = "NotesTooLong")]
    public string? Notes { get; set; }
}
