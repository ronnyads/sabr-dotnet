namespace Sabr.Application.Models;

public sealed class ApiError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Errors { get; set; }
    public string TraceId { get; set; } = string.Empty;
}
