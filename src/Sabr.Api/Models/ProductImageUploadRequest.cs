using Microsoft.AspNetCore.Http;

namespace Sabr.Api.Models;

public sealed class ProductImageUploadRequest
{
    public IFormFile? File { get; set; }
}
