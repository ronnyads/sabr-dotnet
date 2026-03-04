using Sabr.Domain.Common;

namespace Sabr.Domain.Entities;

public sealed class ClientStore : EntityBase
{
    public Guid ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public string StoreCode { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; } = true;
}
