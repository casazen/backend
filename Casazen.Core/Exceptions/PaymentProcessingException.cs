namespace Casazen.Core.Exceptions;

public class PaymentProcessingException : Exception
{
    public PaymentProcessingException(string message) : base(message)
    {
    }

    public PaymentProcessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
