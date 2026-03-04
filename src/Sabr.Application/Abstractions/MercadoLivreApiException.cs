using System.Net;

namespace Sabr.Application.Abstractions;

public sealed class MercadoLivreApiException : Exception
{
    public MercadoLivreApiException(
        HttpStatusCode? statusCode,
        string? errorCode,
        string? errorMessage,
        string? rawBody = null,
        Exception? innerException = null)
        : base(errorMessage ?? "Mercado Livre API request failed.", innerException)
    {
        StatusCode = statusCode;
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        RawBody = rawBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public string? RawBody { get; }
}
