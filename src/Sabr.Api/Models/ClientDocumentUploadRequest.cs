using Microsoft.AspNetCore.Http;
using Sabr.Domain.Enums;

namespace Sabr.Api.Models;

public sealed class ClientDocumentUploadRequest
{
    public DocumentType DocumentType { get; set; }
    public IFormFile File { get; set; } = null!;
}
