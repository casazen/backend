using System.ComponentModel.DataAnnotations;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

public class UpdatePropertyCinRequest
{
    // Raw input may contain spaces or hyphens; the stored (normalized) CIN is at most 18 characters.
    [MaxLength(40, ErrorMessage = CinFormat.InvalidFormatMessageKey)]
    [CinCode]
    public string? CinCode { get; set; }
}
