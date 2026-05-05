namespace Casazen.Core.Exceptions;

/// <summary>
/// Thrown when domain validation fails. Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public class ValidationException : Exception
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string message) : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string field, string error) : base(error)
    {
        Errors = new Dictionary<string, string[]>
        {
            [field] = [error]
        };
    }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }
}
