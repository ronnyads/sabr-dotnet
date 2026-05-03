using Microsoft.AspNetCore.Http;

namespace Phub.Api.Models;

public sealed class ProductImageUploadRequest
{
    public IFormFile? File { get; set; }
}
