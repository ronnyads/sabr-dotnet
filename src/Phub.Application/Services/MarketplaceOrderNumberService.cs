using System.Data;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class MarketplaceOrderNumberService
{
    public const string Prefix = "PHUB-";

    private readonly IAppDbContext _dbContext;

    public MarketplaceOrderNumberService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string> EnsureOrderNumberAsync(MarketplaceOrder order, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(order.InternalOrderNumber))
        {
            return order.InternalOrderNumber;
        }

        var nextNumber = await NextNumberAsync(cancellationToken);
        order.InternalOrderNumber = Format(nextNumber);
        return order.InternalOrderNumber;
    }

    public static string Format(long sequence)
        => $"{Prefix}{sequence:D8}";

    public static long? ParseSequence(string? internalOrderNumber)
    {
        if (string.IsNullOrWhiteSpace(internalOrderNumber))
        {
            return null;
        }

        var normalized = internalOrderNumber.Trim();
        if (!normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(normalized[Prefix.Length..], out var value)
            ? value
            : null;
    }

    private async Task<long> NextNumberAsync(CancellationToken cancellationToken)
    {
        var providerName = _dbContext.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            return await NextNumberWithoutTransactionAsync(cancellationToken);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var sequence = await _dbContext.MarketplaceOrderNumberSequences
            .FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);

        if (sequence == null)
        {
            sequence = new MarketplaceOrderNumberSequence
            {
                Id = 1,
                NextNumber = 2,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.MarketplaceOrderNumberSequences.Add(sequence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return 1;
        }

        var value = Math.Max(1, sequence.NextNumber);
        sequence.NextNumber = value + 1;
        sequence.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value;
    }

    private async Task<long> NextNumberWithoutTransactionAsync(CancellationToken cancellationToken)
    {
        var sequence = await _dbContext.MarketplaceOrderNumberSequences
            .FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);

        if (sequence == null)
        {
            sequence = new MarketplaceOrderNumberSequence
            {
                Id = 1,
                NextNumber = 2,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.MarketplaceOrderNumberSequences.Add(sequence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 1;
        }

        var value = Math.Max(1, sequence.NextNumber);
        sequence.NextNumber = value + 1;
        sequence.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return value;
    }
}
