namespace Phub.Application.Exceptions;

public sealed class ExternalServiceUnavailableException : Exception
{
    public ExternalServiceUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}
