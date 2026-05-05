using System.Net;

namespace Phub.Application.Abstractions;

public sealed class TikTokShopApiException : Exception
{
    public TikTokShopApiException(
        HttpStatusCode? statusCode,
        string? apiCode,
        string? apiMessage,
        string? requestId = null,
        string? rawBody = null,
        string? operation = null,
        Exception? innerException = null)
        : base(apiMessage ?? "TikTok Shop API request failed.", innerException)
    {
        StatusCode = statusCode;
        ApiCode = string.IsNullOrWhiteSpace(apiCode) ? null : apiCode.Trim();
        ApiMessage = string.IsNullOrWhiteSpace(apiMessage) ? null : apiMessage.Trim();
        RequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        RawBody = rawBody;
        Operation = string.IsNullOrWhiteSpace(operation) ? null : operation.Trim();
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ApiCode { get; }

    public string? ApiMessage { get; }

    public string? RequestId { get; }

    public string? RawBody { get; }

    public string? Operation { get; }
}
