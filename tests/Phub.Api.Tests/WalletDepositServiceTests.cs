using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class WalletDepositServiceTests
{
    [Fact]
    public async Task CreateAsync_StoresPendingProofAndKeepsTenantIsolation()
    {
        await using var db = CreateDb();
        var client = new Client { TenantId = "tenant-a", AccountName = "Isabela", Email = "isabela@example.test", Status = ClientStatus.Approved };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var service = new WalletDepositService(db, new WalletService(db));

        var created = await service.CreateAsync("tenant-a", client.Id, 125_50, "TED", "comprovante.pdf", "application/pdf", "%PDF-test"u8.ToArray(), default);
        var wallet = await service.GetClientWalletAsync("tenant-a", client.Id, 20, default);
        var otherTenant = await service.GetClientWalletAsync("tenant-b", client.Id, 20, default);

        Assert.True(created.Succeeded);
        Assert.Equal("Pending", created.Data!.Status);
        Assert.Equal(125_50, wallet.PendingDepositCents);
        Assert.Single(wallet.Deposits);
        Assert.Empty(otherTenant.Deposits);
        Assert.NotNull(await db.WalletDepositProofs.SingleOrDefaultAsync(x => x.DepositRequestId == created.Data.Id));
    }

    [Fact]
    public async Task CreateAsync_RejectsWalletForUnapprovedClient()
    {
        await using var db = CreateDb();
        var client = new Client { TenantId = "tenant-a", AccountName = "Pendente", Email = "pending@example.test", Status = ClientStatus.PendingDocuments };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var service = new WalletDepositService(db, new WalletService(db));

        var result = await service.CreateAsync("tenant-a", client.Id, 100, null, "proof.pdf", "application/pdf", "%PDF-test"u8.ToArray(), default);

        Assert.False(result.Succeeded);
        Assert.Empty(db.WalletDepositRequests);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"wallet-deposits-{Guid.NewGuid():N}").Options);
}
