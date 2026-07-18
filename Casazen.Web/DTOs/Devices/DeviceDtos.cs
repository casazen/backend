using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Devices;

public class RegisterDeviceRequest
{
    [Required, MaxLength(16)]
    public string Platform { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string PushToken { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
}

public class DeviceRegistrationDto
{
    public Guid Id { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
