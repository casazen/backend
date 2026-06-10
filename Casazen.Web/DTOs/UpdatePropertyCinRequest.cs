using System.ComponentModel.DataAnnotations;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs;

public class UpdatePropertyCinRequest
{
    [CinCode]
    [MaxLength(25)]
    public string? CinCode { get; set; }
}
