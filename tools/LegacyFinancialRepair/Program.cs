using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings__Default is required.");

var apply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString, options => options.CommandTimeout(180)).Options);
var ledger = new FinancialLedgerService(db);
var historical = new HistoricalProductCostService(db);
var projection = new OperationalFinancialProjectionService(db, ledger, historical);
var service = new LegacyFinancialCostRepairService(db, ledger, projection, historical);

var result = await service.RepairAllAsync(apply);
Console.WriteLine(JsonSerializer.Serialize(new { mode = apply ? "APPLY" : "DRY_RUN", result }));
