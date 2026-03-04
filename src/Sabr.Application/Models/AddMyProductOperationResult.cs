namespace Sabr.Application.Models;

public sealed class AddMyProductOperationResult
{
    public bool Created { get; set; }
    public MyProductDraftResult Draft { get; set; } = new();
    public bool FromIdempotencyCache { get; set; }
}
