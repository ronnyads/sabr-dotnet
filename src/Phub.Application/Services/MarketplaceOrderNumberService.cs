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

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return await NextPostgresNumberAsync(cancellationToken);
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

    private async Task<long> NextPostgresNumberAsync(CancellationToken cancellationToken)
    {
        // Allocation and repair are one atomic statement. The previous read/update
        // implementation could leave the counter behind orders imported by another
        // path, and concurrent workers could then generate the same PHUB number.
        // The conflict branch always advances the locked counter by at least one and
        // also fast-forwards it past the greatest number already persisted.
        const string sql = """
            INSERT INTO marketplace_order_number_sequences (id, next_number, updated_at)
            VALUES (
                1,
                (SELECT COALESCE(MAX(CAST(SUBSTRING(internal_order_number FROM 6) AS bigint)), 0) + 2
                 FROM marketplace_orders
                 WHERE internal_order_number ~ '^PHUB-[0-9]+$'),
                NOW())
            ON CONFLICT (id) DO UPDATE
            SET next_number = GREATEST(
                    marketplace_order_number_sequences.next_number + 1,
                    (SELECT COALESCE(MAX(CAST(SUBSTRING(internal_order_number FROM 6) AS bigint)), 0) + 2
                     FROM marketplace_orders
                     WHERE internal_order_number ~ '^PHUB-[0-9]+$')),
                updated_at = NOW()
            RETURNING next_number - 1 AS "Value"
            """;

        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
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
