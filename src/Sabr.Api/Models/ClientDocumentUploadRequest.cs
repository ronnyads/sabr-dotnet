using Microsoft.AspNetCore.Http;
using Phub.Domain.Enums;

namespace Phub.Api.Models;

public sealed class ClientDocumentUploadRequest
{
    public DocumentType DocumentType { get; set; }
    public IFormFile File { get; set; } = null!;
}
